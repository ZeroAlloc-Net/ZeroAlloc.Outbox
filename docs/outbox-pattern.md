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

The worker uses **at-least-once delivery**: a message may be dispatched more than once (e.g., if the process crashes after dispatch but before the success mark). Downstream consumers should be idempotent, or the outbox entry ID can be used as an idempotency key.

## ZeroAlloc.Outbox implementation

| Concern | How ZeroAlloc.Outbox handles it |
|---------|-------------------------------|
| Atomic write | `IOutboxWriter<T>` enlists in the caller's `DbTransaction` (`EfCore` store) |
| Type safety | `[OutboxMessage]` generates a concrete `IOutboxWriter<T>` — no stringly-typed payloads |
| Polling | `OutboxWorkerService` (`BackgroundService`) polls on a configurable interval |
| Retry | Exponential back-off: `RetryBaseDelay × 2^(attempt-1)` |
| Dead-letter | Entries exceeding `MaxAttempts` are moved to dead-letter state with the failure reason |
| Dispatcher | `IOutboxDispatcher<T>` — you implement one method; the worker handles scheduling |

See [Getting Started](getting-started.md) to set this up in five minutes.
