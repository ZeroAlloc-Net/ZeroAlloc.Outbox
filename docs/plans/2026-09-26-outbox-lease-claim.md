# Outbox Lease-Based Claim Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop two hosts that poll one outbox from dispatching the same message, by replacing the plain pending fetch with an atomic lease-based claim that is renewed before each dispatch.

**Architecture:**
- **Store contract.** `IOutboxStore.FetchPendingAsync` becomes `ClaimPendingAsync(batchSize, OutboxLease, ct)`. It atomically marks up to N due, unleased rows with `LockedBy`/`LockedUntil`, and returns only those rows. A new `RenewLeaseAsync` extends one row's lease, and reports whether this host still holds it.
- **Worker.** The worker claims a batch, renews each entry right before dispatching it, and skips any entry whose lease it lost. `Mark*` clears the lease.
- **Coverage.** Three stores (ORM, EF Core, InMemory) and two nullable columns.

**Tech Stack:** .NET (net8/9/10), ZeroAlloc.ORM (source-generated SQL), EF Core 9, xUnit, AwesomeAssertions, Testcontainers (MsSql, and PostgreSql to add).

**Spec:** `docs/plans/2026-09-26-outbox-lease-claim-design.md`. Read it first; it is the authority.

## Global Constraints

- Ships as **ZeroAlloc.Outbox 3.0.0**. A single PR, `feat!:` with a `BREAKING CHANGE:` footer and `Closes #191`. Commit body lines are at most 100 characters, with no nested parentheses and no claude.ai session URL. End each commit with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **No default interface implementations** for the new `IOutboxStore` members: a custom store must implement claiming explicitly.
- The **new public API** is exactly as the spec defines:
  - `OutboxLease(string HostId, TimeSpan Duration)`
  - `ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)`
  - `ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)`
  - `OutboxOptions.LeaseDuration`, which defaults to 5 minutes and must be greater than zero
  - `OutboxOptions.HostId`, which defaults to `$"{Environment.MachineName}:{Guid.NewGuid():N}"`, and must be non-empty and at most 128 characters
- **Schema:** the nullable columns `LockedBy` and `LockedUntil`, with the per-dialect types in the spec's table. ORM migration 2 is named `add_outbox_lease`.
- Record every public API change in `src/*/PublicAPI.Unshipped.txt`. The Release build must have 0 warnings, with no suppressions.
- Build and test from the repo root with `-c Release`. Docker is available for Testcontainers. Run heavy test suites one at a time.

## File Structure

| File | Responsibility |
|---|---|
| `src/ZeroAlloc.Outbox/OutboxLease.cs` (new) | The lease value type |
| `src/ZeroAlloc.Outbox/IOutboxStore.cs` | The claim and renew contract |
| `src/ZeroAlloc.Outbox/OutboxOptions.cs`, plus a validator | `LeaseDuration`, `HostId` and validation |
| `src/ZeroAlloc.Outbox/OutboxWorkerService.cs` | Claim, renew-before-dispatch, skip on a lost lease |
| `src/ZeroAlloc.Outbox.InMemory/InMemoryOutboxStore.cs` | The in-memory claim and lease |
| `src/ZeroAlloc.Outbox.Orm/OutboxOrmMigrations.cs` | Migration 2 |
| `src/ZeroAlloc.Outbox.Orm/*PendingOutboxQuery.cs`, `IPendingOutboxQuery.cs`, `OutboxMessageRepository.cs`, `OrmOutboxStore.cs` | The ORM claim, renew and release |
| `src/ZeroAlloc.Outbox.EfCore/OutboxMessageEntity.cs`, `OutboxDbContextExtensions.cs`, `EfCoreOutboxStore.cs` | EF columns, the claim, renew and release |
| `tests/…` | The tests listed per task |
| `docs/…` | The docs and the migration guide |

---

### Task 1: Schema: ORM migration 2 and the EF Core columns

**Files:**
- Modify: `src/ZeroAlloc.Outbox.Orm/OutboxOrmMigrations.cs`
- Modify: `src/ZeroAlloc.Outbox.EfCore/OutboxMessageEntity.cs` and `OutboxDbContextExtensions.cs`
- Test: `tests/ZeroAlloc.Outbox.Orm.Tests/OutboxMigrationTests.cs` (new), for SQLite, plus a SQL Server case in `SqlServerOutboxTests.cs`

