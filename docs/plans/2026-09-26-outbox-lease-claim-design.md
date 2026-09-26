# Outbox lease-based claim: design

**Issue:** #191. **Release:** ZeroAlloc.Outbox 3.0.0, a major. **Status:** approved 2026-09-26.

## Problem

`IOutboxStore.FetchPendingAsync` is a plain `SELECT … LIMIT`. When two hosts poll the same `OutboxMessages` table, both read the same due rows, and both dispatch them. The status update after dispatch commits only once, but by then both side effects have already happened. For the downstream Daedalus project each dispatch is a paid agent turn, so scaling the API host past one replica duplicates work and cost.

The fix the issue suggests, adding `FOR UPDATE SKIP LOCKED` to the fetch, is not enough on its own. Row locks last only until the transaction ends. `OutboxWorkerService` fetches, then dispatches, so the locks are gone before the dispatch starts. A row has to be **claimed** in a way that outlives the fetch.

## Decision

Use a **lease-based atomic claim.** These were rejected:
- **Holding one transaction across fetch, dispatch and mark.** It keeps a DB transaction and connection open across slow, external, possibly paid calls.
- **A single active host through an advisory lock.** It can't scale out, and SQLite has no advisory locks.

## Semantics

- **Claim.** One statement selects up to `BatchSize` rows that are due: `Status` is pending and `NextRetryAt <= now`. Retries are stored as pending with `RetryCount > 0`, so they are included. They must also be unleased: `LockedUntil IS NULL OR LockedUntil < now`. It sets `LockedBy = lease.HostId` and `LockedUntil = now + lease.Duration`, and returns exactly the rows it claimed. Two claimers running at the same moment never claim the same row.
  - **Postgres:** `WITH c AS MATERIALIZED (SELECT Id FROM OutboxMessages WHERE … ORDER BY CreatedAt LIMIT @n FOR UPDATE SKIP LOCKED) UPDATE OutboxMessages t SET … FROM c WHERE t.Id = c.Id RETURNING …`, with the unleased predicate in the CTE. `MATERIALIZED` makes the locking CTE run exactly once, so the rows it locks are exactly the rows updated and returned. PostgreSQL re-checks the CTE's `WHERE` after taking each row lock.
  - **SQL Server:** `WITH claim AS (SELECT TOP (@n) … FROM OutboxMessages WITH (ROWLOCK, READPAST, UPDLOCK) WHERE … ORDER BY CreatedAt) UPDATE claim SET … OUTPUT inserted.…`. An updatable CTE carries the claim order, because `UPDATE TOP` has no `ORDER BY`.
  - **SQLite:** `UPDATE … WHERE Id IN (SELECT Id … ORDER BY CreatedAt LIMIT @n) RETURNING …`. SQLite serializes writers, so the statement is atomic.
  - **EF Core**, which has no `RETURNING`:
    1. Select up to N candidate ids that are due and unleased.
    2. Run a conditional `ExecuteUpdateAsync` over those ids that repeats the unleased predicate. The database re-checks it under the row lock, so a row another claimer took in between is skipped.
    3. Load the rows whose `LockedBy` and `LockedUntil` equal this claim's values.
  - **InMemory:** a lock-protected claim over the in-memory rows, with the same semantics.
