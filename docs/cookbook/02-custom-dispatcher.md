---
id: 02-custom-dispatcher
title: Custom Dispatcher
sidebar_position: 2
---

# Custom Dispatcher

Implement `IOutboxDispatcher<T>` to send messages to any destination.

## HTTP dispatcher example

```csharp
public class OrderPlacedHttpDispatcher(HttpClient http) : IOutboxDispatcher<OrderPlaced>
{
    public async Task DispatchAsync(OrderPlaced message, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/webhooks/order-placed", message, ct);
        response.EnsureSuccessStatusCode();
    }
}
```

Register with a named `HttpClient`:

```csharp
builder.Services.AddHttpClient<OrderPlacedHttpDispatcher>(c =>
{
    c.BaseAddress = new Uri("https://downstream.example.com");
});
builder.Services.AddTransient<IOutboxDispatcher<OrderPlaced>, OrderPlacedHttpDispatcher>();
```

## Email dispatcher example

```csharp
public class OrderPlacedEmailDispatcher(IEmailSender email) : IOutboxDispatcher<OrderPlaced>
{
    public Task DispatchAsync(OrderPlaced message, CancellationToken ct)
        => email.SendAsync(
            to: "ops@example.com",
            subject: $"Order {message.OrderId} placed",
            body: $"Amount: {message.Amount:C}",
            ct);
}
```

## Idempotency

The outbox worker uses **at-least-once delivery**. If the process crashes after dispatch but before the success mark is written, the message will be dispatched again on the next poll. Implement idempotency in your dispatcher or downstream consumer if duplicates are a concern.

A common pattern is to include a unique correlation ID in the message payload and use it as an idempotency key in your downstream consumer.
