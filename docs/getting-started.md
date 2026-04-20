# Getting Started

## 1. Install the NuGet packages

```bash
dotnet add package ZeroAlloc.Outbox
dotnet add package ZeroAlloc.Outbox.EfCore   # or ZeroAlloc.Outbox.InMemory for tests
```

---

## 2. Annotate your message type

Apply `[OutboxMessage]` to any class, record, or struct.

```csharp
using ZeroAlloc.Outbox;

[OutboxMessage]
public sealed record OrderPlaced(Guid OrderId, decimal Amount);
```

The source generator emits a typed `IOutboxWriter<OrderPlaced>`, an `IOutboxTypeDispatcher`, and
a `AddOrderPlacedOutbox()` DI extension method for each annotated type.

---

## 3. Configure the DB schema

In your `DbContext.OnModelCreating`:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.AddOutboxMessages();
}
```

Then add and apply a migration:

```bash
dotnet ef migrations add AddOutboxMessages
dotnet ef database update
```

---

## 4. Register with DI

```csharp
// Add the outbox background worker + default serializer
builder.Services.AddOutbox(options =>
{
    options.MaxAttempts     = 5;
    options.BatchSize       = 50;
    options.PollingInterval = TimeSpan.FromSeconds(5);
    options.RetryBaseDelay  = TimeSpan.FromSeconds(2);
});

// Add the EF Core store (wraps your existing DbContext)
builder.Services.AddOutboxEfCore<AppDbContext>();

// Source-generated extension method — one per [OutboxMessage] type
builder.Services.AddOrderPlacedOutbox();

// Register your dispatcher for each message type
builder.Services.AddTransient<IOutboxDispatcher<OrderPlaced>, OrderPlacedEmailDispatcher>();
```

---

## 5. Write in the same transaction

```csharp
public class PlaceOrderHandler(IOutboxWriter<OrderPlaced> outbox, AppDbContext db)
{
    public async Task Handle(PlaceOrderCommand cmd, CancellationToken ct)
    {
        db.Orders.Add(new Order(cmd));
        await db.SaveChangesAsync(ct);

        // Enlist in the same DB transaction for atomic write
        await outbox.WriteAsync(new OrderPlaced(cmd.Id, cmd.Total), ct: ct);
    }
}
```

The background worker polls the `OutboxMessages` table, deserializes each entry, and calls your
`IOutboxDispatcher<OrderPlaced>`. On success the entry is marked `Succeeded`; on repeated failure
it is dead-lettered after `MaxAttempts` attempts using exponential back-off.

---

## 6. Implement your dispatcher

```csharp
public sealed class OrderPlacedEmailDispatcher(IEmailService email)
    : IOutboxDispatcher<OrderPlaced>
{
    public async ValueTask DispatchAsync(OrderPlaced message, CancellationToken ct)
    {
        await email.SendOrderConfirmationAsync(message.OrderId, ct);
    }
}
```

---

## 7. Testing with InMemory

Replace the EF Core store with the in-memory adapter for unit and integration tests:

```csharp
// In your test host setup
services.AddOutboxInMemory();   // replaces AddOutboxEfCore<T>()

// Inspect store state directly for assertions
var store = host.Services.GetRequiredService<InMemoryOutboxStore>();
store.AllEntries().Should().ContainSingle()
     .Which.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);
```

---

## Options reference

| Property | Default | Description |
|---|---|---|
| `MaxAttempts` | `5` | Dispatch attempts before dead-lettering |
| `BatchSize` | `50` | Messages fetched per polling cycle |
| `PollingInterval` | `5 s` | Delay between polling cycles |
| `RetryBaseDelay` | `2 s` | Base delay for exponential back-off (`delay × 2^attempt`) |
