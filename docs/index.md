---
id: index
title: ZeroAlloc.Outbox
sidebar_position: 1
---

# ZeroAlloc.Outbox

Source-generated transactional outbox for .NET. Annotate a message type with `[OutboxMessage]` and a Roslyn source generator emits a typed writer and dispatcher bridge — no reflection, no boxing, AOT-safe. Backed by EF Core or in-memory, with a built-in polling worker, retry, and dead-letter.

## Contents

- [Getting Started](getting-started.md) — install, annotate, register, dispatch
- [Outbox Pattern](outbox-pattern.md) — why the transactional outbox pattern exists
- [Message Types](message-types.md) — `[OutboxMessage]`, generated code, type discriminator
- [Dispatchers](dispatchers.md) — `IOutboxDispatcher<T>`, `IOutboxTypeDispatcher` (generated bridge), custom implementations
- [Store Adapters](store-adapters.md) — EF Core adapter (schema, migration), InMemory (test usage)
- [Background Worker](background-worker.md) — polling, retry back-off, dead-letter, `OutboxOptions` reference
- [Dependency Injection](dependency-injection.md) — `AddOutbox` builder, `WithEfCore`, `AddOrderPlacedOutbox`, lifetime rules, v1.x → v2.x migration table
- [Diagnostics](diagnostics.md) — ZO0001, ZO0002, ZO0003
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
