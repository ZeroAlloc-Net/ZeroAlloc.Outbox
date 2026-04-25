---
id: 03-mediator-integration
title: Mediator Integration
sidebar_position: 3
---

# Mediator Integration

The `ZeroAlloc.Outbox.Mediator` package provides a ready-made `IOutboxDispatcher<T>` implementation that fans out to all registered `INotificationHandler<T>` implementations via ZeroAlloc.Mediator.

## Installation

```bash
dotnet add package ZeroAlloc.Outbox
dotnet add package ZeroAlloc.Outbox.Mediator
dotnet add package ZeroAlloc.Mediator
dotnet add package ZeroAlloc.Mediator.Generator
```

## Message as a Notification

Annotate the type as both an outbox message and a Mediator notification:

```csharp
using ZeroAlloc.Outbox;
using ZeroAlloc.Mediator;

[OutboxMessage]
public sealed record OrderPlaced(int OrderId, decimal Amount) : INotification;
```

## Registration

```csharp
services.AddMediator();               // ZeroAlloc.Mediator
services.AddOutbox()
        .AddOutboxEfCore<AppDbContext>()
        .AddOrderPlacedOutbox();

// Wire MediatorOutboxDispatcher<OrderPlaced> as IOutboxDispatcher<OrderPlaced>
services.AddOutboxMediator<OrderPlaced>();
```

`AddOutboxMediator<T>()` registers `MediatorOutboxDispatcher<T>` as `IOutboxDispatcher<T>`. The dispatcher calls each `INotificationHandler<T>` sequentially when the outbox worker dispatches the message — after the original transaction commits.

## Handlers

Implement handlers as normal ZeroAlloc.Mediator notification handlers:

```csharp
public sealed class SendConfirmationEmailHandler : INotificationHandler<OrderPlaced>
{
    private readonly IEmailService _email;
    public SendConfirmationEmailHandler(IEmailService email) => _email = email;

    public async ValueTask Handle(OrderPlaced notification, CancellationToken ct)
        => await _email.SendOrderConfirmationAsync(notification.OrderId, ct);
}
```

The ZeroAlloc.Mediator generator registers handlers automatically. Multiple handlers for the same notification are all called.

## Manual Dispatcher (Alternative)

If you need custom dispatch logic, implement `IOutboxDispatcher<T>` directly and call the mediator yourself:

```csharp
public sealed class OrderPlacedDispatcher(IMediator mediator) : IOutboxDispatcher<OrderPlaced>
{
    public Task DispatchAsync(OrderPlaced message, CancellationToken ct)
        => mediator.PublishAsync(message, ct);
}

// Registration
services.AddTransient<IOutboxDispatcher<OrderPlaced>, OrderPlacedDispatcher>();
```
