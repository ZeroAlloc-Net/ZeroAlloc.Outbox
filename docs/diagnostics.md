---
id: diagnostics
title: Diagnostics
sidebar_position: 9
---

# Diagnostics

The `ZeroAlloc.Outbox.Generator` analyzer emits the following diagnostics at compile time.

| ID | Severity | Description |
|----|----------|-------------|
| [ZO0001](diagnostics/ZO0001.md) | Warning | `[OutboxMessage]` applied to an interface — no code is generated |
| [ZO0002](diagnostics/ZO0002.md) | Warning | `[OutboxMessage]` applied to a static class — no code is generated |
| [ZO0003](diagnostics/ZO0003.md) | Warning | `[OutboxMessage]` applied to a nested type — use a top-level type for a stable type discriminator |

All diagnostics are enabled by default. They are warnings, not errors, so the build succeeds but you will not get the generated writer.

## Retired IDs

These `[Obsolete]` diagnostic IDs marked the v1.x DI extension aliases. The aliases were removed in 4.0, so the IDs are no longer reported. They are retired and **must not be reused** for a new diagnostic: old suppressions and `NoWarn` entries in consumer projects still name them.

| ID | Removed in | Covered |
|----|------------|---------|
| `ZAOBOX002` | 4.0 | `AddOutboxInMemory()` |
| `ZAOBOX003` | 4.0 | `AddOutboxEfCore<TContext>()` |
| `ZAOBOX004` | 4.0 | `AddOutboxMediator<T>()` |
| `ZAOBOX005` | 4.0 | `AddOutboxResilience<T, TDispatcherInterface, TResilienceProxy>()` |
| `ZAOBOX006` | 4.0 | `AddOutboxDashboardEvents()` |
| `ZAOBOX010` | 4.0 | the generated `services.Add{Type}Outbox()` on `IServiceCollection` |

`ZAOBOX001` and `ZAOBOX007`–`ZAOBOX009` were never assigned. See [Migrating to v4](migrating-to-v4.md#removed-v1x-aliases) for the replacements.
