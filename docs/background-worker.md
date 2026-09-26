---
id: background-worker
title: Background Worker
sidebar_position: 7
---

# Background Worker

## Overview

`OutboxWorkerService` is an `IHostedService` (specifically `BackgroundService`) registered by `AddOutbox()`. It runs a polling loop:

1. Create a fresh DI scope (isolates EF Core `DbContext` per batch).
2. Build an `OutboxLease` from `OutboxOptions.HostId` and `OutboxOptions.LeaseDuration`, and claim up to `BatchSize` due entries with it via `ClaimPendingAsync`. Claiming atomically leases the rows to this host, so a second host polling the same store cannot claim them too.
3. For each claimed entry, renew its lease, then look up the registered `IOutboxTypeDispatcher` by `TypeName`.
   - If the renewal fails, another host may already own the row — the worker skips it without dispatching, and counts it on `outbox.lease.lost` with `reason=renew-failed`.
4. Dispatch. On success mark the entry `Succeeded`. On failure increment `RetryCount` and schedule the next retry or dead-letter. Each mark carries the lease and returns whether it moved the row.
   - If a mark returns `false`, the message is no longer pending under this host's lease: another host took it over after the lease expired mid-dispatch, and may have already completed it, or an operator cancelled it from the dashboard. This is not a dispatch failure: the worker counts it on `outbox.lease.lost` with `reason=completed-elsewhere` and continues the batch.
5. Sleep for `PollingInterval` and repeat.

Once a dispatch has returned or failed, the worker records its outcome — the mark, the retry or the dead-letter — even if the host is stopping. That write runs on its own 5-second timeout rather than the stopping token: cancelling it would leave a delivered message pending for another host to deliver again, or a failed attempt uncounted.

If a batch ends early — the worker is stopped, or a store call such as a renewal or a mark fails — the worker releases the leases on the entries it has not finished, on the same 5-second timeout, so another host can pick them up immediately instead of waiting out `LeaseDuration`. An entry whose dispatch was cancelled by the stop, or never started, is released; an entry whose dispatch already ran is not. This is best effort: a failure to release is logged, not thrown, and the affected leases simply expire. Delivery stays at-least-once either way.

An `OperationCanceledException` raised by a dispatcher on its own — for example an `HttpClient` timeout — is *not* treated as the worker stopping. It is a failed dispatch attempt like any other, retried or dead-lettered by the normal path; only cancellation of the host's own stopping token stops the worker.

See [The claim, renew and release model](outbox-pattern.md#the-claim-renew-and-release-model) for why a lease is needed and how to size `LeaseDuration`.

## Retry back-off

The retry delay is calculated as:

```
delay = RetryBaseDelay × 2^(attempt - 1)
```

| Attempt | `RetryBaseDelay = 1 s` | `RetryBaseDelay = 5 s` |
|---------|------------------------|------------------------|
| 1 | 1 s | 5 s |
| 2 | 2 s | 10 s |
| 3 | 4 s | 20 s |
| 4 | 8 s | 40 s |

When `RetryCount` reaches `MaxAttempts` the entry is dead-lettered with the last exception message.

## Dead-letter

Dead-lettered entries have `Status = DeadLetter` and a non-null `DeadLetterError`. The worker does not retry them. To requeue a dead-lettered entry, reset `Status = Pending` and `RetryCount = 0` directly in the database (or via your own management tooling).

An entry is also dead-lettered immediately (attempt 0) if no `IOutboxTypeDispatcher` is registered for its `TypeName`. This prevents the worker from endlessly re-fetching an unroutable entry.

## `OutboxOptions` reference

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `PollingInterval` | `TimeSpan` | `00:00:05` | How long the worker sleeps between batch cycles. Must be greater than zero |
| `BatchSize` | `int` | `50` | Maximum number of entries claimed per cycle. Must be greater than zero |
| `MaxAttempts` | `int` | `5` | Dispatch attempts before dead-lettering. Must be at least 1 |
| `RetryBaseDelay` | `TimeSpan` | `00:00:02` | Base delay for exponential back-off calculation |
| `LeaseDuration` | `TimeSpan` | `00:05:00` | How long a claim or renewal lasts. Must be greater than zero and at most one day, and must exceed the slowest single dispatch |
| `HostId` | `string` | `"{MachineName}:{guid}"`, fixed per process | Identifies this process as the lease holder. Must not be empty or whitespace, and at most 128 characters; set it explicitly when several `IHost`s share a process |

Every rule above is checked when the options are built, so a bad value fails at startup with an `OptionsValidationException` rather than stalling the worker. The one-day cap on `LeaseDuration` bounds how long a crashed host's claimed messages stay unclaimable.

Configure via `AddOutbox`:

```csharp
builder.Services.AddOutbox(options =>
{
    options.PollingInterval = TimeSpan.FromSeconds(10);
    options.BatchSize       = 100;
    options.MaxAttempts     = 3;
    options.RetryBaseDelay  = TimeSpan.FromSeconds(5);
    options.LeaseDuration   = TimeSpan.FromMinutes(2);
    options.HostId          = $"orders-api:{Environment.MachineName}";
});
```