**Interfaces:**
- Produces:
  - DB columns `LockedBy` and `LockedUntil`, nullable.
  - `OutboxMessageEntity.LockedBy` (`string?`) and `OutboxMessageEntity.LockedUntil` (`DateTimeOffset?`).

- [ ] **Step 1: Failing test.** Run migration 1 only, using a migration source that returns just the first migration, which you get by taking the first element of `GetMigrations()`. Insert one row, then run the full `OutboxOrmMigrations.Sqlite`. Assert that both columns exist, with `PRAGMA table_info(OutboxMessages)`, and that the pre-existing row has NULL `LockedBy` and `LockedUntil`. Add the equivalent SQL Server test using `INFORMATION_SCHEMA.COLUMNS`.
- [ ] **Step 2: Run it** with `dotnet test tests/ZeroAlloc.Outbox.Orm.Tests -c Release --filter "FullyQualifiedName~Migration"`. It should fail, because the columns are missing.
- [ ] **Step 3: Implement.** Change `Source` to take several migrations, `new Migration(1, "create_outbox_messages", create)` followed by `new Migration(2, "add_outbox_lease", lease)`, where `lease` is:
  - SQLite: `ALTER TABLE OutboxMessages ADD COLUMN LockedBy TEXT NULL; ALTER TABLE OutboxMessages ADD COLUMN LockedUntil TEXT NULL;`
  - Postgres: `ALTER TABLE OutboxMessages ADD COLUMN IF NOT EXISTS LockedBy VARCHAR(128) NULL; ALTER TABLE OutboxMessages ADD COLUMN IF NOT EXISTS LockedUntil TIMESTAMPTZ NULL;`
  - SQL Server: `IF COL_LENGTH(N'OutboxMessages', N'LockedBy') IS NULL ALTER TABLE OutboxMessages ADD LockedBy NVARCHAR(128) NULL;` and the same pattern for `LockedUntil DATETIMEOFFSET NULL`.

  Check how `MigrationRunner` executes multi-statement SQL for each dialect, and split the migration into separate `Migration` entries if one migration can only run a single statement.

  EF: add `public string? LockedBy { get; set; }` and `public DateTimeOffset? LockedUntil { get; set; }` to the entity, and `entity.Property(e => e.LockedBy).HasMaxLength(128);` to the mapping.
- [ ] **Step 4: Run** the Orm tests and `tests/ZeroAlloc.Outbox.Tests`. Everything should pass, and existing EF tests still pass because EF creates the schema with `EnsureCreated`.
- [ ] **Step 5: Commit** with `feat(orm): add lease columns to the outbox schema`.

---

### Task 2: Contract, worker and all three stores

**Files:**
- Create: `src/ZeroAlloc.Outbox/OutboxLease.cs`
- Modify: `src/ZeroAlloc.Outbox/IOutboxStore.cs`, `OutboxOptions.cs`, `OutboxServiceCollectionExtensions.cs` (options validation), `OutboxWorkerService.cs` and `PublicAPI.Unshipped.txt` (plus `PublicAPI.Shipped.txt` for removed members, as the analyzer requires)
- Modify: `src/ZeroAlloc.Outbox.InMemory/InMemoryOutboxStore.cs`
- Modify: `src/ZeroAlloc.Outbox.Orm/IPendingOutboxQuery.cs`, `LimitPendingOutboxQuery.cs`, `FetchFirstPendingOutboxQuery.cs`, `OutboxMessageRepository.cs` and `OrmOutboxStore.cs`
- Modify: `src/ZeroAlloc.Outbox.EfCore/EfCoreOutboxStore.cs`
- Every caller of `FetchPendingAsync` in `src/` and `tests/`: rename them, with `grep -rn FetchPendingAsync`.
- Test: `tests/ZeroAlloc.Outbox.Tests/InMemoryOutboxStoreClaimTests.cs` (new), `OutboxWorkerTests.cs` and `OutboxOptionsValidationTests.cs` (new); `tests/ZeroAlloc.Outbox.Orm.Tests/OrmOutboxClaimTests.cs` (new, SQLite); `tests/ZeroAlloc.Outbox.Tests/EfCoreOutboxClaimTests.cs` (new, SQLite)

**Interfaces:**
- Consumes: the Task 1 columns and entity properties.
- Produces:

