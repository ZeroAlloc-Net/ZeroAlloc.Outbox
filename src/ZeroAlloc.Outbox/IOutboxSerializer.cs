namespace ZeroAlloc.Outbox;

/// <summary>Serializes and deserializes outbox message payloads.</summary>
/// <remarks>
/// <c>AddOutbox()</c> resolves this to the AOT-safe <see cref="DispatchingOutboxSerializer"/> when an
/// <c>ISerializerDispatcher</c> from <c>ZeroAlloc.Serialisation</c> is registered, for example with
/// <c>services.AddSerializerDispatcher()</c>. The reflection-based
/// <see cref="SystemTextJsonOutboxSerializer"/>, which is not trim- or AOT-safe, is used only when
/// the application opts in with <c>WithSystemTextJsonSerializer()</c>. There is no implicit fallback.
/// </remarks>
public interface IOutboxSerializer
{
    /// <summary>Serializes <paramref name="value"/> to a byte buffer.</summary>
    ReadOnlyMemory<byte> Serialize<T>(T value);

    /// <summary>Deserializes a value of type <typeparamref name="T"/> from <paramref name="data"/>.</summary>
    T Deserialize<T>(ReadOnlyMemory<byte> data);
}
