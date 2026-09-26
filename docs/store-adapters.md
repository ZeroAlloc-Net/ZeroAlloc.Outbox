---
id: store-adapters
title: Store Adapters
sidebar_position: 6
---

# Store Adapters

## EF Core store (`ZeroAlloc.Outbox.EfCore`)

### Registration

```csharp
builder.Services.AddOutbox()
        .WithEfCore<AppDbContext>();
```

This registers `EfCoreOutboxStore` as `IOutboxStore` (scoped) and wires the `OutboxMessageEntity` configuration into the provided `DbContext`.

### Schema

A single table `OutboxMessages` is added with these columns:

| Column | Type | Notes |
|--------|------|-------|
| `Id` | `OutboxMessageId` | Primary key, generated client-side (`OutboxMessageId.New()`) |
| `TypeName` | `nvarchar(256)` | Fully-qualified type discriminator |
| `Payload` | `varbinary(max)` | Serialized message bytes |
| `Status` | `int` | `0` = Pending, `1` = Succeeded, `2` = DeadLetter |
| `RetryCount` | `int` | Number of failed dispatch attempts |
| `CreatedAt` | `datetimeoffset` | UTC time of enqueue |
| `NextRetryAt` | `datetimeoffset` | Earliest time to retry |
| `DeadLetterError` | `nvarchar(2000)?` | Last failure reason or dead-letter reason |
| `LockedBy` | `nvarchar(128)?` | Host holding the current lease, or `NULL` if unleased |
| `LockedUntil` | `datetimeoffset?` | When the current lease expires, or `NULL` if unleased |

An index on `(Status, NextRetryAt)` covers the `ClaimPendingAsync` query; no new index is needed for `LockedBy`/`LockedUntil`.

The claim has no `UPDATE … RETURNING` to rely on, so it selects candidate ids, leases them with a conditional `ExecuteUpdate` that re-checks the unleased predicate, retries up to 3 times if a concurrent claimer took some of them first, then reads back the rows this call leased. Under heavy contention with many hosts this contends more than the ORM store's single locking statement (below) and a claim can come back empty, just triggering a re-poll — prefer the ORM store over EF Core for large scale-out.

> **SQL Server:** the claim and `ReleaseLeasesAsync` translate `Contains` over the candidate ids to `OPENJSON`, which requires database compatibility level 130 or higher.

### Migration

The store does not auto-migrate. Add a migration after adding the store, and again after upgrading to v3.0 for the `LockedBy`/`LockedUntil` columns:

```bash
dotnet ef migrations add AddOutboxMessages --project src/YourProject.EfCore
dotnet ef migrations add AddOutboxLease --project src/YourProject.EfCore
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

## ORM store (`ZeroAlloc.Outbox.Orm`)

### Registration

```csharp
builder.Services.AddOutbox()
        .WithOrm(OutboxOrmDialect.Postgres); // or .Sqlite (default) / .SqlServer
