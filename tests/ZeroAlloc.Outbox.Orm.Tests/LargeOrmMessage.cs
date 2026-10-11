using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox.Orm.Tests;

// A message whose payload dwarfs everything else an enqueue allocates, so a second copy of it shows.
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record LargeOrmMessage(string Body);
