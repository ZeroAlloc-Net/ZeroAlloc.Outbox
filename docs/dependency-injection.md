---
id: dependency-injection
title: Dependency Injection
sidebar_position: 8
---

# Dependency Injection

## `AddOutbox`

```csharp
builder.Services.AddOutbox();
// or
builder.Services.AddOutbox(options => { options.BatchSize = 100; });
```

Registers:

| Service | Lifetime | Notes |
|---------|----------|-------|
| `IOptions<OutboxOptions>` | Singleton | Bound from `OutboxOptions` section or inline configuration |
| `OutboxWorkerService` | Singleton | `IHostedService`; polls the store in a background loop |
| `SystemTextJsonOutboxSerializer` | Singleton | Default `IOutboxSerializer` implementation |

## `AddOutboxEfCore<TContext>`

```csharp
builder.Services.AddOutboxEfCore<AppDbContext>();
```

Registers:

| Service | Lifetime | Notes |
|---------|----------|-------|
| `EfCoreOutboxStore` as `IOutboxStore` | Scoped | Scoped to match `DbContext` lifetime |

The `TContext` type must have `OutboxMessageEntity` configured (done automatically by `AddOutboxEfCore`).

## `AddOutboxInMemory`

```csharp
builder.Services.AddOutboxInMemory();
```

Registers:

| Service | Lifetime | Notes |
|---------|----------|-------|
| `InMemoryOutboxStore` as `IOutboxStore` | Singleton | Thread-safe; singleton so tests share the same instance |
| `InMemoryOutboxStore` (concrete) | Singleton | Same instance; lets tests call `AllEntries()` directly |

## Generated `AddXxxOutbox` extension

The source generator emits one extension per `[OutboxMessage]` type, e.g. for `OrderPlaced`:

```csharp
public static IServiceCollection AddOrderPlacedOutbox(this IServiceCollection services)
{
    services.AddTransient<IOutboxWriter<OrderPlaced>, OrderPlacedOutboxWriter>();
    services.AddTransient<IOutboxTypeDispatcher, OrderPlacedOutboxTypeDispatcher>();
    return services;
}
```

The `IOutboxTypeDispatcher` is registered as `Transient` so it is resolved fresh inside each scope the worker creates per batch cycle.

## Lifetime rules

- `IOutboxStore` implementations must be **Scoped** (EF Core) or **Singleton** (InMemory). Do not register a Singleton EF Core store — it captures the `DbContext` and causes data corruption.
- `IOutboxDispatcher<T>` implementations can be any lifetime. `Transient` is the safest default.
- The worker creates a new `IServiceScope` per batch cycle, so Scoped services are correctly isolated.
