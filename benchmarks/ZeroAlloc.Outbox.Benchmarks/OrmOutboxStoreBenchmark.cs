using System;
using System.Data.Async;
using System.Data.Async.Adapters;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;

namespace ZeroAlloc.Outbox.Benchmarks;

// Measures OrmOutboxStore, the production store, against SQLite in memory: an enqueue on each of
// its three paths, and a full claim of a batch with every message marked for retry. The numbers
// include SQLite and ADO.NET; what the store itself adds shows in the allocation column against a
// payload of known size.
[MemoryDiagnoser]
[SimpleJob]
public class OrmOutboxStoreBenchmark
{
    private const int BatchSize = 50;
    private static readonly byte[] s_payload = new byte[256];
    private static readonly OutboxLease s_lease = new("benchmark", TimeSpan.FromMinutes(1));

    private SqliteConnection _raw = null!;
    private IAsyncDbConnection _connection = null!;
    private OrmOutboxStore _store = null!;

    [GlobalSetup(Targets = [nameof(Enqueue), nameof(EnqueueInTransaction), nameof(EnqueueInDbTransaction)])]
    public void Setup()
    {
        _raw = new SqliteConnection("Data Source=:memory:");
        _raw.Open();
        _connection = _raw.AsAsync();
        new MigrationRunner(_connection, OutboxOrmMigrations.Sqlite, new SqliteMigrationDialect())
            .RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        _store = new OrmOutboxStore(_connection);
    }

    [GlobalCleanup]
    public void Cleanup() => _raw.Dispose();

    [Benchmark(Description = "Enqueue, no transaction")]
    public ValueTask Enqueue()
        => _store.EnqueueAsync("Bench.Cmd", s_payload, transaction: null, CancellationToken.None);

    [Benchmark(Description = "Enqueue in an IAsyncDbTransaction")]
    public async Task EnqueueInTransaction()
    {
        var tx = await _connection.BeginTransactionAsync().ConfigureAwait(false);
        await using (tx.ConfigureAwait(false))
        {
            await _store.EnqueueInTransactionAsync("Bench.Cmd", s_payload, tx, CancellationToken.None)
                .ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        }
    }

    [Benchmark(Description = "Enqueue in a DbTransaction")]
    public async Task EnqueueInDbTransaction()
    {
        DbTransaction tx = await _raw.BeginTransactionAsync().ConfigureAwait(false);
        await using (tx.ConfigureAwait(false))
        {
            await _store.EnqueueAsync("Bench.Cmd", s_payload, tx, CancellationToken.None).ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        }
    }

    [GlobalSetup(Target = nameof(ClaimAndMarkBatch))]
    public void SetupBatch()
    {
        Setup();
        for (var i = 0; i < BatchSize; i++)
            Enqueue().AsTask().GetAwaiter().GetResult();
    }

    // Each claimed message is marked failed with a retry time already past, which puts it back
    // to pending, so every invocation claims the same full batch of 50.
    [Benchmark(Description = "Claim 50 and mark each for retry")]
    public async Task ClaimAndMarkBatch()
    {
        var batch = await _store.ClaimPendingAsync(BatchSize, s_lease, CancellationToken.None).ConfigureAwait(false);
        if (batch.Count != BatchSize)
            throw new InvalidOperationException($"Claimed {batch.Count} messages, expected {BatchSize}.");

        var due = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < batch.Count; i++)
            await _store.MarkFailedAsync(batch[i].Id, 1, due, s_lease, CancellationToken.None).ConfigureAwait(false);
    }
}
