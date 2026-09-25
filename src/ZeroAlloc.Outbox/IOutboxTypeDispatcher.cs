namespace ZeroAlloc.Outbox;

/// <summary>
/// Implemented by the source generator per [OutboxMessage] type and registered with DI.
/// The background worker resolves all registered implementations to build a dispatch registry.
/// </summary>
/// <remarks>
/// <para>
/// This interface carries no ZeroAlloc.Resilience attributes. <c>OutboxWorkerService</c>
/// resolves every registered <see cref="IOutboxTypeDispatcher"/> as a collection, and a
/// generated resilience proxy wraps a single implementation, so a proxy of this interface
/// could not interpose on the worker (Outbox#20). For in-process retries around dispatch,
/// wrap your own dispatcher interface with <c>WithResilience</c> from
/// ZeroAlloc.Outbox.Resilience.
/// </para>
/// <para>
/// The store-level exponential backoff (<c>MarkFailedAsync</c> / <c>nextRetry</c>) is a
/// durable cross-poll schedule and is separate from any in-process retry: it survives
/// process restarts, where an in-process retry loop does not.
/// </para>
/// </remarks>
public interface IOutboxTypeDispatcher
{
    /// <summary>The fully-qualified type name used as the discriminator in the store.</summary>
    string TypeName { get; }

    /// <summary>Deserializes <paramref name="payload"/> and dispatches the message.</summary>
    ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
}
