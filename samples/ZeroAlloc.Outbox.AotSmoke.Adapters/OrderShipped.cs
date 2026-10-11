using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox.AotSmoke.Adapters;

// Dispatched through ZeroAlloc.Outbox.Mediator: MediatorOutboxDispatcher<T> hands it to every
// registered INotificationHandler<OrderShipped>.
[OutboxMessage]
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record OrderShipped(string OrderId) : INotification;
