using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.AotSmoke;

internal sealed class FakeOutboxSerializer : IOutboxSerializer
{
    public ReadOnlyMemory<byte> Serialize<T>(T value)
        => System.Text.Encoding.UTF8.GetBytes(value?.ToString() ?? "");

    public T Deserialize<T>(ReadOnlyMemory<byte> data) => default!;
}
