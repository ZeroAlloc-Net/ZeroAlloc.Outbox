---
id: dependency-injection
title: Dependency Injection
sidebar_position: 8
---

# Dependency Injection

## Migrating from v1.x

| v1.x | v2.x |
|---|---|
| `services.AddOutbox()` returning `IServiceCollection` | `services.AddOutbox()` returning `IOutboxBuilder` (use `.Services` to recover the collection) |
| `services.AddOutboxEfCore<TCtx>()` | `services.AddOutbox().WithEfCore<TCtx>()` |
| `services.AddOutboxInMemory()` | `services.AddOutbox().WithInMemoryStore()` |
| `services.AddOrderPlacedOutbox()` | `services.AddOutbox().AddOrderPlacedOutbox()` |
| `services.AddOutboxMediator<T>()` | `services.AddOutbox().WithMediator<T>()` |
| `services.AddOutboxResilience<T, …>()` | `services.AddOutbox().WithResilience<T, …>()` |
| `services.AddOutboxDashboardEvents()` | `services.AddOutbox().WithDashboardEvents()` |

The v1.x extensions were kept as `[Obsolete]` shims through 2.x and 3.x, under diagnostic IDs `ZAOBOX002`–`ZAOBOX006` and `ZAOBOX010`. They were removed in 4.0; see [Migrating to v4](migrating-to-v4.md#removed-v1x-aliases).

## `AddOutbox`

```csharp
builder.Services.AddOutbox();
// or
builder.Services.AddOutbox(options => { options.BatchSize = 100; });
```

`AddOutbox` returns an `IOutboxBuilder`. Chain `With*` extensions on the builder to register the store, dashboard, mediator bridge, resilience bridge, and the per-message-type extensions emitted by the source generator. Use `builder.Services` to recover the underlying `IServiceCollection` if you need to register additional services in the same chain.

Registers:

| Service | Lifetime | Notes |
|---------|----------|-------|
| `IOptions<OutboxOptions>` | Singleton | Bound from `OutboxOptions` section or inline configuration |
| `OutboxWorkerService` | Singleton | `IHostedService`; polls the store in a background loop |
| `IValidateOptions<OutboxOptions>` | Singleton | Rejects invalid `OutboxOptions` when the worker starts |
| `IOutboxSerializer` | Singleton | `DispatchingOutboxSerializer` if `ISerializerDispatcher` is registered; otherwise resolving it throws. Not registered when the application registered its own first |

`AddOutbox()` is trim- and AOT-safe: it carries no `[RequiresUnreferencedCode]` or `[RequiresDynamicCode]`.

**Serializer selection** — `AddOutbox()` never falls back to reflection-based JSON. The `IOutboxSerializer` resolves as follows, first match wins:

1. `WithSystemTextJsonSerializer()` was called: `SystemTextJsonOutboxSerializer`. It replaces every serializer registered before it, including the dispatcher default. It is `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`.
2. The application registered its own `IOutboxSerializer` before `AddOutbox()`: that one.
3. An `ISerializerDispatcher` from `ZeroAlloc.Serialisation` is registered, before or after `AddOutbox()`: the AOT-safe `DispatchingOutboxSerializer`.
4. Otherwise: resolving `IOutboxSerializer` throws an `InvalidOperationException` that names `services.AddSerializerDispatcher()` and `.WithSystemTextJsonSerializer()`. Because `OutboxWorkerService` builds every `IOutboxTypeDispatcher` when it starts, and each generated one needs the serializer, this surfaces as a failed host start.

An application whose dispatchers never deserialize an outbox payload, such as one that runs the worker only for ZeroAlloc.Saga commands, needs no serializer. See [AOT-Safe Serialisation](cookbook/06-aot-serialisation.md) for setup details.

## `WithSystemTextJsonSerializer`

```csharp
builder.Services.AddOutbox()
        .WithSystemTextJsonSerializer();
```

Registers `SystemTextJsonOutboxSerializer` as the `IOutboxSerializer` singleton. Use it when reflection is acceptable, that is, when you do not trim or publish with NativeAOT.

## `WithEfCore<TContext>`

```csharp
builder.Services.AddOutbox()
        .WithEfCore<AppDbContext>();
```

Registers:

| Service | Lifetime | Notes |
|---------|----------|-------|
| `EfCoreOutboxStore` as `IOutboxStore` | Scoped | Scoped to match `DbContext` lifetime |

The `TContext` must call `modelBuilder.AddOutboxMessages()` in `OnModelCreating` to register the `OutboxMessages` table. `WithEfCore` only registers the store service — it does not configure the model.

## `WithInMemoryStore`

```csharp
builder.Services.AddOutbox()
        .WithInMemoryStore();
```

Registers:

| Service | Lifetime | Notes |
|---------|----------|-------|
| `InMemoryOutboxStore` as `IOutboxStore` | Singleton | Thread-safe; singleton so tests share the same instance |
| `InMemoryOutboxStore` (concrete) | Singleton | Same instance; lets tests call `AllEntries()` directly |

## Generated `AddXxxOutbox` extension

The source generator emits one extension per `[OutboxMessage]` type, hung off `IOutboxBuilder`. For `OrderPlaced`:

```csharp
public static IOutboxBuilder AddOrderPlacedOutbox(this IOutboxBuilder builder)
{
    builder.Services.AddTransient<IOutboxWriter<OrderPlaced>, OrderPlacedOutboxWriter>();
    builder.Services.AddTransient<IOutboxTypeDispatcher, OrderPlacedOutboxTypeDispatcher>();
    builder.Services.TryAddTransient<IOutboxDispatcher<OrderPlaced>,
        DefaultOutboxDispatcher<OrderPlaced>>();
    return builder;
}
```

Call it directly on the builder:

```csharp
builder.Services.AddOutbox()
        .WithEfCore<AppDbContext>()
        .AddOrderPlacedOutbox();
```

The `IOutboxTypeDispatcher` is registered as `Transient` so it is resolved fresh inside each scope the worker creates per batch cycle.

## Lifetime rules

- `IOutboxStore` implementations must be **Scoped** (EF Core) or **Singleton** (InMemory). Do not register a Singleton EF Core store — it captures the `DbContext` and causes data corruption.
- A container hosts one outbox pipeline, so it has one `IOutboxStore`. Registering the same store again is a no-op; registering a different one, such as `WithOrm()` after `WithEfCore<T>()`, throws an `InvalidOperationException`. See [Migrating to v4](migrating-to-v4.md#one-store-per-container).
- `IOutboxDispatcher<T>` implementations can be any lifetime. `Transient` is the safest default.
- The worker creates a new `IServiceScope` per batch cycle, so Scoped services are correctly isolated.

## `WithTelemetry`

The `ZeroAlloc.Outbox.Telemetry` bridge package adds a parameterless `WithTelemetry()` extension on `IOutboxBuilder` that decorates every registered `IOutboxTypeDispatcher` with a source-generated OpenTelemetry proxy — emitting an `outbox.dispatch` span, an `outbox.dispatched_total` counter, and an `outbox.dispatch_duration_ms` histogram on the `ZeroAlloc.Outbox` ActivitySource/Meter. The call is idempotent and walks the entire dispatcher fan-out, so it covers all `[OutboxMessage]` types registered before the call. See the [OpenTelemetry Integration](cookbook/08-telemetry.md) cookbook entry for a full walk-through, including composition order with `WithResilience`.
