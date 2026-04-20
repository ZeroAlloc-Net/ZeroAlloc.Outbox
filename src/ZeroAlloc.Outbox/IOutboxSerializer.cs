namespace ZeroAlloc.Outbox;

/// <summary>Serializes and deserializes outbox message payloads.</summary>
public interface IOutboxSerializer
{
    ReadOnlyMemory<byte> Serialize<T>(T value);
    T Deserialize<T>(ReadOnlyMemory<byte> data);
}
