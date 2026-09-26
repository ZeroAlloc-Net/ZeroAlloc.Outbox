---
id: outbox-pattern
title: The Outbox Pattern
sidebar_position: 2
---

# The Outbox Pattern

## The problem

In a distributed system, writing to a database and publishing a message to a broker are two separate operations. If the process crashes after the database write but before the message is published, the downstream consumer never learns about the event. Conversely, if the message is published first and then the database write fails, the consumer acts on data that does not exist.

Both races produce inconsistency that is hard to detect and painful to recover from.

## The solution

The **transactional outbox pattern** solves the problem by treating the message as data:

1. Write your domain change **and** the outgoing message to the same database in the same transaction. Either both commit or both roll back — atomicity guaranteed.
2. A separate background worker polls the outbox table and dispatches each message to its destination (message broker, HTTP endpoint, etc.).
3. Once a message is dispatched successfully, mark it as succeeded. If dispatch fails, retry with back-off until a maximum attempt count is reached, then dead-letter.

The worker uses **at-least-once delivery**: a message may be dispatched more than once. That still happens after the change below — a duplicate now requires a single dispatch to outlive `LeaseDuration`, or a host to die after dispatching but before marking — so downstream consumers should be idempotent, or use the outbox entry ID as an idempotency key.

## The claim, renew and release model

A plain `SELECT … LIMIT` is not enough once more than one host polls the same table: two hosts can read the same due rows and both dispatch them. Adding `FOR UPDATE SKIP LOCKED` to that select doesn't fix it either, because the row lock only lasts for the transaction, and the worker dispatches *after* the transaction that fetched the row has ended. A row has to be claimed in a way that outlives the fetch, so ZeroAlloc.Outbox uses a **lease**.

- **Claim.** `IOutboxStore.ClaimPendingAsync(batchSize, lease, ct)` atomically selects up to `batchSize` due, unleased rows and marks them with `lease.HostId` and an expiry of now plus `lease.Duration`. Two hosts claiming at the same moment never claim the same row.
- **Renew.** Right before dispatching each claimed entry, the worker calls `RenewLeaseAsync` to extend the lease. This means `LeaseDuration` only has to cover **one** dispatch, not the whole batch — but it must exceed the slowest single dispatch, or another host can claim and redeliver the same message while this host is still working on it.
- **Release.** `MarkSucceededAsync`, `MarkFailedAsync` and `DeadLetterAsync` each take the lease and clear it as part of the same atomic update, so the message either becomes claimable again (on failure) or is done (on success or dead-letter). If a batch ends early, because the worker is stopping or a store call failed, it releases the leases on whatever it didn't dispatch so another host doesn't have to wait out the full `LeaseDuration`.

Two options control this:

- `OutboxOptions.HostId` identifies the claiming process. It defaults to `"{MachineName}:{guid}"`, generated once per process — **but if several `IHost` instances run in one process** (for example in tests), they share that default and will treat each other's leases as their own, so set `HostId` explicitly in that case.
- `OutboxOptions.LeaseDuration` (default 5 minutes) is how long a claim or renewal lasts. Set it comfortably above your slowest dispatcher call — a webhook with a generous timeout, say — not the batch size or polling interval.

Lease expiry is compared against each host's own clock, so hosts need roughly synchronized clocks. A host whose clock runs S ahead of the others sees their leases expire S early, and can claim a message another host is still dispatching. NTP keeps skew far below any sensible `LeaseDuration`.

## Custom stores

A custom `IOutboxStore` must implement `ClaimPendingAsync` and `RenewLeaseAsync` as atomic, race-free operations — an unleased read followed by a separate write reintroduces the double-dispatch bug this design exists to fix. `MarkSucceededAsync`, `MarkFailedAsync` and `DeadLetterAsync` must clear the lease (`LockedBy` and `LockedUntil`, or their equivalent) as part of the same conditional write that moves the message, guarded on the caller still holding it. There are deliberately no default interface implementations for these members: a store that silently doesn't claim is exactly the bug being fixed.

## ZeroAlloc.Outbox implementation

| Concern | How ZeroAlloc.Outbox handles it |
|---------|-------------------------------|
| Atomic write | `IOutboxWriter<T>` enlists in the caller's `DbTransaction` (`EfCore` store) |
| Type safety | `[OutboxMessage]` generates a concrete `IOutboxWriter<T>` — no stringly-typed payloads |
| Polling | `OutboxWorkerService` (`BackgroundService`) polls on a configurable interval |
| Claim | `ClaimPendingAsync` leases a batch atomically so two hosts never dispatch the same row |
| Retry | Exponential back-off: `RetryBaseDelay × 2^(attempt-1)` |
| Dead-letter | Entries exceeding `MaxAttempts` are moved to dead-letter state with the failure reason |
| Dispatcher | `IOutboxDispatcher<T>` — you implement one method; the worker handles scheduling |

See [Getting Started](getting-started.md) to set this up in five minutes, and [Migrating to v3](migrating-to-v3.md) if you're upgrading from an earlier version.
