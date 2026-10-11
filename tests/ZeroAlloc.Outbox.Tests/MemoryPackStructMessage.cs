using MemoryPack;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox.Tests;

// A value type in a format whose reader allocates nothing for it, so a box on the read path shows as
// the only allocation.
[OutboxMessage]
[MemoryPackable]
[ZeroAllocSerializable(SerializationFormat.MemoryPack)]
public partial struct MemoryPackStructMessage
{
    public int Id { get; set; }
    public long Amount { get; set; }
    public bool Express { get; set; }
}
