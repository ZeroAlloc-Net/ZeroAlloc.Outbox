---
id: dispatchers
title: Dispatchers
sidebar_position: 5
---

# Dispatchers

## `IOutboxDispatcher<T>`

Implement one method to send a deserialized message to its destination:

```csharp
public interface IOutboxDispatcher<T>
{
    Task DispatchAsync(T message, CancellationToken ct);
}
```

Example — publishing to a message broker:

```csharp
public class OrderPlacedDispatcher(IMessageBus bus) : IOutboxDispatcher<OrderPlaced>
{
    public Task DispatchAsync(OrderPlaced message, CancellationToken ct)
        => bus.PublishAsync(message, ct);
}
```

Register it:

```csharp
builder.Services.AddTransient<IOutboxDispatcher<OrderPlaced>, OrderPlacedDispatcher>();
```

## `IOutboxTypeDispatcher` (internal bridge)

The generator also emits an `OrderPlacedOutboxTypeDispatcher` that implements the non-generic `IOutboxTypeDispatcher`. This is the interface the background worker uses internally to dispatch by type name string without reflection. You never implement this interface yourself.

```csharp
// Generated — do not implement manually
internal sealed class OrderPlacedOutboxTypeDispatcher : IOutboxTypeDispatcher
{
    public string TypeName => "MyApp.OrderPlaced";

    public Task DispatchAsync(byte[] payload, CancellationToken ct)
    {
        var message = _serializer.Deserialize<OrderPlaced>(payload);
        return _dispatcher.DispatchAsync(message, ct);
    }
}
```

## Custom dispatchers

Any transport works. Examples:

| Transport | Pattern |
|-----------|---------|
| Message broker | Inject `IMessageBus` / `IPublisher` and publish |
| HTTP | Inject `HttpClient` and `POST` |
| Email | Inject `IEmailSender` and send |
| ZeroAlloc.Mediator | Inject `IMediator` and `SendAsync` / `PublishAsync` |

See [Mediator Integration](cookbook/03-mediator-integration.md) for the last case.
