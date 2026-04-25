namespace ZeroAlloc.Outbox;

/// <summary>Serializes and deserializes outbox message payloads.</summary>
/// <remarks>
/// The default implementation (<see cref="SystemTextJsonOutboxSerializer"/>) uses reflection-based
/// JSON and is not AOT-safe. For AOT/trimming scenarios, register an <c>ISerializerDispatcher</c>
/// from <c>ZeroAlloc.Serialisation</c> before calling <c>AddOutbox()</c> — the framework will
/// automatically use <see cref="DispatchingOutboxSerializer"/> instead.
/// </remarks>
public interface IOutboxSerializer
{
    /// <summary>Serializes <paramref name="value"/> to a byte buffer.</summary>
    ReadOnlyMemory<byte> Serialize<T>(T value);

    /// <summary>Deserializes a value of type <typeparamref name="T"/> from <paramref name="data"/>.</summary>
    T Deserialize<T>(ReadOnlyMemory<byte> data);
}