- **Per-message renewal.** Right before dispatching each claimed entry, the worker calls `RenewLeaseAsync`, which runs `UPDATE … SET LockedUntil = now + Duration WHERE Id = @id AND LockedBy = @hostId AND LockedUntil >= now AND Status = Pending`. An expired lease can't be renewed, even by the same host, because another host may already be entitled to claim the row. If no row changes, this host has lost the lease, so the worker skips the entry and does not dispatch it. The lease therefore only has to cover **one** dispatch, not a whole sequentially processed batch.
- **Release on completion.** `MarkSucceededAsync`, `MarkFailedAsync` and `DeadLetterAsync` clear `LockedBy` and `LockedUntil`. A retried row becomes claimable again at its `NextRetryAt`.
- **Atomic marks, guarded by the lease.** Each mark takes the lease the batch was claimed with, and is **one** conditional statement: `UPDATE … SET …, LockedBy = NULL, LockedUntil = NULL WHERE Id = @id AND Status = Pending AND LockedBy = @hostId`. On EF Core it is an `ExecuteUpdateAsync`, and on InMemory the same check and write happen under the entry lock. Pending, whatever the `RetryCount`, is exactly the set of `OutboxMessageFsm` states from which Dispatch, Fail and Exhaust are legal. So the state machine stays the documented model, and the database condition enforces it. There is no separate read-then-transition (#198).
  - **Only the lease holder can mark.** A host whose lease was taken over by another host changes nothing: its late `MarkFailedAsync` cannot reschedule a message the new holder is dispatching, and cannot clear the new holder's `LockedBy` and `LockedUntil`. Since at most one host holds the lease at a time, of two concurrent marks from different hosts exactly one wins, and a terminal state is never overwritten.
  - **No expiry check.** The guard deliberately does not test `LockedUntil`. A host whose lease expired but was not claimed by anyone else still holds it, and can still record the outcome of the dispatch it finished. Only a takeover, which rewrites `LockedBy`, revokes the right to mark.
  - A mark returns `true` if it moved the row, and `false` if the row had already moved on or another host now holds its lease.
- **Completed elsewhere.** A mark returning `false` means this host's lease ran out mid-dispatch, and another host claimed the message and may already have finished it. The worker counts it on `outbox.lease.lost`, tagged `reason=completed-elsewhere`, and carries on with the batch. It does not treat it as a dispatch failure. A renewal that fails is counted on the same instrument, tagged `reason=renew-failed`. Both are tagged `message.type`.
- **Release on shutdown.** When cancellation stops the worker mid-batch, it calls `ReleaseLeasesAsync` for the claimed entries it did not finish, including one whose dispatch was cancelled. That runs `UPDATE … SET LockedBy = NULL, LockedUntil = NULL WHERE Id IN (@ids) AND LockedBy = @hostId AND Status = Pending`, so another host can claim them at once instead of waiting out the lease. It is best effort: it runs on its own 5-second timeout rather than the cancelled token, and a failure is logged, not thrown, leaving the leases to expire. The ORM store issues it as one statement per id, because ZeroAlloc.ORM cannot expand a collection parameter into an `IN` list.
- **Dispatcher cancellation.** Only a cancellation of the worker's stopping token stops the worker. An `OperationCanceledException` a dispatcher raises on its own, such as an `HttpClient` timeout, is a failed dispatch attempt: it goes through the normal failure path, retry or dead-letter, and the worker keeps running.
- **Crash recovery.** If a host dies holding a lease, the lease expires after `Duration` and another host claims the row.
- **Guarantee.** Delivery stays **at-least-once**. A duplicate now requires a single dispatch to outlive `LeaseDuration`, or a host to die after dispatching but before marking, which was already true.

## Public API, breaking

- `IOutboxStore.FetchPendingAsync(int batchSize, CancellationToken ct)` becomes `ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)`.
- New members on `IOutboxStore`:
  - `ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)`.
  - `ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)`, which returns the number of leases released.
- `MarkSucceededAsync`, `MarkFailedAsync` and `DeadLetterAsync` take an `OutboxLease lease` parameter before the `CancellationToken`, and return `ValueTask<bool>` instead of `ValueTask`: `true` if the call moved the message, `false` if it was no longer pending or `lease.HostId` no longer holds its lease. They apply only while the caller is still the lease holder, whether or not the lease has expired. They no longer throw `InvalidOperationException` on an illegal transition; they report `false`.
- A new type: `public readonly record struct OutboxLease(string HostId, TimeSpan Duration)`.
- `OutboxOptions` gains:
  - `TimeSpan LeaseDuration`, defaulting to 5 minutes. It must be greater than zero, and must exceed the slowest single dispatch.
  - `string HostId`, defaulting to `$"{Environment.MachineName}:{Guid.NewGuid():N}"` once per process. It must be non-empty and at most 128 characters.

  Both are validated when options are built.
- `OutboxWorkerService` builds one `OutboxLease` per batch from the options, claims, renews each entry before dispatching it, and passes the same lease to every mark. It treats a mark that returns `false` as completed elsewhere, and releases unfinished leases when it stops mid-batch. A dispatcher's own cancellation is a failed attempt, not a stop. The counter `outbox.lease.lost` carries the tags `message.type` and `reason`.
- There are no default interface implementations. A custom store must implement claiming explicitly, because a store that silently doesn't claim is exactly the bug being fixed.

## Schema

There are two new nullable columns.

| Column | SQLite | Postgres | SQL Server |
|---|---|---|---|
| `LockedBy` | TEXT NULL | VARCHAR(128) NULL | NVARCHAR(128) NULL |
| `LockedUntil` | TEXT NULL | TIMESTAMPTZ NULL | DATETIMEOFFSET NULL |

- **ORM.** `OutboxOrmMigrations` gains migration 2, `add_outbox_lease`, with the `ALTER TABLE … ADD` statements for each dialect. They are guarded, so they're idempotent where the dialect allows. Existing rows get NULL, meaning unleased.
- **EF Core.** `OutboxMessageEntity` gains `LockedBy` and `LockedUntil`, and `AddOutboxMessages` maps `LockedBy` with a max length of 128. Consumers add an EF migration.
- **Indexes.** The existing `(Status, NextRetryAt)` index serves the claim predicate, so no new index is needed.

## Tests

- **Claim exclusivity**, written first and failing on today's code. For each store (ORM on SQLite, Postgres and SQL Server; EF Core on Postgres and SQL Server; InMemory), enqueue N messages, then run K concurrent claimers, each with its own connection or `DbContext`, repeatedly claiming, holding briefly and releasing. Every message is claimed exactly once.
- **Two workers end-to-end.** Two `OutboxWorkerService` instances share one database and a counting dispatcher. Every message is dispatched exactly once, provided no dispatch exceeds the lease.
- **Lease expiry.** A lease left unreleased is claimable by another host after expiry.
- **Lost lease.** After host A's lease expires and host B claims the row:
  - A's `RenewLeaseAsync` returns `false`.
  - The worker skips dispatching.
  - A's late `Mark*` returns `false`, does not overwrite B's outcome, and leaves B's `LockedBy` and `LockedUntil` untouched.
- **Expired but not taken over.** A's lease expires and nobody claims the row: A's `MarkSucceededAsync` still returns `true`.
- **Batch renewal.** When the lease is shorter than the whole batch but longer than one dispatch, there are no duplicates.
- **Release semantics.** A failed message is claimable again at its `NextRetryAt`, and a dispatched or dead-lettered message never is.
- **Migration.** A database created at schema version 1, with rows in it, upgrades through migration 2 and its existing rows stay claimable. This repo has no EF Core migrations, so there is no model snapshot to update; EF Core users generate their own migration for the two new columns.
- **Options validation.** A non-positive `LeaseDuration` or one longer than a day, an empty, whitespace or over-long `HostId`, a non-positive `BatchSize` or `PollingInterval`, and a `MaxAttempts` below 1 are all rejected.

## Docs

- README, `docs/outbox-pattern.md`, `docs/background-worker.md` and `docs/store-adapters.md`: describe the claim and lease model, `LeaseDuration`, `HostId`, and at-least-once delivery.
- `docs/migrating-to-v3.md`: covers the EF migration step, the `IOutboxStore` changes for custom stores, choosing `LeaseDuration`, and the delivery guarantee.

## Delivery

- **One PR.** Use `feat!:` with a `BREAKING CHANGE:` footer and `Closes #191`. Body lines are at most 100 characters, with no nested parentheses.
- **Before release,** find the org repos that implement `IOutboxStore` or call `FetchPendingAsync`.
  Only Saga and Daedalus actually break, tracked as ZeroAlloc-Net/ZeroAlloc.Saga#173 and
  MarcelRoozekrans/Daedalus.NET#310. Scheduling has its own `IJobStore`, unrelated to
  `IOutboxStore`; its claim was already fixed in Scheduling#224. Templates has no references to
  either. Open the two follow-up PRs after 3.0 is on NuGet.
