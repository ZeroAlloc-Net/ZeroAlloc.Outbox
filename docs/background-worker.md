---
id: background-worker
title: Background Worker
sidebar_position: 7
---

# Background Worker

## Overview

`OutboxWorkerService` is an `IHostedService` (specifically `BackgroundService`) registered by `AddOutbox()`. It runs a polling loop:

1. Create a fresh DI scope (isolates EF Core `DbContext` per batch).
2. Fetch up to `BatchSize` pending entries whose `NextRetryAt` is at or before the current UTC time.
3. For each entry, look up the registered `IOutboxTypeDispatcher` by `TypeName`.
4. Dispatch. On success mark the entry `Succeeded`. On failure increment `RetryCount` and schedule the next retry or dead-letter.
5. Sleep for `PollingInterval` and repeat.

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
| `PollingInterval` | `TimeSpan` | `00:00:05` | How long the worker sleeps between batch cycles |
| `BatchSize` | `int` | `50` | Maximum number of entries fetched per cycle |
| `MaxAttempts` | `int` | `5` | Dispatch attempts before dead-lettering |
| `RetryBaseDelay` | `TimeSpan` | `00:00:02` | Base delay for exponential back-off calculation |

Configure via `AddOutbox`:

```csharp
builder.Services.AddOutbox(options =>
{
    options.PollingInterval = TimeSpan.FromSeconds(10);
    options.BatchSize       = 100;
    options.MaxAttempts     = 3;
    options.RetryBaseDelay  = TimeSpan.FromSeconds(5);
});
```
