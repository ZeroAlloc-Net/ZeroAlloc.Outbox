using ZeroAlloc.Outbox;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox.AotSmoke;

// [OutboxMessage] makes the Outbox generator emit the writer, the type dispatcher and
// AddOrderPlacedOutbox(). [ZeroAllocSerializable] makes the Serialisation generator emit an
// AOT-safe serializer and AddSerializerDispatcher(), which AddOutbox() picks up.
[OutboxMessage]
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record OrderPlaced(string OrderId, decimal Total);