```csharp
namespace ZeroAlloc.Outbox;
public readonly record struct OutboxLease(string HostId, TimeSpan Duration);

public interface IOutboxStore
{
    // EnqueueAsync and EnqueueDeferredAsync stay unchanged.
    ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct);
    ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct);
    // MarkSucceededAsync, MarkFailedAsync and DeadLetterAsync keep their signatures and now also clear LockedBy and LockedUntil.
}
```

Remove `FetchPendingAsync` from `IOutboxStore` and from the stores.

`OutboxOptions` gains `public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);` and `public string HostId { get; set; } = s_defaultHostId;`, where `private static readonly string s_defaultHostId = $"{Environment.MachineName}:{Guid.NewGuid():N}";`. Validate with an `IValidateOptions<OutboxOptions>` registered in `AddOutbox`: `LeaseDuration > TimeSpan.Zero`, and `HostId` non-empty with length at most 128. Put the failure messages in the test.

**Semantics shared by every store:**
- **Due and unleased:** `Status = Pending AND NextRetryAt <= @now AND (LockedUntil IS NULL OR LockedUntil < @now)`. Retries are stored as Pending, so they are included.
- **Claim:** set `LockedBy = lease.HostId` and `LockedUntil = @now + lease.Duration` on at most `batchSize` such rows, oldest `CreatedAt` first, and return exactly those rows.
- **Renew:** `UPDATE … SET LockedUntil = @now + Duration WHERE Id = @id AND LockedBy = @hostId AND LockedUntil >= @now AND Status = Pending`. It returns `true` if and only if one row changed. An expired lease can't be renewed, even by the same host, so a row that another host could already have claimed is never renewed.
- **Release:** every `Mark*` statement also sets `LockedBy = NULL, LockedUntil = NULL`.

**ORM SQL**, in `[Query]`/`[Command]` generated methods matching the existing style:
- **Postgres and SQLite**, in `LimitPendingOutboxQuery` (this path serves both dialects):

```sql
UPDATE OutboxMessages
SET LockedBy = @hostId, LockedUntil = @until
WHERE Id IN (
    SELECT Id FROM OutboxMessages
    WHERE Status = @status AND NextRetryAt <= @now
      AND (LockedUntil IS NULL OR LockedUntil < @now)
    ORDER BY CreatedAt
    LIMIT @batchSize
    /*POSTGRES*/ FOR UPDATE SKIP LOCKED)
  AND (LockedUntil IS NULL OR LockedUntil < @now)
RETURNING Id, TypeName, Payload, RetryCount, CreatedAt
```

  SQLite has no `FOR UPDATE SKIP LOCKED`, and doesn't need it because it serializes writers. Keep one SQL per query class, and give Postgres its own `ClaimPostgresPendingOutboxQuery` if needed. Choose the class from `OutboxOrmDialect` in `OrmOutboxStore`, which already distinguishes `Postgres`. Repeating the lease predicate in the outer `WHERE` makes Postgres re-check it after waiting on a row lock.

  Order `RETURNING` rows by `CreatedAt` in C# after the query, because `RETURNING` order is unspecified.
- **SQL Server**, in `FetchFirstPendingOutboxQuery`:

```sql
WITH claim AS (
    SELECT TOP (@batchSize) Id, TypeName, Payload, RetryCount, CreatedAt, LockedBy, LockedUntil
    FROM OutboxMessages WITH (ROWLOCK, READPAST, UPDLOCK)
    WHERE Status = @status AND NextRetryAt <= @now
      AND (LockedUntil IS NULL OR LockedUntil < @now)
    ORDER BY CreatedAt)
UPDATE claim SET LockedBy = @hostId, LockedUntil = @until
OUTPUT inserted.Id, inserted.TypeName, inserted.Payload, inserted.RetryCount, inserted.CreatedAt
```

  Then sort the output by `CreatedAt` in C#.

