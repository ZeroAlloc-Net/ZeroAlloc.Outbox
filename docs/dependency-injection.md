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

The v1.x extensions remain as `[Obsolete]` shims (diagnostic IDs `ZAOBOX001`–`ZAOBOX010`) for one minor version, then are removed.

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
| `IOutboxSerializer` | Singleton | `DispatchingOutboxSerializer` if `ISerializerDispatcher` is registered; otherwise `SystemTextJsonOutboxSerializer` |

**Serializer selection** — if `ISerializerDispatcher` (from `ZeroAlloc.Serialisation`) is registered in the container before `AddOutbox()` is called, the AOT-safe `DispatchingOutboxSerializer` is used automatically. See [AOT-Safe Serialisation](cookbook/06-aot-serialisation.md) for setup details.

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
- `IOutboxDispatcher<T>` implementations can be any lifetime. `Transient` is the safest default.
- The worker creates a new `IServiceScope` per batch cycle, so Scoped services are correctly isolated.
