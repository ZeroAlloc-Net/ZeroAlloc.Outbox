using System.Data.Async;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// The SQL the outbox store needs, one method per statement. Each dialect implements it as a
/// ZeroAlloc.ORM repository whose generator emits the parameter binding at compile time.
/// </summary>
/// <remarks>
/// <para>
/// Every mark is guarded by <c>Status = @pending AND LockedBy = @hostId</c> and returns the number of
/// rows it changed, so the caller learns whether it won the transition. The status condition is the
/// outbox state machine for these triggers: Dispatch, Fail and Exhaust are legal exactly from the
/// pending states. The lease condition means only the current lease holder can move a row.
/// <c>LockedUntil</c> is deliberately not checked, so a host whose lease expired without being taken
/// over can still record its outcome.
/// </para>
/// <para>
/// Status is written as its integer value rather than a name. That keeps the column orderable and
/// indexable on every provider, and means adding a state does not invalidate existing rows.
/// </para>
/// <para>
/// The claim is a single <c>UPDATE</c> that leases the rows it takes and returns them, and every
/// provider spells that differently. The order of the returned rows is unspecified on every dialect,
/// so the store sorts them by <c>CreatedAt</c> itself.
/// </para>
/// </remarks>
internal interface IOutboxStatements
{
    Task<int> InsertAsync(
        Guid id,
        string typeName,
        byte[] payload,
        int status,
        DateTimeOffset nextRetryAt,
        DateTimeOffset createdAt,
        CancellationToken ct);

    /// <summary>
    /// Same insert, enlisted in a transaction the caller owns.
    /// </summary>
    Task<int> InsertInTransactionAsync(
        Guid id,
        string typeName,
        byte[] payload,
        int status,
        DateTimeOffset nextRetryAt,
        DateTimeOffset createdAt,
        IAsyncDbTransaction tx,
        CancellationToken ct);

    Task<int> MarkProcessedAsync(
        Guid id, int status, DateTimeOffset processedAt, int pending, string hostId, CancellationToken ct);

    Task<int> MarkForRetryAsync(
        Guid id, int retryCount, DateTimeOffset nextRetryAt, int pending, string hostId, CancellationToken ct);

    Task<int> DeadLetterAsync(
        Guid id, int status, string error, DateTimeOffset processedAt, int pending, string hostId,
        CancellationToken ct);

    /// <summary>
    /// Gives up this host's lease on a pending row. A row leased by another host, or no longer
    /// pending, is left alone.
    /// </summary>
    Task<int> ReleaseLeaseAsync(Guid id, string hostId, int pending, CancellationToken ct);

    /// <summary>
    /// Extends a lease the host still holds. An expired lease is not renewed, even for the host
    /// that took it, because another host may already be entitled to claim the row.
    /// </summary>
    /// <returns>The number of rows changed: 1 if renewed, 0 if the lease was lost.</returns>
    Task<int> RenewLeaseAsync(
        Guid id, string hostId, int status, DateTimeOffset now, DateTimeOffset until, CancellationToken ct);

    /// <summary>
    /// Moves a row back to pending, due <paramref name="now"/>, with a fresh attempt counter and
    /// no lease, if it is still dead-lettered.
    /// </summary>
    /// <returns>The number of rows changed: 1 if requeued, 0 if the row is missing or not dead-lettered.</returns>
    Task<int> RequeueAsync(Guid id, int status, DateTimeOffset now, int deadLetter, CancellationToken ct);

    /// <summary>
    /// Reads a row's status and attempt counter, or null if there is no such row.
    /// </summary>
    Task<OutboxRowState?> ReadStateAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Leases up to <paramref name="batchSize"/> messages that are due and unleased to
    /// <paramref name="hostId"/> until <paramref name="until"/>, and returns exactly those messages.
    /// </summary>
    Task<IReadOnlyList<OutboxMessageRow>> ClaimPendingAsync(
        int status, DateTimeOffset now, DateTimeOffset until, string hostId, int batchSize, CancellationToken ct);
}