**EF Core claim.** This is provider-agnostic; EF has no `RETURNING`.
1. `var until = now + lease.Duration;` Then collect up to `batchSize` candidate ids, due and unleased and ordered by `CreatedAt`, with `Take(batchSize)` and `Select(e => e.Id)`.
2. Run `ExecuteUpdateAsync` on `Where(e => candidateIds.Contains(e.Id) && (e.LockedUntil == null || e.LockedUntil < now) && e.Status == Pending)`, setting `LockedBy = lease.HostId` and `LockedUntil = until`. The database re-checks the predicate on each row under its update lock, so a row that a concurrent claimer took in between is not updated. That's correct on Postgres and SQL Server at READ COMMITTED, and on SQLite, which serializes writers.
3. Load the rows `Where(e => candidateIds.Contains(e.Id) && e.LockedBy == lease.HostId && e.LockedUntil == until)` with `AsNoTracking`, ordered by `CreatedAt`. The same `until` value written in step 2 identifies exactly this claim's rows.
4. Renew and release go through `ExecuteUpdateAsync` with the same predicates as the shared semantics.
5. Make sure no tracked entities go stale. Existing `Mark*` code that loads entities must see the cleared lease, so clear `LockedBy`/`LockedUntil` in the same save or `ExecuteUpdate`.

**InMemory.** Add `LockedBy` and `LockedUntil` to the entry. `ClaimPendingAsync` takes one store-wide `lock` (add a private `readonly object _claimGate`) while it selects and marks entries, and the `Mark*` methods clear the lease. Implement `RenewLeaseAsync` under the entry lock with the renew predicate.

**Worker.**
- In `ProcessBatchAsync`, replace the fetch with `store.ClaimPendingAsync(_options.BatchSize, lease, ct)`, where `var lease = new OutboxLease(_options.HostId, _options.LeaseDuration);`.
- In `ProcessEntryAsync`, after the dispatcher lookup and before dispatch, check the lease:

```csharp
if (!await store.RenewLeaseAsync(entry.Id, lease, ct).ConfigureAwait(false))
{
    _leaseLost.Add(1, tag);   // new Counter<long> "outbox.lease.lost", next to the existing counters
    return;                   // another host owns this message now; do not dispatch
}
```

  Pass `lease` into `ProcessEntryAsync`. The missing-dispatcher dead-letter path also needs the lease renewed first, so it doesn't dead-letter a message another host has claimed.

- [ ] **Step 1: Failing tests.**
  - **InMemory exclusivity:** enqueue 200 messages. Run 8 tasks in parallel, each looping `ClaimPendingAsync(10, new OutboxLease($"h{i}", TimeSpan.FromMinutes(1)))` and recording claimed ids, then calling `MarkSucceededAsync` on each, until a claim returns empty. Assert 200 distinct ids, each seen exactly once.
  - **InMemory lease expiry:** claim with `Duration = TimeSpan.FromMilliseconds(50)` and don't release. Wait 100 ms. A second host's claim returns the row.
  - **InMemory lost lease:** host A claims with a 50 ms lease, and host B claims after 100 ms. A's `RenewLeaseAsync` returns `false`, and B's returns `true`.
  - **Release:** a claimed row that gets `MarkFailedAsync` with `nextRetryAt = now` is claimable again. A row after `MarkSucceededAsync` or `DeadLetterAsync` is never claimable.
  - **Worker skips a lost lease:** use a fake `IOutboxStore` whose `RenewLeaseAsync` returns `false` for one entry. The dispatcher is not called for that entry, and no `Mark*` is called for it.
  - **Options validation:** `LeaseDuration = TimeSpan.Zero`, `HostId = ""` and `HostId = new string('x', 129)` each fail validation when the options are resolved.
  - **ORM on SQLite, and EF on SQLite, claim exclusivity:** the same 200-message, 8-claimer test, where each claimer uses its own connection or `DbContext` to one shared SQLite file database. Use a temp file path, not `:memory:`, and set `busy_timeout` so writers wait instead of failing.
  - **Late `Mark` after a lost lease:** host B claims and `MarkSucceededAsync` after A's lease expired, then A calls `MarkFailedAsync`. The final state is Dispatched, because the state machine's terminal state wins.
- [ ] **Step 2: Run them.** They fail to compile, or fail, on today's code.
- [ ] **Step 3: Implement** the contract, options, validation, InMemory, ORM, EF and worker changes described above, and rename every `FetchPendingAsync` call site.
- [ ] **Step 4: Run** `dotnet build -c Release` with 0 warnings, then `dotnet test tests/ZeroAlloc.Outbox.Tests -c Release` and `dotnet test tests/ZeroAlloc.Outbox.Orm.Tests -c Release`. The SQL Server container tests run in the Orm project; they must pass too.
- [ ] **Step 5: Commit** with `feat!: claim outbox messages with a lease so concurrent hosts never dispatch twice`. The body explains the claim, renew and release model. It includes `BREAKING CHANGE: IOutboxStore.FetchPendingAsync is replaced by ClaimPendingAsync and RenewLeaseAsync; the outbox schema gains LockedBy and LockedUntil columns.`, on lines of at most 100 characters, and `Closes #191`.

