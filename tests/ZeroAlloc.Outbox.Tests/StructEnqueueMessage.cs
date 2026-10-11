using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox.Tests;

// A value type, so the enqueue path is measured where boxing would show.
[OutboxMessage]
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public readonly record struct StructEnqueueMessage(int Id, long Amount, bool Express);
