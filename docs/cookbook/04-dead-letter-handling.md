---
id: 04-dead-letter-handling
title: Dead-Letter Handling
sidebar_position: 4
---

# Dead-Letter Handling

## What is a dead-lettered entry?

An outbox entry is dead-lettered when:
- The dispatcher throws an exception on `MaxAttempts` consecutive attempts.
- No `IOutboxTypeDispatcher` is registered for the entry's `TypeName`.

Dead-lettered entries have `Status = 2` (`DeadLettered`) and a non-null `ErrorMessage`. The worker never re-fetches them.

## Inspecting dead-lettered entries (EF Core)

Query the outbox table directly:

```csharp
var deadLettered = await db.Set<OutboxMessageEntity>()
    .Where(e => e.Status == OutboxMessageStatus.DeadLettered)
    .OrderBy(e => e.CreatedAt)
    .ToListAsync(ct);
```

## Requeuing a dead-lettered entry

Reset the entry so the worker picks it up again:

```csharp
var entry = await db.Set<OutboxMessageEntity>().FindAsync([id], ct)
    ?? throw new KeyNotFoundException(id.ToString());

entry.Status     = OutboxMessageStatus.Pending;
entry.RetryCount = 0;
entry.NextRetryAt = null;
entry.ErrorMessage = null;

await db.SaveChangesAsync(ct);
```

## Alerting

Hook into your monitoring stack by querying dead-lettered entries on a schedule:

```csharp
// Example: periodic health check that alerts if dead-lettered count > 0
var count = await db.Set<OutboxMessageEntity>()
    .CountAsync(e => e.Status == OutboxMessageStatus.DeadLettered, ct);

if (count > 0)
    logger.LogError("Outbox has {Count} dead-lettered message(s). Manual intervention required.", count);
```

## InMemory dead-letter (tests)

```csharp
store.AllEntries()
    .Where(e => e.Status == InMemoryOutboxStore.InMemoryEntryStatus.DeadLettered)
    .Should().BeEmpty("no messages should be dead-lettered");
```
