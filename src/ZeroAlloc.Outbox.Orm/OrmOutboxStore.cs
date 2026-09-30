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
/// State transitions follow <see cref="OutboxMessageFsm"/>, the same machine the
/// EF Core adapter uses. Each mark is one conditional <c>UPDATE</c> guarded by
/// the row still being pending, which is exactly the set of states from which
/// Dispatch, Fail and Exhaust are legal, and by the row still being leased by
/// the marking host. So the database, not a separate read, decides the
/// transition: only the current lease holder can move the row, so of two
/// concurrent marks from different hosts exactly one wins, and a terminal state
/// is never overwritten. The lease expiry is not checked, so a host whose lease
/// expired without being taken over can still record its outcome.
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
    /// Selects the batch-claim spelling. Only the batch claim differs; every
    /// other statement is plain ANSI.
    /// </param>
    public OrmOutboxStore(IAsyncDbConnection connection, OutboxOrmDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _repo = new OutboxMessageRepository(connection);
        _pending = dialect switch
        {
            // SQL Server has neither LIMIT nor RETURNING. Postgres takes row
            // locks with SKIP LOCKED, which SQLite neither has nor needs.
            OutboxOrmDialect.SqlServer => new ClaimSqlServerPendingOutboxQuery(connection),
            OutboxOrmDialect.Postgres => new ClaimPostgresPendingOutboxQuery(connection),
            _ => new ClaimSqlitePendingOutboxQuery(connection),
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// A non-null <paramref name="transaction"/> enlists the insert in it, through
    /// <see cref="EnqueueInTransactionAsync"/>. ORM callers already hold an
    /// <see cref="IAsyncDbTransaction"/> and should call that method directly.
    /// </remarks>
    public async ValueTask EnqueueAsync(
        string typeName,
        ReadOnlyMemory<byte> payload,
        DbTransaction? transaction,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(typeName);

        if (transaction is null)
        {
            var now = DateTimeOffset.UtcNow;
            await _repo.InsertAsync(
                OutboxMessageId.New().Value, typeName, payload.ToArray(),
                (int)OrmOutboxMessageStatus.Pending, now, now, ct).ConfigureAwait(false);
            return;
        }

        var callerConnection = transaction.Connection
            ?? throw new InvalidOperationException(
                "The supplied DbTransaction has no connection, which means it has already been "
                + "committed or rolled back. Enqueue inside an open transaction, or pass null to "
                + "write immediately.");

        // Wrap the caller's ADO.NET pair in the ORM's adapter so the insert takes one path.
        var asyncConnection = callerConnection.AsAsync();
        var asyncTransaction = new AdapterDbTransaction(
            transaction, (AdapterDbConnection)asyncConnection);
        await EnqueueInTransactionAsync(typeName, payload, asyncTransaction, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists a serialized message payload inside a transaction the caller owns, so the row
    /// commits or rolls back together with the caller's other writes.
    /// </summary>
    /// <param name="typeName">The message type name the dispatcher routes on.</param>
    /// <param name="payload">The serialized message.</param>
    /// <param name="transaction">
    /// An open transaction, as returned by <see cref="IAsyncDbConnection.BeginTransactionAsync"/>.
    /// The insert runs on its <see cref="IAsyncDbTransaction.Connection"/>, not on the
    /// connection this store was created with.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <remarks>
    /// Works with any <see cref="IAsyncDbConnection"/>, not only one made by
    /// <c>AsAsync()</c>, so a caller never has to unwrap the ORM's transaction to a
    /// <see cref="DbTransaction"/>. To write immediately instead, call
    /// <see cref="EnqueueAsync"/> with a null transaction.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="typeName"/> or <paramref name="transaction"/> is null.
    /// </exception>
    public async ValueTask EnqueueInTransactionAsync(
        string typeName,
        ReadOnlyMemory<byte> payload,
        IAsyncDbTransaction transaction,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(transaction);

        var now = DateTimeOffset.UtcNow;

        // A transaction belongs to the connection that began it, and ADO.NET
        // rejects a command whose connection and transaction disagree. So the
        // insert runs on the caller's connection rather than the injected one:
        // enlisting means joining their unit of work, not borrowing their
        // transaction object. This is what makes the enqueue commit or roll back
        // together with whatever else the caller is writing.
        var scoped = new OutboxMessageRepository(transaction.Connection);

        await scoped.InsertInTransactionAsync(
            OutboxMessageId.New().Value, typeName, payload.ToArray(),
            (int)OrmOutboxMessageStatus.Pending, now, now, transaction, ct)
            .ConfigureAwait(false);
    }

    // EnqueueDeferredAsync is deliberately not overridden. The EF Core adapter
    // overrides it to stage the row in the change tracker so the caller's own
    // SaveChanges commits it; there is no equivalent ambient unit of work here,
    // and inventing one would mean holding writes in memory with no defined
    // flush point. The interface's default sends it to EnqueueAsync with no
    // transaction, which writes immediately -- a documented fallback the
    // contract explicitly supports. Callers wanting transactional enqueue call
    // EnqueueInTransactionAsync, or pass their DbTransaction to EnqueueAsync, and
    // own the flush point themselves.

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(
        int batchSize, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        var now = DateTimeOffset.UtcNow;
        var rows = await _pending.ClaimPendingAsync(
            (int)OrmOutboxMessageStatus.Pending, now, now + lease.Duration, lease.HostId, batchSize, ct)
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

        // Neither RETURNING nor OUTPUT promises an order, so restore oldest-first here.
        result.Sort(static (a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        var now = DateTimeOffset.UtcNow;
        var changed = await _repo.RenewLeaseAsync(
            id.Value, lease.HostId, (int)OrmOutboxMessageStatus.Pending, now, now + lease.Duration, ct)
            .ConfigureAwait(false);
        return changed == 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One conditional statement per id. ZeroAlloc.ORM binds a collection parameter only as
    /// BulkInsert rows and cannot expand it into an <c>IN (…)</c> list, and the list is at most
    /// one batch, released once on shutdown.
    /// </remarks>
    public async ValueTask<int> ReleaseLeasesAsync(
        IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ThrowIfNoHost(lease);

        var released = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            released += await _repo.ReleaseLeaseAsync(
                ids[i].Value, lease.HostId, (int)OrmOutboxMessageStatus.Pending, ct)
                .ConfigureAwait(false);
        }

        return released;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        var changed = await _repo.MarkProcessedAsync(
            id.Value, (int)OrmOutboxMessageStatus.Succeeded, DateTimeOffset.UtcNow,
            (int)OrmOutboxMessageStatus.Pending, lease.HostId, ct)
            .ConfigureAwait(false);
        return changed == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkFailedAsync(
        OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        // Stays Pending: the status column records dispatch outcome, and a
        // message awaiting another attempt is still pending. Retry versus
        // first-attempt is carried by RetryCount. The retry time is stored in UTC like every
        // other timestamp: SQLite compares these as text, so a mixed offset would sort wrongly,
        // and a PostgreSQL timestamptz parameter rejects a non-zero offset.
        var changed = await _repo.MarkForRetryAsync(
            id.Value, retryCount, nextRetryAt.ToUniversalTime(), (int)OrmOutboxMessageStatus.Pending,
            lease.HostId, ct)
            .ConfigureAwait(false);
        return changed == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeadLetterAsync(
        OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        var changed = await _repo.DeadLetterAsync(
            id.Value, (int)OrmOutboxMessageStatus.DeadLetter, error, DateTimeOffset.UtcNow,
            (int)OrmOutboxMessageStatus.Pending, lease.HostId, ct)
            .ConfigureAwait(false);
        return changed == 1;
    }

    // A default OutboxLease carries no host, and a null, empty or blank LockedBy would read as
    // unleased, and a blank one identifies no host.
    private static void ThrowIfNoHost(OutboxLease lease)
    {
        if (string.IsNullOrWhiteSpace(lease.HostId))
            throw new ArgumentException("The lease has no HostId.", nameof(lease));
    }
}
