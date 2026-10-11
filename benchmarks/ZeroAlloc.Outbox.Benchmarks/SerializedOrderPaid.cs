using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox.Benchmarks;

// A value type, so EnqueueSerializationBenchmark shows whether serializing it boxes.
[OutboxMessage]
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public readonly record struct SerializedOrderPaid(int Id, long Amount, bool Express);
