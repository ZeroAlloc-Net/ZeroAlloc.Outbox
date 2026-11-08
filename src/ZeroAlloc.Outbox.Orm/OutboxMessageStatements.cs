using System.Data.Async;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// Runs the <see cref="OutboxSql"/> statements over an <see cref="IAsyncDbConnection"/>, binding
/// parameters by hand. Nothing reflects at runtime.
/// </summary>
/// <remarks>
/// Each call opens the connection if it is closed and closes it again afterwards, so a connection
/// that was already open is left open. Values are bound as they are; the DateTimeOffset values the
/// store passes are already UTC.
/// </remarks>
internal sealed class OutboxMessageStatements(IAsyncDbConnection connection, OutboxSql sql)
{
    public Task<int> InsertAsync(
        Guid id,
        string typeName,
        byte[] payload,
        int status,
        DateTimeOffset nextRetryAt,
        DateTimeOffset createdAt,
        CancellationToken ct)
        => InsertCoreAsync(null, id, typeName, payload, status, nextRetryAt, createdAt, ct);

    /// <summary>
    /// Same insert, enlisted in a transaction the caller owns.
    /// </summary>
    public Task<int> InsertInTransactionAsync(
        Guid id,
        string typeName,
        byte[] payload,
        int status,
        DateTimeOffset nextRetryAt,
        DateTimeOffset createdAt,
        IAsyncDbTransaction tx,
        CancellationToken ct)
        => InsertCoreAsync(tx, id, typeName, payload, status, nextRetryAt, createdAt, ct);

    public async Task<int> MarkProcessedAsync(
        Guid id, int status, DateTimeOffset processedAt, int pending, string hostId, CancellationToken ct)
    {
        var conn = connection;
        var openedHere = await OpenAsync(conn, ct).ConfigureAwait(false);
        try
        {
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql.MarkProcessed;
                Add(cmd, "@id", id);
                Add(cmd, "@status", status);
                Add(cmd, "@processedAt", processedAt);
                Add(cmd, "@pending", pending);
                Add(cmd, "@hostId", hostId);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (openedHere) await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    public async Task<int> MarkForRetryAsync(
        Guid id, int retryCount, DateTimeOffset nextRetryAt, int pending, string hostId, CancellationToken ct)
    {
        var conn = connection;
        var openedHere = await OpenAsync(conn, ct).ConfigureAwait(false);
        try
        {
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql.MarkForRetry;
                Add(cmd, "@id", id);
                Add(cmd, "@retryCount", retryCount);
                Add(cmd, "@nextRetryAt", nextRetryAt);
                Add(cmd, "@pending", pending);
                Add(cmd, "@hostId", hostId);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (openedHere) await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    public async Task<int> DeadLetterAsync(
        Guid id, int status, string error, DateTimeOffset processedAt, int pending, string hostId,
        CancellationToken ct)
    {
        var conn = connection;
        var openedHere = await OpenAsync(conn, ct).ConfigureAwait(false);
        try
        {
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql.DeadLetter;
                Add(cmd, "@id", id);
                Add(cmd, "@status", status);
                Add(cmd, "@error", error);
                Add(cmd, "@processedAt", processedAt);
                Add(cmd, "@pending", pending);
                Add(cmd, "@hostId", hostId);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (openedHere) await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gives up this host's lease on a pending row. A row leased by another host, or no longer
    /// pending, is left alone.
    /// </summary>
    public async Task<int> ReleaseLeaseAsync(Guid id, string hostId, int pending, CancellationToken ct)
    {
        var conn = connection;
        var openedHere = await OpenAsync(conn, ct).ConfigureAwait(false);
        try
        {
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql.ReleaseLease;
                Add(cmd, "@id", id);
                Add(cmd, "@hostId", hostId);
                Add(cmd, "@pending", pending);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (openedHere) await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Extends a lease the host still holds. An expired lease is not renewed, even for the host
    /// that took it, because another host may already be entitled to claim the row.
    /// </summary>
    /// <returns>The number of rows changed: 1 if renewed, 0 if the lease was lost.</returns>
    public async Task<int> RenewLeaseAsync(
        Guid id, string hostId, int status, DateTimeOffset now, DateTimeOffset until, CancellationToken ct)
    {
        var conn = connection;
        var openedHere = await OpenAsync(conn, ct).ConfigureAwait(false);
        try
        {
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql.RenewLease;
                Add(cmd, "@id", id);
                Add(cmd, "@hostId", hostId);
                Add(cmd, "@status", status);
                Add(cmd, "@now", now);
                Add(cmd, "@until", until);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (openedHere) await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Leases up to <paramref name="batchSize"/> messages that are due and unleased to
    /// <paramref name="hostId"/> until <paramref name="until"/>, and returns exactly those messages.
    /// </summary>
    public async Task<IReadOnlyList<OutboxMessageRow>> ClaimPendingAsync(
        int status, DateTimeOffset now, DateTimeOffset until, string hostId, int batchSize, CancellationToken ct)
    {
        var conn = connection;
        var openedHere = await OpenAsync(conn, ct).ConfigureAwait(false);
        try
        {
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql.Claim;
                Add(cmd, "@status", status);
                Add(cmd, "@now", now);
                Add(cmd, "@until", until);
                Add(cmd, "@hostId", hostId);
                Add(cmd, "@batchSize", batchSize);
                var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (((IAsyncDisposable)reader).ConfigureAwait(false))
                {
                    var list = new List<OutboxMessageRow>();
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        list.Add(new OutboxMessageRow(
                            reader.GetGuid(0),
                            reader.GetString(1),
                            reader.GetFieldValue<byte[]>(2),
                            reader.GetInt32(3),
                            reader.GetFieldValue<DateTimeOffset>(4)));
                    }

                    return list;
                }
            }
        }
        finally
        {
            if (openedHere) await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task<int> InsertCoreAsync(
        IAsyncDbTransaction? tx,
        Guid id,
        string typeName,
        byte[] payload,
        int status,
        DateTimeOffset nextRetryAt,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        var conn = connection;
        var openedHere = await OpenAsync(conn, ct).ConfigureAwait(false);
        try
        {
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                if (tx is not null)
                    cmd.Transaction = tx;
                cmd.CommandText = sql.Insert;
                Add(cmd, "@id", id);
                Add(cmd, "@typeName", typeName);
                Add(cmd, "@payload", payload);
                Add(cmd, "@status", status);
                Add(cmd, "@nextRetryAt", nextRetryAt);
                Add(cmd, "@createdAt", createdAt);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (openedHere) await conn.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask<bool> OpenAsync(IAsyncDbConnection conn, CancellationToken ct)
    {
        if (conn.State == System.Data.ConnectionState.Open)
            return false;

        await conn.OpenAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static void Add(IAsyncDbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
