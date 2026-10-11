using ZeroAlloc.Outbox;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox.AotSmoke.Adapters;

// Dispatched through ZeroAlloc.Outbox.Resilience: the proxy the Resilience generator emits for
// IResilientOrderPaidDispatcher retries the dispatcher behind it.
[OutboxMessage]
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record OrderPaid(string OrderId);
