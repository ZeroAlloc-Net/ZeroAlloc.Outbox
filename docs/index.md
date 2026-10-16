---
id: index
title: ZeroAlloc.Outbox
sidebar_position: 1
---

# ZeroAlloc.Outbox

Source-generated transactional outbox for .NET. Annotate a message type with `[OutboxMessage]` and a Roslyn source generator emits a typed writer and dispatcher bridge — no reflection, no boxing, AOT-safe. Backed by ZeroAlloc.ORM or EF Core (production) or in-memory (tests), with a built-in polling worker, retry, and dead-letter.

## Features

| Feature | Notes |
|---------|-------|
| Source-generated writers | `[OutboxMessage]` triggers generator; typed `IOutboxWriter<T>` emitted at compile time |
| Typed dispatchers | `IOutboxDispatcher<T>` — implement once, wire to any transport (bus, HTTP, email) |
| ORM store | Raw SQL through ZeroAlloc.ORM on the caller's transaction; SQLite, PostgreSQL or SQL Server |
| EF Core store | Writes and reads via `DbContext`; enlist in ambient transaction for atomicity |
| InMemory store | Thread-safe in-process store for unit and integration tests |
| Polling worker | `OutboxWorkerService` (`IHostedService`) polls on configurable interval with scope isolation |
| Lease-based claim | `ClaimPendingAsync` atomically leases a batch to one host, so scaling out never double-dispatches a row |
| Exponential backoff | Retry delay = `RetryBaseDelay × 2^(attempt-1)`; configurable via `OutboxOptions` |
| Dead-letter | Entries that exceed `MaxAttempts` are dead-lettered with the failure reason |
| AOT / trimmer safe | All dispatch code is generated; no `Type.GetType`, no `MakeGenericType`. `AddOutbox()` is trim- and AOT-safe; reflection JSON is an explicit opt-in |
| `IOptions<OutboxOptions>` | Configured through standard `Microsoft.Extensions.Options`; the worker reads its options once when it starts, so a change takes effect after a restart |

## Contents

- [Getting Started](getting-started.md) — install, annotate, register, dispatch
- [Outbox Pattern](outbox-pattern.md) — why the transactional outbox pattern exists
- [Message Types](message-types.md) — `[OutboxMessage]`, generated code, type discriminator
- [Dispatchers](dispatchers.md) — `IOutboxDispatcher<T>`, `IOutboxTypeDispatcher` (generated bridge), custom implementations
- [Store Adapters](store-adapters.md) — EF Core adapter (schema, migration), ORM adapter (dialects, single-statement claim), InMemory (test usage)
- [Background Worker](background-worker.md) — polling, retry back-off, dead-letter, `OutboxOptions` reference
- [Dependency Injection](dependency-injection.md) — `AddOutbox` builder, `WithEfCore`, `AddOrderPlacedOutbox`, lifetime rules, v1.x → v2.x migration table
- [Migrating to v3](migrating-to-v3.md) — the lease-based claim, `IOutboxStore` changes, and the EF Core migration step
- [Migrating to v4](migrating-to-v4.md) — choose a serializer explicitly; `AddOutbox()` is now trim- and AOT-safe
- [Diagnostics](diagnostics.md) — ZO0001 to ZO0007
- [Performance](performance.md) — zero-alloc design, AOT safety, source-gen vs reflection
- [Testing](testing.md) — `InMemoryOutboxStore`, `AllEntries()`, worker integration tests
- [Dashboard](dashboard.md) — live HTML/SSE dashboard with four tabs, throughput chart, and message actions
- Cookbook
  - [EF Core Transaction](cookbook/01-ef-core-transaction.md)
  - [Custom Dispatcher](cookbook/02-custom-dispatcher.md)
  - [Mediator Integration](cookbook/03-mediator-integration.md)
  - [Dead-Letter Handling](cookbook/04-dead-letter-handling.md)
  - [Testing with Host](cookbook/05-testing-with-host.md)
  - [AOT-Safe Serialisation](cookbook/06-aot-serialisation.md)
  - [Resilience Bridge](cookbook/07-resilience-bridge.md)
  - [OpenTelemetry Integration](cookbook/08-telemetry.md)
