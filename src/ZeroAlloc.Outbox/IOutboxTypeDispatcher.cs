namespace ZeroAlloc.Outbox;

/// <summary>
/// Implemented by the source generator per [OutboxMessage] type and registered with DI.
/// The background worker resolves all registered implementations to build a dispatch registry.
/// </summary>
public interface IOutboxTypeDispatcher
{
    /// <summary>The fully-qualified type name used as the discriminator in the store.</summary>
    string TypeName { get; }

    /// <summary>Deserializes <paramref name="payload"/> and dispatches the message.</summary>
    ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
}
