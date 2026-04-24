using ZeroAlloc.Resilience;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Implemented by the source generator per [OutboxMessage] type and registered with DI.
/// The background worker resolves all registered implementations to build a dispatch registry.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DispatchAsync"/> is annotated with <see cref="RetryAttribute"/> so the Resilience
/// generator can emit a proxy for any custom single-implementation scenario.
/// </para>
/// <para>
/// <b>Architectural note (Outbox#20):</b> The Resilience proxy cannot replace the
/// <c>IEnumerable&lt;IOutboxTypeDispatcher&gt;</c> collection pattern used by
/// <c>OutboxWorkerService</c> because the generated
/// <c>AddIOutboxTypeDispatcherResilience&lt;TImpl&gt;</c> extension wraps a single
/// implementation and cannot interpose each element of the collection individually.
/// Full proxy-based DI replacement is deferred. The store-level exponential backoff
/// (<c>MarkFailedAsync</c> / <c>nextRetry</c>) is a durable cross-poll schedule and
/// is likewise deferred — it is not replaceable by an in-process <c>[Retry]</c> loop
/// without a redesign.
/// </para>
/// </remarks>
public interface IOutboxTypeDispatcher
{
    /// <summary>The fully-qualified type name used as the discriminator in the store.</summary>
    string TypeName { get; }

    /// <summary>Deserializes <paramref name="payload"/> and dispatches the message.</summary>
    /// <remarks>
    /// Annotated with <see cref="RetryAttribute"/> so the Resilience generator produces a proxy
    /// for single-implementation wrappers. Default: 3 attempts, 200 ms exponential backoff.
    /// The <c>IEnumerable&lt;IOutboxTypeDispatcher&gt;</c> collection registration in
    /// <c>OutboxWorkerService</c> is not automatically wrapped — see class-level remarks.
    /// </remarks>
    [Retry(MaxAttempts = 3, BackoffMs = 200)]
    ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
}
