using System.Data.Common;

namespace ZeroAlloc.Outbox;

/// <summary>Durable backing store for outbox messages.</summary>
public interface IOutboxStore
{
    /// <summary>Persists a serialized message payload.</summary>
    ValueTask EnqueueAsync(
        string typeName,
        ReadOnlyMemory<byte> payload,
        DbTransaction? transaction,
        CancellationToken ct);

    /// <summary>
    /// Tracks the outbox message in the underlying store WITHOUT committing it. Pending
    /// changes are flushed by the next sibling write that calls the store's containing
    /// unit of work (e.g. for <c>Outbox.EfCore</c>: the next <c>DbContext.SaveChangesAsync</c>
    /// from another consumer of the same scoped <c>DbContext</c>).
    /// </summary>
    /// <remarks>
    /// Required by callers that need atomic commit of an outbox row alongside other domain
    /// writes — most notably <c>ZeroAlloc.Saga.Outbox</c>, where the outbox row must commit
    /// in the same transaction as the saga state save so an OCC retry rolls both back together.
    /// <para>
    /// Backends that don't support deferred persistence MAY implement this as a regular
    /// <see cref="EnqueueAsync"/> via the default body below — the resulting auto-commit
    /// is suboptimal for atomicity but keeps the API uniform. The atomicity contract holds
    /// only when paired with a backend that participates in an outer transaction (currently
    /// only <c>ZeroAlloc.Outbox.EfCore</c> overrides this with a true defer).
    /// </para>
    /// </remarks>
    ValueTask EnqueueDeferredAsync(
        string typeName,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
        => EnqueueAsync(typeName, payload, transaction: null, ct);

    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> messages that are due and not
    /// leased by anyone, oldest first, and returns exactly the messages it claimed.
    /// </summary>
    /// <remarks>
    /// A message is due when it is pending and its next retry time has passed, and unleased
    /// when it has no lease or its lease has expired. Claiming sets its lease holder to
    /// <see cref="OutboxLease.HostId"/> and its expiry to now plus
    /// <see cref="OutboxLease.Duration"/>. Two claimers running at the same moment never
    /// claim the same message.
    /// </remarks>
    ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct);

    /// <summary>
    /// Extends this host's lease on one message to now plus <see cref="OutboxLease.Duration"/>.
    /// </summary>
    /// <returns>
    /// True if this host still held an unexpired lease on the pending message and it was
    /// extended. False if the lease expired, even though no other host has claimed the message
    /// yet, or another host holds it, or the message is no longer pending. On false the
    /// caller must not dispatch the message.
    /// </returns>
    ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct);

    /// <summary>
    /// Gives up this host's leases on the given messages, so another host can claim them at
    /// once instead of waiting for the leases to expire.
    /// </summary>
    /// <returns>The number of leases released.</returns>
    /// <remarks>
    /// Only messages that are still pending and leased by <see cref="OutboxLease.HostId"/> are
    /// released; any other id is left alone. The worker calls this when it stops in the middle
    /// of a batch, for the claimed messages it did not finish.
    /// </remarks>
    ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct);

    /// <summary>
    /// Marks a message this host has leased as successfully dispatched, and releases its lease.
    /// </summary>
    /// <param name="id">The message to mark.</param>
    /// <param name="lease">
    /// The lease the message was claimed with. The mark applies only while
    /// <see cref="OutboxLease.HostId"/> is still the message's lease holder.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// True if this call moved the message. False if it was no longer pending, because another
    /// host had already completed or dead-lettered it, or if another host now holds its lease,
    /// or if it no longer exists.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The change is one conditional write on the message still being pending and still leased
    /// by <see cref="OutboxLease.HostId"/>. Only the current lease holder can move a message, so
    /// of two concurrent marks from different hosts exactly one wins, a host whose lease was
    /// taken over cannot overwrite the new holder's lease or outcome, and a terminal state is
    /// never overwritten.
    /// </para>
    /// <para>
    /// The lease expiry is deliberately not checked. A host whose lease expired but was not yet
    /// claimed by anyone else still holds it, and can still record the outcome of its dispatch.
    /// </para>
    /// </remarks>
    ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct);

    /// <summary>
    /// Records a failed dispatch attempt on a message this host has leased and schedules the next
    /// retry, and releases its lease so it is claimable again at <paramref name="nextRetryAt"/>.
    /// </summary>
    /// <param name="id">The message to mark.</param>
    /// <param name="retryCount">The number of failed attempts so far, including this one.</param>
    /// <param name="nextRetryAt">When the message becomes due again.</param>
    /// <param name="lease"><inheritdoc cref="MarkSucceededAsync" path="/param[@name='lease']"/></param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns><inheritdoc cref="MarkSucceededAsync" path="/returns"/></returns>
    /// <remarks><inheritdoc cref="MarkSucceededAsync" path="/remarks"/></remarks>
    ValueTask<bool> MarkFailedAsync(
        OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct);

    /// <summary>
    /// Moves a message this host has leased to dead-letter after exhausting all retry attempts,
    /// and releases its lease.
    /// </summary>
    /// <param name="id">The message to mark.</param>
    /// <param name="error">Why the message was dead-lettered.</param>
    /// <param name="lease"><inheritdoc cref="MarkSucceededAsync" path="/param[@name='lease']"/></param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns><inheritdoc cref="MarkSucceededAsync" path="/returns"/></returns>
    /// <remarks><inheritdoc cref="MarkSucceededAsync" path="/remarks"/></remarks>
    ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct);
}