---

### Task 3: Concurrency proof on Postgres and SQL Server, and two workers end-to-end

**Files:**
- Modify: `tests/ZeroAlloc.Outbox.Orm.Tests/ZeroAlloc.Outbox.Orm.Tests.csproj`, adding `Testcontainers.PostgreSql` (the same version as `Testcontainers.MsSql`, 4.15.0) and `Npgsql`
- Create: `tests/ZeroAlloc.Outbox.Orm.Tests/PostgresOutboxClaimTests.cs` and `SqlServerOutboxClaimTests.cs`
- Modify: `tests/ZeroAlloc.Outbox.Tests/ZeroAlloc.Outbox.Tests.csproj`, adding `Testcontainers.PostgreSql`, `Testcontainers.MsSql`, `Npgsql.EntityFrameworkCore.PostgreSQL` 9.x and `Microsoft.EntityFrameworkCore.SqlServer` 9.x, matching the EF 9.0.20 already referenced
- Create: `tests/ZeroAlloc.Outbox.Tests/EfCoreServerClaimTests.cs` and `TwoWorkerEndToEndTests.cs`

- [ ] **Step 1: Write the tests.**
  - **Per database** (ORM on Postgres, ORM on SQL Server, EF on Postgres, EF on SQL Server), using one container per test class:
    - **Exclusivity:** 500 messages and 12 claimers, each with its own connection or `DbContext`. Every id is claimed exactly once.
    - **Lease expiry:** claim with a 1 s lease, wait 2 s, then another host claims the row.
    - **Lost lease:** the same shape as the InMemory test.
    - **Batch renewal:** a lease of 2 s, and a batch of 5 where each dispatch takes 1 s, so the batch takes longer than the lease and each dispatch is shorter than it. Two claimers alternate, with no duplicates.
  - **Two workers end-to-end:** two `OutboxWorkerService` hosts (two `ServiceProvider`s) share one Postgres database, with a thread-safe counting dispatcher and different `HostId`s. Enqueue 300 messages, run until all are dispatched with a 60 s timeout, and assert that every message was dispatched exactly once.
- [ ] **Step 2: Run** each class on its own, `--filter "FullyQualifiedName~<Class>"`. They must pass on the Task 2 code. To prove the tests can detect a regression, temporarily revert the claim to a plain `SELECT` locally, confirm the exclusivity test fails, then restore it. Record this in the report.
- [ ] **Step 3: Commit** with `test: prove the outbox claim is exclusive on postgres and sql server`.

---

### Task 4: Docs, migration guide and downstream check

**Files:**
- Modify: `README.md`, `docs/outbox-pattern.md`, `docs/background-worker.md` and `docs/store-adapters.md`
- Create: `docs/migrating-to-v3.md`

- [ ] **Step 1: Docs.**
  - **The claim, renew and release model:** why a lease is needed rather than `SKIP LOCKED` alone, what `LeaseDuration` and `HostId` do, and how to choose them. `LeaseDuration` must exceed the slowest single dispatch.
  - **Delivery guarantee:** at-least-once, and exactly when a duplicate can still happen.
  - **Custom stores:** a store must implement `ClaimPendingAsync` and `RenewLeaseAsync` atomically, and `Mark*` must clear the lease.
- [ ] **Step 2: `docs/migrating-to-v3.md`.**
  - **ORM users:** migration 2 runs automatically through `OutboxOrmMigrations`.
  - **EF Core users:** run `dotnet ef migrations add AddOutboxLease` after upgrading. Show the expected columns.
  - **Custom `IOutboxStore` implementers:** the method rename, the new method, and the semantics to implement.
  - **New options.**
- [ ] **Step 3: Downstream check.**
  - In each local clone (`C:\Projects\Prive\ZeroAlloc\ZeroAlloc.*`), after fetching origin/main, run `git grep -n "FetchPendingAsync\|: IOutboxStore\|IOutboxStore" origin/main -- src tests` and list every hit outside Outbox.
  - Don't change other repos. Report the list with file:line, so the controller can open follow-up PRs after 3.0 ships.
- [ ] **Step 4: Commit** with `docs: document the outbox lease model and the v3 migration`.
