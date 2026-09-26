---
id: testing
title: Testing
sidebar_position: 11
---

# Testing

## InMemory store

`ZeroAlloc.Outbox.InMemory` provides `InMemoryOutboxStore` — a thread-safe, in-process outbox store with no database dependency.

### Basic assertion pattern

```csharp
// After writing a message
await writer.WriteAsync(new OrderPlaced(42, 99.99m));

var store = services.GetRequiredService<InMemoryOutboxStore>();

// Assert the entry was written
store.AllEntries().Should().ContainSingle()
    .Which.TypeName.Should().Be("MyApp.OrderPlaced");
```

### Asserting dispatch

```csharp
// After the worker has run
store.AllEntries().Should().ContainSingle()
    .Which.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);
```

### Asserting dead-letter

```csharp
store.AllEntries().Should().ContainSingle()
    .Which.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.DeadLetter);
```

## Integration test with `HostBuilder`

Use a real hosted worker to test the full pipeline:

```csharp
[Fact]
public async Task OrderPlaced_IsDispatched()
{
    var dispatched = new List<OrderPlaced>();

    using var host = await new HostBuilder()
        .ConfigureServices(services =>
        {
            services.AddOutbox(o => { o.PollingInterval = TimeSpan.FromMilliseconds(50); })
                    .WithInMemoryStore()
                    .AddOrderPlacedOutbox();
            services.AddTransient<IOutboxDispatcher<OrderPlaced>>(
                _ => new DelegateDispatcher<OrderPlaced>(msg =>
                {
                    dispatched.Add(msg);
                    return Task.CompletedTask;
                }));
        })
        .StartAsync();

    var writer = host.Services.GetRequiredService<IOutboxWriter<OrderPlaced>>();
    await writer.WriteAsync(new OrderPlaced(1, 50m));

    await Task.Delay(200); // let the worker poll

    dispatched.Should().ContainSingle().Which.OrderId.Should().Be(1);

    var store = host.Services.GetRequiredService<InMemoryOutboxStore>();
    store.AllEntries().Single().Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);

    await host.StopAsync();
}
```

See [Testing with Host](cookbook/05-testing-with-host.md) for a full helper class that encapsulates the setup.

## Testing without the worker

If you want to test only the write side (not dispatch), skip `AddOutbox()` and call `store.ClaimPendingAsync` + your dispatcher manually, or simply inspect `AllEntries()`. A message must be claimed before `MarkSucceededAsync`, `MarkFailedAsync` or `DeadLetterAsync` will do anything to it — each is a conditional update that only applies to a row this host currently leases, so marking a freshly-enqueued row directly, without claiming it first, returns `false` and changes nothing.

### Claiming before marking

To drive a message through its states by hand, claim it under a lease and mark it with that same
lease:

```csharp
var store = new InMemoryOutboxStore();
await store.EnqueueAsync("MyApp.OrderPlaced", payload, transaction: null, ct);

var lease = new OutboxLease("test-host", TimeSpan.FromMinutes(1));
var claimed = await store.ClaimPendingAsync(batchSize: 10, lease, ct);
var id = claimed.Should().ContainSingle().Subject.Id;

// Marking with the lease that claimed the message moves it and returns true.
(await store.MarkSucceededAsync(id, lease, ct)).Should().BeTrue();

// A second mark, or a mark under a lease that never claimed the message, returns false.
(await store.MarkSucceededAsync(id, lease, ct)).Should().BeFalse();
```

The same pattern works against the EF Core and ORM stores. To seed a failed or dead-lettered
message, claim it, then call `MarkFailedAsync` or `DeadLetterAsync` with the lease; a message
marked failed is claimable again once its `nextRetryAt` has passed.
