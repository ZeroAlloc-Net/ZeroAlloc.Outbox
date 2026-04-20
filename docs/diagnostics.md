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
