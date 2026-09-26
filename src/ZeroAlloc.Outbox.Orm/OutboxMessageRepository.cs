using System.Data.Async;
using ZeroAlloc.ORM;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// The SQL the outbox store needs, as ZeroAlloc.ORM partial methods. The
/// generator emits parameter binding and materialisation at compile time, so
/// nothing here reflects at runtime.
/// </summary>
/// <remarks>
/// <para>
/// The SQL is plain ANSI throughout, so the same repository serves SQLite,
/// PostgreSQL and SQL Server. Only the DDL and the batch claim differ; the DDL
/// lives in <see cref="OutboxOrmMigrations"/>, and the claim in the
/// <see cref="IPendingOutboxQuery"/> for each dialect.
/// </para>
/// <para>
/// Every mark is guarded by <c>Status = @pending AND LockedBy = @hostId</c> and
/// returns the number of rows it changed, so the caller learns whether it won
/// the transition. The status condition is the outbox state machine for these
/// triggers: Dispatch, Fail and Exhaust are legal exactly from the pending
/// states. The lease condition means only the current lease holder can move a
/// row. <c>LockedUntil</c> is deliberately not checked, so a host whose lease
/// expired without being taken over can still record its outcome.
/// </para>
/// <para>
/// Status is written as its integer value rather than a name. That keeps the
/// column orderable and indexable on every provider, and means adding a state
/// does not invalidate existing rows.
/// </para>
/// </remarks>
internal sealed partial class OutboxMessageRepository(IAsyncDbConnection connection)
{
    [Command("""
        INSERT INTO OutboxMessages
            (Id, TypeName, Payload, Status, RetryCount, NextRetryAt, CreatedAt)
        VALUES
            (@id, @typeName, @payload, @status, 0, @nextRetryAt, @createdAt)
        """)]
    public partial Task<int> InsertAsync(
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
    /// <remarks>
    /// A separate method rather than a nullable parameter: the ORM decides at
    /// compile time whether to emit the <c>cmd.Transaction</c> assignment, so
    /// the two shapes are genuinely different emitted commands, not one command
    /// with an optional argument.
    /// </remarks>
    [Command("""
        INSERT INTO OutboxMessages
            (Id, TypeName, Payload, Status, RetryCount, NextRetryAt, CreatedAt)
        VALUES
            (@id, @typeName, @payload, @status, 0, @nextRetryAt, @createdAt)
        """)]
    public partial Task<int> InsertInTransactionAsync(
        Guid id,
        string typeName,
        byte[] payload,
        int status,
        DateTimeOffset nextRetryAt,
        DateTimeOffset createdAt,
        IAsyncDbTransaction tx,
        CancellationToken ct);

    [Command("""
        UPDATE OutboxMessages
        SET Status = @status, ProcessedAt = @processedAt,
            LockedBy = NULL, LockedUntil = NULL
        WHERE Id = @id AND Status = @pending AND LockedBy = @hostId
        """)]
    public partial Task<int> MarkProcessedAsync(
        Guid id, int status, DateTimeOffset processedAt, int pending, string hostId, CancellationToken ct);

    [Command("""
        UPDATE OutboxMessages
        SET RetryCount = @retryCount, NextRetryAt = @nextRetryAt,
            LockedBy = NULL, LockedUntil = NULL
        WHERE Id = @id AND Status = @pending AND LockedBy = @hostId
        """)]
    public partial Task<int> MarkForRetryAsync(
        Guid id, int retryCount, DateTimeOffset nextRetryAt, int pending, string hostId, CancellationToken ct);

    [Command("""
        UPDATE OutboxMessages
        SET Status = @status, DeadLetterError = @error, ProcessedAt = @processedAt,
            LockedBy = NULL, LockedUntil = NULL
        WHERE Id = @id AND Status = @pending AND LockedBy = @hostId
        """)]
    public partial Task<int> DeadLetterAsync(
        Guid id, int status, string error, DateTimeOffset processedAt, int pending, string hostId,
        CancellationToken ct);

    /// <summary>
    /// Gives up this host's lease on a pending row. A row leased by another host,
    /// or no longer pending, is left alone.
    /// </summary>
    [Command("""
        UPDATE OutboxMessages
        SET LockedBy = NULL, LockedUntil = NULL
        WHERE Id = @id AND LockedBy = @hostId AND Status = @pending
        """)]
    public partial Task<int> ReleaseLeaseAsync(Guid id, string hostId, int pending, CancellationToken ct);

    /// <summary>
    /// Extends a lease the host still holds. An expired lease is not renewed,
    /// even for the host that took it, because another host may already be
    /// entitled to claim the row.
    /// </summary>
    /// <returns>The number of rows changed: 1 if renewed, 0 if the lease was lost.</returns>
    [Command("""
        UPDATE OutboxMessages
        SET LockedUntil = @until
        WHERE Id = @id AND LockedBy = @hostId AND LockedUntil >= @now AND Status = @status
        """)]
    public partial Task<int> RenewLeaseAsync(
        Guid id, string hostId, int status, DateTimeOffset now, DateTimeOffset until, CancellationToken ct);
}
