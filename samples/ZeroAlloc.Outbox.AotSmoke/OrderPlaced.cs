using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.AotSmoke;

[OutboxMessage]
public sealed record OrderPlaced(string OrderId, decimal Total);
