---
id: store-adapters
title: Store Adapters
sidebar_position: 6
---

# Store Adapters

## EF Core store (`ZeroAlloc.Outbox.EfCore`)

### Registration

```csharp
builder.Services.AddOutboxEfCore<AppDbContext>();
```

This registers `EfCoreOutboxStore` as `IOutboxStore` (scoped) and wires the `OutboxMessageEntity` configuration into the provided `DbContext`.

### Schema

A single table `OutboxMessages` is added with these columns:

| Column | Type | Notes |
|--------|------|-------|
| `Id` | `Guid` | Primary key, generated client-side (`Guid.NewGuid()`) |
| `TypeName` | `nvarchar(500)` | Fully-qualified type discriminator |
| `Payload` | `varbinary(max)` | Serialized message bytes |
| `Status` | `int` | `0` = Pending, `1` = Succeeded, `2` = DeadLettered |
| `RetryCount` | `int` | Number of failed dispatch attempts |
| `CreatedAt` | `datetimeoffset` | UTC time of enqueue |
| `NextRetryAt` | `datetimeoffset?` | Earliest time to retry (null = immediately eligible) |
| `ErrorMessage` | `nvarchar(2000)?` | Last failure reason or dead-letter reason |

An index on `(Status, NextRetryAt)` covers the `FetchPendingAsync` query.

### Migration

The store does not auto-migrate. Add a migration after adding the store:

```bash
dotnet ef migrations add AddOutboxMessages --project src/YourProject.EfCore
dotnet ef database update
```

### Transactional enqueue

Pass the ambient `DbTransaction` to `IOutboxWriter<T>.WriteAsync` to enlist the enqueue in the caller's transaction:

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);
db.Orders.Add(order);
await db.SaveChangesAsync(ct);
await writer.WriteAsync(new OrderPlaced(order.Id), tx.GetDbTransaction(), ct);
await tx.CommitAsync(ct);
```

See [EF Core Transaction](cookbook/01-ef-core-transaction.md) for the complete pattern.

---

## InMemory store (`ZeroAlloc.Outbox.InMemory`)

### Registration

```csharp
builder.Services.AddOutboxInMemory();
```

Registers `InMemoryOutboxStore` as both `IOutboxStore` and `InMemoryOutboxStore` (singleton) so tests can inspect entries directly.

### Test usage

```csharp
var store = host.Services.GetRequiredService<InMemoryOutboxStore>();

// Assert entry was written
store.AllEntries().Should().ContainSingle();

// Assert dispatched
store.AllEntries().Single().Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);
```

The `AllEntries()` method returns a snapshot — it does not lock the store, so call it after the worker has had time to process.

See [Testing](testing.md) for integration test patterns using `HostBuilder` + InMemory store.
