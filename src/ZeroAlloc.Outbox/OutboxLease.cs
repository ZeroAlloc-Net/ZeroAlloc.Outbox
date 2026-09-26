namespace ZeroAlloc.Outbox;

/// <summary>
/// A time-bounded claim on outbox messages, held by one host.
/// </summary>
/// <param name="HostId">
/// Identifies the claiming host. Stored in the row's <c>LockedBy</c> column, so it must be
/// unique per running process and at most 128 characters.
/// </param>
/// <param name="Duration">
/// How long a claim or renewal lasts. Once it runs out another host may claim the message, so
/// it must exceed the slowest single dispatch.
/// </param>
/// <remarks>
/// <see cref="IOutboxStore.ClaimPendingAsync"/> marks the rows it returns with this lease, and
/// <see cref="IOutboxStore.RenewLeaseAsync"/> extends it for one message right before dispatch.
/// Two hosts polling the same store therefore never dispatch the same message, unless a
/// single dispatch outlives <see cref="Duration"/>.
/// </remarks>
public readonly record struct OutboxLease(string HostId, TimeSpan Duration);
