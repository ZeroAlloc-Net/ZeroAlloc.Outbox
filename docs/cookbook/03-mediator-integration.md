---
id: 03-mediator-integration
title: Mediator Integration
sidebar_position: 3
---

# Mediator Integration

Use `ZeroAlloc.Mediator` as the transport for outbox dispatchers.

## Setup

Add both packages:

```bash
dotnet add package ZeroAlloc.Outbox
dotnet add package ZeroAlloc.Outbox.Generator
dotnet add package ZeroAlloc.Outbox.EfCore
dotnet add package ZeroAlloc.Mediator
dotnet add package ZeroAlloc.Mediator.Generator
```

## Message as a notification

Annotate the type as both an outbox message and a Mediator notification:

```csharp
[OutboxMessage]
public sealed record OrderPlaced(int OrderId, decimal Amount) : INotification;
```

## Dispatcher

```csharp
public class OrderPlacedDispatcher(IMediator mediator) : IOutboxDispatcher<OrderPlaced>
{
    public Task DispatchAsync(OrderPlaced message, CancellationToken ct)
        => mediator.PublishAsync(message, ct);
}
```

## Registration

```csharp
builder.Services.AddOutbox();
builder.Services.AddOutboxEfCore<AppDbContext>();
builder.Services.AddOrderPlacedOutbox();
builder.Services.AddTransient<IOutboxDispatcher<OrderPlaced>, OrderPlacedDispatcher>();

builder.Services.AddMediator(); // ZeroAlloc.Mediator
```

Handlers registered with Mediator are called when the outbox worker dispatches the message — after the original transaction commits.
