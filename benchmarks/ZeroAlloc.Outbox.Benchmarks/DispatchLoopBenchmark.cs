using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ZeroAlloc.Outbox.Benchmarks;

// Measures one poll of OutboxWorkerService, the dispatch loop itself: an idle poll that claims
// nothing, and a poll that claims and dispatches a batch of 50. The store and the dispatcher
// complete synchronously and allocate nothing, so every byte reported is the worker's own: the
// DI scope of the poll, and for a batch the dispatchers it resolves.
[MemoryDiagnoser]
[SimpleJob]
public class DispatchLoopBenchmark
{
    private const string TypeName = "Bench.Ping";

    private ServiceProvider _idleProvider = null!;
    private ServiceProvider _batchProvider = null!;
    private OutboxWorkerService _idle = null!;
    private OutboxWorkerService _batch = null!;

    [GlobalSetup]
    public void Setup()
    {
        (_idleProvider, _idle) = CreateWorker(new ReplayingStore(0));
        (_batchProvider, _batch) = CreateWorker(new ReplayingStore(50));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _idleProvider.Dispose();
        _batchProvider.Dispose();
    }

    [Benchmark(Description = "Idle poll")]
    public Task IdlePoll() => _idle.ProcessBatchAsync(CancellationToken.None);

    [Benchmark(Description = "Poll dispatching 50")]
    public Task Batch() => _batch.ProcessBatchAsync(CancellationToken.None);

    private static (ServiceProvider, OutboxWorkerService) CreateWorker(IOutboxStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddTransient<IOutboxTypeDispatcher, NoOpDispatcher>();
        var provider = services.BuildServiceProvider();
        var worker = new OutboxWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboxOptions { HostId = "benchmark", BatchSize = 50 }),
            NullLogger<OutboxWorkerService>.Instance);
        return (provider, worker);
    }

    private sealed class NoOpDispatcher : IOutboxTypeDispatcher
    {
        public string TypeName => DispatchLoopBenchmark.TypeName;

        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>Hands out the same claimed batch on every poll; every call succeeds synchronously.</summary>
    private sealed class ReplayingStore : IOutboxStore
    {
        private readonly IReadOnlyList<OutboxEntry> _batch;

        public ReplayingStore(int count)
        {
            var batch = new List<OutboxEntry>(count);
            for (var i = 0; i < count; i++)
            {
                batch.Add(new OutboxEntry
                {
                    Id = OutboxMessageId.New(),
                    TypeName = DispatchLoopBenchmark.TypeName,
                    RawPayload = [1],
                    RetryCount = 0,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                });
            }

            _batch = batch.AsReadOnly();
        }

        public ValueTask EnqueueAsync(
            string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)
            => new(_batch);

        public ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct) => new(true);

        public ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
            => new(0);

        public ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct) => new(true);

        public ValueTask<bool> MarkFailedAsync(
            OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
            => new(true);

        public ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
            => new(true);
    }
}
