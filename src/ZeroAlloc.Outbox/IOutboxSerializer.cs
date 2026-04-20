using System.Diagnostics.CodeAnalysis;

namespace ZeroAlloc.Outbox;

/// <summary>Serializes and deserializes outbox message payloads.</summary>
public interface IOutboxSerializer
{
    /// <summary>Serializes <paramref name="value"/> to a UTF-8 byte array.</summary>
    [RequiresUnreferencedCode("Serialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("Serialization may require dynamic code generation.")]
    ReadOnlyMemory<byte> Serialize<T>(T value);

    /// <summary>Deserializes a value of type <typeparamref name="T"/> from <paramref name="data"/>.</summary>
    [RequiresUnreferencedCode("Deserialization may require types that cannot be statically analyzed.")]
    [RequiresDynamicCode("Deserialization may require dynamic code generation.")]
    T Deserialize<T>(ReadOnlyMemory<byte> data);
}
