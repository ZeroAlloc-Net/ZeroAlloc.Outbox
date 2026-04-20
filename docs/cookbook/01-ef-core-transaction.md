---
id: 01-ef-core-transaction
title: EF Core Transaction
sidebar_position: 1
---

# EF Core Transaction

Write an outbox message in the same `DbTransaction` as your domain change so both commit or both roll back atomically.

## Pattern

```csharp
public class OrderService(
    AppDbContext db,
    IOutboxWriter<OrderPlaced> writer)
{
    public async Task PlaceOrderAsync(NewOrder request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var order = new Order { /* ... */ };
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);

            // Enlist the outbox write in the same transaction
            await writer.WriteAsync(
                new OrderPlaced(order.Id, order.Total),
                tx.GetDbTransaction(),
                ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
```

## Key points

- Pass `tx.GetDbTransaction()` as the second argument to `WriteAsync`. The EF Core store uses `UseTransactionAsync` to enlist in the caller's transaction.
- If `SaveChangesAsync` or `WriteAsync` throws, the `catch` block rolls back both the order row and the outbox entry.
- The outbox worker will not see the entry until the transaction commits, so there is no risk of dispatching a message for a rolled-back domain change.

## Without an explicit transaction

If you do not pass a transaction, the store writes in its own implicit transaction:

```csharp
// This is NOT atomic — order and outbox are separate transactions
db.Orders.Add(order);
await db.SaveChangesAsync(ct);
await writer.WriteAsync(new OrderPlaced(order.Id, order.Total), ct: ct);
```

Use the explicit transaction pattern for production code.
