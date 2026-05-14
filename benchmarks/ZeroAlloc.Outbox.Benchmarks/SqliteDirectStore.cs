using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.Benchmarks;

// IOutboxStore implementation that talks directly to SQLite via Microsoft.Data.Sqlite.
// Used only by HandRolledOverheadBenchmark — the ZA row of the comparison runs against
// THIS store while the HandRolledOutbox row uses a parallel SQLite table via raw SQL,
// against the SAME connection. Storage variance cancels out of the delta so the
// remaining number is the ZA abstraction cost on top of the same INSERT/SELECT/UPDATE.
internal sealed class SqliteDirectStore : IOutboxStore
{
    private readonly SqliteConnection _conn;

    public SqliteDirectStore(SqliteConnection conn)
    {
        _conn = conn;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS za_outbox (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              type_name TEXT NOT NULL,
              payload BLOB NOT NULL,
              status TEXT NOT NULL DEFAULT 'pending',
              retry_count INTEGER NOT NULL DEFAULT 0,
              next_retry_at TEXT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
    {
        // When caller supplies a transaction we enlist in it; otherwise we create our own
        // so the enqueue is durably committed before the call returns (matches the
        // hand-rolled baseline's per-call transaction shape — correctness-matched).
        SqliteTransaction? ownedTx = null;
        var tx = (SqliteTransaction?)transaction;
        if (tx is null)
        {
            ownedTx = _conn.BeginTransaction();
            tx = ownedTx;
        }
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO za_outbox (type_name, payload) VALUES ($t, $p);";
            cmd.Parameters.AddWithValue("$t", typeName);
            cmd.Parameters.AddWithValue("$p", payload.ToArray());
            cmd.ExecuteNonQuery();
            ownedTx?.Commit();
        }
        finally
        {
            ownedTx?.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    // Maps generated OutboxMessageId values to the SQLite rowid for the duration
    // of a fetch→mark cycle. Cleared on each fetch.
    private readonly Dictionary<OutboxMessageId, long> _idToRowid = new();

    public ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct)
    {
        var list = new List<OutboxEntry>(batchSize);
        _idToRowid.Clear();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, type_name, payload FROM za_outbox WHERE status='pending' ORDER BY id LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", batchSize);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rowid = r.GetInt64(0);
            var type = r.GetString(1);
            var payload = (byte[])r.GetValue(2);
            var msgId = OutboxMessageId.New();
            _idToRowid[msgId] = rowid;
            list.Add(new OutboxEntry
            {
                Id = msgId,
                TypeName = type,
                RawPayload = payload,
                RetryCount = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(list);
    }

    public ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct)
    {
        if (!_idToRowid.TryGetValue(id, out var rowid))
            return ValueTask.CompletedTask;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE za_outbox SET status='done' WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", rowid);
        cmd.ExecuteNonQuery();
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct)
        => ValueTask.CompletedTask; // not exercised by this benchmark

    public ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct)
        => ValueTask.CompletedTask; // not exercised by this benchmark
}
