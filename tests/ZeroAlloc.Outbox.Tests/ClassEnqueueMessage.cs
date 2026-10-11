using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox.Tests;

[OutboxMessage]
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record ClassEnqueueMessage(string OrderId, decimal Total);
