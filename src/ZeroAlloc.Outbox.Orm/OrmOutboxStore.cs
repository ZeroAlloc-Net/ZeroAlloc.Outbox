using System.Data.Async;
using System.Data.Async.Adapters;
using System.Data.Common;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// <see cref="IOutboxStore"/> backed by ZeroAlloc.ORM. Raw SQL with
/// compile-time parameter binding, no change tracker, and nothing that reflects
/// at runtime.
/// </summary>
/// <remarks>
/// <para>
/// Writes the same <c>OutboxMessages</c> table as the EF Core adapter, with the
/// same status values, so the two can be swapped without a data migration.
/// </para>
/// <para>
/// State transitions are validated through <see cref="OutboxMessageFsm"/>, the
/// same machine the EF Core adapter uses, so an illegal move is rejected here
/// rather than quietly writing a row no dispatcher will pick up.
/// </para>
/// <para>
/// This implements <see cref="IOutboxStore"/> only. The dashboard's
/// <c>IOutboxDashboardStore</c> is a distinctly larger surface — aggregate
/// views, projections, operator actions — and is deliberately left out rather
/// than stubbed, so a dashboard pointed at this store fails at registration
/// instead of silently reporting nothing.
/// </para>
/// </remarks>
public sealed class OrmOutboxStore : IOutboxStore
{
    private readonly OutboxMessageRepository _repo;
    private readonly IPendingOutboxQuery _pending;

    /// <summary>
    /// Creates a store over the application's connection.
    /// </summary>
    /// <param name="connection">The connection the outbox table lives on.</param>
    public OrmOutboxStore(IAsyncDbConnection connection)
        : this(connection, OutboxOrmDialect.Sqlite)
    {
    }

    /// <summary>
    /// Creates a store over the application's connection, for a specific
    /// database.
    /// </summary>
    /// <param name="connection">The connection the outbox table lives on.</param>
    /// <param name="dialect">
    /// Selects the paged-query spelling. Only the batch fetch differs; every
    /// other statement is plain ANSI.
    /// </param>
    public OrmOutboxStore(IAsyncDbConnection connection, OutboxOrmDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _repo = new OutboxMessageRepository(connection);
        _pending = dialect switch
        {
            // SQL Server has no LIMIT. SQLite has no OFFSET/FETCH. Postgres
            // accepts both and stays on LIMIT so its behaviour is unchanged.
            OutboxOrmDialect.SqlServer => new FetchFirstPendingOutboxQuery(connection),
            _ => new LimitPendingOutboxQuery(connection),
        };
    }

    /// <inheritdoc />
    public async ValueTask EnqueueAsync(
        string typeName,
        ReadOnlyMemory<byte> payload,
        DbTransaction? transaction,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(typeName);

        var id = OutboxMessageId.New();
        var now = DateTimeOffset.UtcNow;

        if (transaction is null)
        {
            await _repo.InsertAsync(
                id.Value, typeName, payload.ToArray(),
                (int)OrmOutboxMessageStatus.Pending, now, now, ct).ConfigureAwait(false);
            return;
        }

        // A transaction belongs to the connection that began it, and ADO.NET
        // rejects a command whose connection and transaction disagree. So the
        // insert runs on the caller's connection rather than the injected one:
        // enlisting means joining their unit of work, not borrowing their
        // transaction object. This is what makes the enqueue commit or roll back
        // together with whatever else the caller is writing.
        var callerConnection = transaction.Connection
            ?? throw new InvalidOperationException(
                "The supplied DbTransaction has no connection, which means it has already been "
                + "committed or rolled back. Enqueue inside an open transaction, or pass null to "
                + "write immediately.");

        var asyncConnection = callerConnection.AsAsync();
        var asyncTransaction = new AdapterDbTransaction(
            transaction, (AdapterDbConnection)asyncConnection);
        var scoped = new OutboxMessageRepository(asyncConnection);

        await scoped.InsertInTransactionAsync(
            id.Value, typeName, payload.ToArray(),
            (int)OrmOutboxMessageStatus.Pending, now, now, asyncTransaction, ct)
            .ConfigureAwait(false);
    }

    // EnqueueDeferredAsync is deliberately not overridden. The EF Core adapter
    // overrides it to stage the row in the change tracker so the caller's own
    // SaveChanges commits it; there is no equivalent ambient unit of work here,
    // and inventing one would mean holding writes in memory with no defined
    // flush point. The interface's default sends it to EnqueueAsync with no
    // transaction, which writes immediately -- a documented fallback the
    // contract explicitly supports. Callers wanting transactional enqueue pass
    // their DbTransaction to EnqueueAsync above.

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct)
    {
        var rows = await _pending.FetchPendingAsync(
            (int)OrmOutboxMessageStatus.Pending, DateTimeOffset.UtcNow, batchSize, ct)
            .ConfigureAwait(false);

        var result = new List<OutboxEntry>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(new OutboxEntry
            {
                Id = new OutboxMessageId(row.Id),
                TypeName = row.TypeName,
                RawPayload = row.Payload,
                RetryCount = row.RetryCount,
                CreatedAt = row.CreatedAt,
            });
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct)
    {
        if (!await TryTransitionAsync(id, OutboxMessageTrigger.Dispatch, "succeeded", ct).ConfigureAwait(false))
            return;

        await _repo.MarkProcessedAsync(
            id.Value, (int)OrmOutboxMessageStatus.Succeeded, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask MarkFailedAsync(
        OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct)
    {
        if (!await TryTransitionAsync(id, OutboxMessageTrigger.Fail, "failed", ct).ConfigureAwait(false))
            return;

        // Stays Pending: the status column records dispatch outcome, and a
        // message awaiting another attempt is still pending. Retry versus
        // first-attempt is carried by RetryCount, which is how ToState
        // reconstructs the FSM position on the next read.
        await _repo.MarkForRetryAsync(
            id.Value, (int)OrmOutboxMessageStatus.Pending, retryCount, nextRetryAt, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct)
    {
        if (!await TryTransitionAsync(id, OutboxMessageTrigger.Exhaust, "dead-lettered", ct).ConfigureAwait(false))
            return;

        await _repo.DeadLetterAsync(
            id.Value, (int)OrmOutboxMessageStatus.DeadLetter, error, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the row's current state and checks the trigger is legal from there.
    /// </summary>
    /// <returns>
    /// False when the row no longer exists, matching the EF Core adapter, which
    /// returns quietly rather than throwing on a message someone else removed.
    /// </returns>
    /// <exception cref="InvalidOperationException">The transition is not legal.</exception>
    private async ValueTask<bool> TryTransitionAsync(
        OutboxMessageId id, OutboxMessageTrigger trigger, string verb, CancellationToken ct)
    {
        var state = await _repo.GetStateAsync(id.Value, ct).ConfigureAwait(false);
        if (state is null) return false;

        var fsm = new OutboxMessageFsm(ToState(state.Status, state.RetryCount));
        if (!fsm.TryFire(trigger))
        {
            throw new InvalidOperationException(
                $"Cannot mark message {id} as {verb} in state {fsm.Current}.");
        }

        return true;
    }

    // Mirrors the EF Core adapter exactly. Pending and Retry share a status
    // value and are told apart by RetryCount.
    private static OutboxMessageState ToState(int status, int retryCount) => status switch
    {
        (int)OrmOutboxMessageStatus.Succeeded => OutboxMessageState.Dispatched,
        (int)OrmOutboxMessageStatus.DeadLetter => OutboxMessageState.DeadLetter,
        _ => retryCount == 0 ? OutboxMessageState.Pending : OutboxMessageState.Retry,
    };
}
