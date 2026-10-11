using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox.Benchmarks;

[OutboxMessage]
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record SerializedOrderShipped(string OrderId, decimal Total);