```

This registers `OrmOutboxStore` as `IOutboxStore` (scoped) against the `IAsyncDbConnection` already registered in the container — register that connection yourself; its lifetime, provider and connection string belong to the application, not to the outbox. `WithOrm()` with no dialect argument defaults to SQLite. The dialect only selects the batch-claim statement; every other statement the store issues is plain ANSI SQL shared across all three.

> No `IOutboxDashboardStore` is registered for this store. The dashboard's aggregate-view surface is much larger and isn't implemented here, so pointing the dashboard at the ORM store fails at registration instead of silently reporting nothing.

### Schema

The same `OutboxMessages` table as the EF Core adapter — the two stores can be swapped without a data migration — created by `OutboxOrmMigrations`, not by the store itself; the store does not create tables on the fly.

| Column | Type (Postgres) | Notes |
|--------|------|-------|
| `Id` | `UUID` | Primary key |
| `TypeName` | `VARCHAR(256)` | Fully-qualified type discriminator |
| `Payload` | `BYTEA` | Serialized message bytes |
| `Status` | `INTEGER` | `0` = Pending, `1` = Succeeded, `2` = DeadLetter |
| `RetryCount` | `INTEGER` | Number of failed dispatch attempts |
| `NextRetryAt` | `TIMESTAMPTZ` | Earliest time to retry |
| `CreatedAt` | `TIMESTAMPTZ` | UTC time of enqueue |
| `ProcessedAt` | `TIMESTAMPTZ?` | When the message succeeded or was dead-lettered |
| `DeadLetterError` | `TEXT?` | Last failure reason or dead-letter reason |
| `LockedBy` | `VARCHAR(128)?` | Host holding the current lease, or `NULL` if unleased |
| `LockedUntil` | `TIMESTAMPTZ?` | When the current lease expires, or `NULL` if unleased |

SQLite (`TEXT`/`BLOB` columns) and SQL Server (`NVARCHAR`/`UNIQUEIDENTIFIER`/`DATETIMEOFFSET` columns) use the same shape with their own provider types — see `OutboxOrmMigrations`.

### Migration

Hand `OutboxOrmMigrations` and the matching ORM dialect to ZeroAlloc.ORM's `MigrationRunner`:

```csharp
var runner = new MigrationRunner(connection, OutboxOrmMigrations.Postgres, new PostgresMigrationDialect());
await runner.RunAsync(ct);
```

`OutboxOrmMigrations.Sqlite`, `.Postgres` and `.SqlServer` each carry two migrations: migration 1, `create_outbox_messages`, creates the table and the `(Status, NextRetryAt)` index; migration 2, `add_outbox_lease`, adds the nullable `LockedBy` and `LockedUntil` columns. Existing rows get `NULL` in the lease columns, meaning unleased and immediately claimable.

Migration 1 is guarded on every dialect (`IF NOT EXISTS` on SQLite and Postgres, an `OBJECT_ID` check on SQL Server). Migration 2 is guarded on Postgres (`ADD COLUMN IF NOT EXISTS`) and SQL Server (a `COL_LENGTH` check), but **not on SQLite**: it is a plain `ALTER TABLE … ADD COLUMN`, and SQLite has no way to make that conditional in SQL.

> **Switching a SQLite database from the EF Core store to the ORM store:** the EF Core model already created `LockedBy` and `LockedUntil`, so migration 2 fails with a duplicate column error. Record it as applied before the first `MigrationRunner` run, in ZeroAlloc.ORM's history table:
>
> ```sql
> CREATE TABLE IF NOT EXISTS __zaorm_migrations (
>     version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);
> INSERT INTO __zaorm_migrations (version, name, applied_at)
> VALUES (2, 'add_outbox_lease', datetime('now'));
> ```
>
> Migration 1 then runs as a no-op against the existing table and index. On Postgres and SQL Server no step is needed.

### Claim

Unlike the EF Core adapter, each dialect claims a batch in **one** statement — no candidate select, retry loop or read-back:

- **SQLite** — `UPDATE OutboxMessages SET LockedBy = @hostId, LockedUntil = @until WHERE Id IN (SELECT Id … ORDER BY CreatedAt LIMIT @batchSize) AND (LockedUntil IS NULL OR LockedUntil < @now) RETURNING …`. SQLite serializes writers, so the whole statement runs atomically and two claimers can never take the same row.
- **Postgres** — a `MATERIALIZED` CTE that locks the batch with `FOR UPDATE SKIP LOCKED`, then `UPDATE OutboxMessages … FROM` that CTE with `RETURNING`. `MATERIALIZED` forces the locking CTE to run exactly once as its own step, so the rows it locks are exactly the rows the update touches and returns — an inlined or non-materialized CTE would let the planner fold or re-evaluate it, breaking that guarantee. After taking each row lock, PostgreSQL re-checks the CTE's own `WHERE` against the latest row version, so the unleased predicate is enforced under the lock, not against a stale read.
- **SQL Server** — an updatable `TOP` CTE over the table hinted `WITH (ROWLOCK, READPAST, UPDLOCK)`, updated with `OUTPUT inserted.…`. An updatable CTE carries the claim order because `UPDATE TOP` alone has no `ORDER BY`. `UPDLOCK` holds the selected rows until the statement commits; `READPAST` lets a concurrent claimer skip rows another claimer already locked instead of blocking on them and then claiming the same rows once that transaction commits; `ROWLOCK` keeps the locks at row granularity so `READPAST` has rows to skip.

`ReleaseLeasesAsync` runs as one statement per id rather than a single `IN (…)` list — ZeroAlloc.ORM binds a collection parameter only as bulk-insert rows, not as an expanded `IN` list. The list released this way is at most one batch, and only when a batch ends early, because the worker is stopping or a store call failed, so the extra round trips don't matter in practice.

Because each dialect claims a whole batch in one locking statement instead of EF Core's select-then-conditional-update-then-retry, the ORM store contends less as more hosts poll the same table concurrently, and a claim never comes back empty while work is waiting just because another claimer got there first — **prefer the ORM store over EF Core when scaling out to many hosts.**

---

## InMemory store (`ZeroAlloc.Outbox.InMemory`)

### Registration

```csharp
builder.Services.AddOutbox()
        .WithInMemoryStore();
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
