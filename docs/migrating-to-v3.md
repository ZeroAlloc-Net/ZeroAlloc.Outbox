---
id: migrating-to-v3
title: Migrating to v3
sidebar_position: 13
---

# Migrating to v3

ZeroAlloc.Outbox 3.0 replaces the plain "fetch pending, dispatch, mark" loop with a
**lease-based atomic claim**, so that running more than one host against the same store never
dispatches the same message twice. See [The claim, renew and release model](outbox-pattern.md#the-claim-renew-and-release-model)
for why this was necessary and how it works. This page covers what upgrading actually requires.

## ORM users

Nothing to run by hand. `OutboxOrmMigrations` gained migration 2, `add_outbox_lease`, adding the
nullable `LockedBy` and `LockedUntil` columns. It's already part of the same `IMigrationSource`
your existing `MigrationRunner.RunAsync` call runs — the next time it runs against an upgraded
package it applies migration 2 and you're done. Existing rows get `NULL` in both columns, meaning
unleased, so they're immediately claimable.

## EF Core users

`OutboxMessageEntity` gained `LockedBy` (max length 128) and `LockedUntil`. After upgrading the
package, add a migration:

```bash
dotnet ef migrations add AddOutboxLease --project src/YourProject.EfCore
dotnet ef database update
```

The generated migration adds two nullable columns:

| Column | Type | Notes |
|--------|------|-------|
| `LockedBy` | `nvarchar(128)` NULL | Host holding the current lease |
| `LockedUntil` | `datetimeoffset` NULL | When the current lease expires |

> **SQL Server:** the claim and `ReleaseLeasesAsync` translate `Contains` over a list of
> candidate ids to `OPENJSON`, which needs database compatibility level 130 or higher. Raise it
> before upgrading if your database predates that.

## Custom `IOutboxStore` implementers

This is the breaking part of the release. If you implement `IOutboxStore` directly — rather than
using the EF Core, InMemory or ORM adapter — you need to change it:

| v2.x | v3.0 |
|---|---|
| `ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct)` | `ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)` |
| *(none)* | `ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)` |
| *(none)* | `ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)` |
| `ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct)` | `ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)` |
| `ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct)` | `ValueTask<bool> MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)` |
| `ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct)` | `ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)` |

There are no default interface implementations for any of these — a store that silently doesn't
claim would reintroduce the exact double-dispatch bug this release fixes, so it has to be a
compile error until you implement the real thing.

What each member must do:

- **`ClaimPendingAsync`** atomically selects up to `batchSize` due, unleased rows and marks them
  with `lease.HostId` and an expiry of now plus `lease.Duration`, in one statement or transaction
  that never lets two concurrent callers claim the same row.
- **`RenewLeaseAsync`** extends the lease on one message the caller still holds, and returns
  `false` — without throwing — if the lease already expired or another host now holds it.
- **`ReleaseLeasesAsync`** clears the lease on the given ids, but only the ones this host still
  holds, and returns how many it released.
- **`MarkSucceededAsync`, `MarkFailedAsync`, `DeadLetterAsync`** are each one atomic conditional
  update: move the row only if it is still pending *and* still leased by `lease.HostId`, and clear
  `LockedBy`/`LockedUntil` as part of that same write. They no longer throw
  `InvalidOperationException` on an illegal transition — they return `false`. Deliberately, they do
  **not** check whether the lease has expired: a host whose lease expired but was never reclaimed
  by another host still owns the row and can still record the outcome of the dispatch it finished.
  Only a takeover, which rewrites `LockedBy`, revokes that right.

A consequence for tests and seeders: a message must be claimed before it can be marked. Code that
called `MarkSucceededAsync` (or `Failed`/`DeadLetter`) directly against a freshly-enqueued row now
needs to `ClaimPendingAsync` it first — see [Claiming before marking](testing.md#claiming-before-marking)
for the pattern.

## New options

`OutboxOptions` gained two members, both validated when the options are built:

| Option | Default | Notes |
|---|---|---|
| `LeaseDuration` | 5 minutes | Must be greater than zero and at most one day, and must exceed your slowest single dispatch — the worker renews it per message, not per batch, so it only has to outlast one dispatch. |
| `HostId` | `"{MachineName}:{guid}"`, generated once per process | Must not be empty or whitespace, and at most 128 characters. Several `IHost`s in one process share this default, so set it explicitly when that's the case, or every one of them will treat the others' leases as its own. |

The existing options are now validated too. A `BatchSize` of zero or less, a `MaxAttempts` below 1
or a `PollingInterval` of zero or less used to be accepted and left the worker claiming nothing,
dead-lettering on the first failure, or spinning without a pause. They now fail at startup with an
`OptionsValidationException`.

```csharp
builder.Services.AddOutbox(options =>
{
    options.LeaseDuration = TimeSpan.FromMinutes(2); // longer than your slowest dispatcher call
    options.HostId = $"orders-api:{Environment.MachineName}";
});
```

## Rolling out

1. **Apply the schema change first.** Run the EF Core migration, or let the ORM `MigrationRunner`
   apply migration 2, before any v3 host starts. A v3 host claims through `LockedBy` and
   `LockedUntil`, so it fails against a table without them. The new columns are nullable and v2
   ignores them, so v2 hosts keep working on the migrated table.
2. **Then replace the hosts.** While v2 and v3 hosts run side by side, the v2 hosts ignore leases:
   they fetch and dispatch rows a v3 host has claimed, and a v3 host can claim a row a v2 host is
   dispatching. Duplicates therefore continue, as they did under v2, until every host runs v3.
   Finish the rollout promptly rather than running a mixed fleet for long.

Hosts also need roughly synchronized clocks, which NTP gives you. Each host compares lease expiry
times, written from its own clock, against its own clock: a host whose clock runs S ahead of the
others sees their leases expire S early and can claim a message another host is still
dispatching. Keep clock skew well below `LeaseDuration`.

## Delivery guarantee

Delivery is still **at-least-once**, exactly as before. What changed is *when* a duplicate can
happen: it now requires a single dispatch to outlive `LeaseDuration` (so another host claims and
redelivers the same message while the first is still working on it), or a host to die after
dispatching but before marking — the same case that already existed in v2.x. Size `LeaseDuration`
generously above your slowest dispatcher call and duplicates from scaling out become vanishingly
rare rather than routine.
