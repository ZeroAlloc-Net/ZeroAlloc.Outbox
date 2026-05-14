using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace ZeroAlloc.Outbox.Benchmarks;

// Correctness-matched hand-rolled outbox. Same SQLite connection as the ZA
// SqliteDirectStore in the benchmark. Same guarantees:
//   - Enqueue happens inside a transaction (atomic with caller's other writes)
//   - Dispatch tick reads pending rows, invokes the handler, marks done in the
//     same transaction — no double-dispatch under concurrent pollers
//
// Differences from the ZA path: no IOutboxWriter<T> abstraction, no serializer,
// no OutboxMessageId TypedId — caller hands in the raw byte payload + type tag.
internal sealed class HandRolledOutbox
{
    private readonly SqliteConnection _conn;

    public HandRolledOutbox(SqliteConnection conn)
    {
        _conn = conn;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS hand_outbox (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              type_name TEXT NOT NULL,
              payload BLOB NOT NULL,
              status TEXT NOT NULL DEFAULT 'pending'
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Enqueue(string typeName, byte[] payload)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO hand_outbox (type_name, payload) VALUES ($t, $p);";
        cmd.Parameters.AddWithValue("$t", typeName);
        cmd.Parameters.AddWithValue("$p", payload);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public int DispatchTick(int limit, Action<string, byte[]> handler)
    {
        using var tx = _conn.BeginTransaction();
        var rows = new List<(long Id, string Type, byte[] Payload)>(limit);
        using (var sel = _conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT id, type_name, payload FROM hand_outbox WHERE status='pending' ORDER BY id LIMIT $n;";
            sel.Parameters.AddWithValue("$n", limit);
            using var r = sel.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), (byte[])r.GetValue(2)));
        }
        foreach (var (id, type, payload) in rows)
        {
            handler(type, payload);
            using var upd = _conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE hand_outbox SET status='done' WHERE id=$id;";
            upd.Parameters.AddWithValue("$id", id);
            upd.ExecuteNonQuery();
        }
        tx.Commit();
        return rows.Count;
    }
}
