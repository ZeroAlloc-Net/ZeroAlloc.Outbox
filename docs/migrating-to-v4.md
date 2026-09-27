---
id: migrating-to-v4
title: Migrating to v4
sidebar_position: 14
---

# Migrating to v4

## Outbox 4.0: choose a serializer explicitly

Up to 3.x, `AddOutbox()` picked the payload serializer for you: the AOT-safe
`DispatchingOutboxSerializer` when an `ISerializerDispatcher` was registered, and otherwise the
reflection-based `SystemTextJsonOutboxSerializer`. That silent fallback is why `AddOutbox()` carried
`[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`. Every caller got `IL2026` and `IL3050`, even
one that registered the dispatcher so the fallback could never run, and a NativeAOT app with
warnings as errors could not call `AddOutbox()` without a suppression.

In 4.0 the fallback is gone and so are the attributes. `AddOutbox()` is trim- and AOT-safe, and the
reflection-based serializer is an explicit opt-in that carries the attributes itself.

### What you need to do

If you already call `services.AddSerializerDispatcher()`, or register your own
`IOutboxSerializer`, nothing changes. Remove any `#pragma warning disable IL2026, IL3050` or
`[UnconditionalSuppressMessage]` you put around `AddOutbox()`; it is no longer needed.

If you relied on the fallback, pick one:

```csharp
// AOT-safe: annotate each [OutboxMessage] type with [ZeroAllocSerializable]
// and give it a JsonSerializerContext, then:
services.AddSerializerDispatcher();
services.AddOutbox().WithEfCore<AppDbContext>().AddOrderPlacedOutbox();

// Reflection-based System.Text.Json, the same serializer 3.x fell back to:
services.AddOutbox()
        .WithEfCore<AppDbContext>()
        .AddOrderPlacedOutbox()
        .WithSystemTextJsonSerializer();
```

`WithSystemTextJsonSerializer()` produces the same bytes as the 3.x fallback, so messages already in
the store stay readable. It carries `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`, so a
trimmed or NativeAOT build warns at that call, and only there. See
[AOT-Safe Serialisation](cookbook/06-aot-serialisation.md) for the dispatcher setup.

### If you do nothing

Resolving `IOutboxSerializer` throws an `InvalidOperationException` that names both options. The
outbox worker now builds every `IOutboxTypeDispatcher` when it starts, and each generated one needs
the serializer, so the error appears as a failed host start, not when the first message is written
or dispatched.

An application whose dispatchers never deserialize an outbox payload needs no serializer and keeps
working unchanged. ZeroAlloc.Saga's outbox bridge is one: its saga command dispatchers carry their
own serializers.

### Precedence

When more than one serializer source is present, the first match wins:

1. `WithSystemTextJsonSerializer()`, which replaces every serializer registered before it.
2. An `IOutboxSerializer` the application registered before `AddOutbox()`.
3. `DispatchingOutboxSerializer`, when an `ISerializerDispatcher` is registered before or after
   `AddOutbox()`.

### Other changes in the same release

- `OutboxWorkerService` overrides `StartAsync` to build the type dispatchers once before its poll
  loop starts. A dispatcher that cannot be constructed, for any reason, now fails the host start.
  In 3.x the worker logged the same exception on every poll and never dispatched anything.
- The generator no longer emits `[UnconditionalSuppressMessage]` for `IL2026` and `IL3050` on the
  generated writer and type dispatcher. `IOutboxSerializer` has not carried trim attributes since
  1.3.0, so they suppressed nothing, and they would have hidden a real warning.

## Removed v1.x aliases

The v1.x DI extensions have been `[Obsolete]` since 2.0 and are removed in 4.0. Each has a direct
replacement on the `IOutboxBuilder` that `AddOutbox()` returns:

| Removed | Obsolete ID | Replacement |
|---|---|---|
| `services.AddOutboxInMemory()` | `ZAOBOX002` | `services.AddOutbox().WithInMemoryStore()` |
| `services.AddOutbox().AddOutboxInMemory()` | `ZAOBOX002` | `services.AddOutbox().WithInMemoryStore()` |
| `services.AddOutboxEfCore<TContext>()` | `ZAOBOX003` | `services.AddOutbox().WithEfCore<TContext>()` |
| `services.AddOutbox().AddOutboxEfCore<TContext>()` | `ZAOBOX003` | `services.AddOutbox().WithEfCore<TContext>()` |
| `services.AddOutboxMediator<T>()` | `ZAOBOX004` | `services.AddOutbox().WithMediator<T>()` |
| `services.AddOutbox().AddOutboxMediator<T>()` | `ZAOBOX004` | `services.AddOutbox().WithMediator<T>()` |
| `services.AddOutboxResilience<T, TDispatcherInterface, TResilienceProxy>()` | `ZAOBOX005` | `services.AddOutbox().WithResilience<T, TDispatcherInterface, TResilienceProxy>()` |
| `services.AddOutbox().AddOutboxResilience<T, TDispatcherInterface, TResilienceProxy>()` | `ZAOBOX005` | `services.AddOutbox().WithResilience<T, TDispatcherInterface, TResilienceProxy>()` |
| `services.AddOutboxDashboardEvents()` | `ZAOBOX006` | `services.AddOutbox().WithDashboardEvents()` |
| generated `services.Add{Type}Outbox()` on `IServiceCollection` | `ZAOBOX010` | generated `services.AddOutbox().Add{Type}Outbox()` |

The `IServiceCollection` forms registered only their own piece, without `AddOutbox()`'s worker and
options. The replacements hang off `AddOutbox()`, so call it once and chain everything on the
builder it returns. A host that needs a piece without the worker, such as a dashboard-only host,
registers it directly, for example
`services.AddSingleton<IOutboxDashboardEventPublisher, ChannelOutboxDashboardEventPublisher>()`.

The IDs `ZAOBOX002`–`ZAOBOX006` and `ZAOBOX010` are retired and will not be reused, so a `NoWarn`
entry that names them is now dead and can be deleted. See [Diagnostics](diagnostics.md#retired-ids).
