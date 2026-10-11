---
id: 07-resilience-bridge
title: Resilience Bridge
sidebar_position: 7
---

# Resilience Bridge

The `ZeroAlloc.Outbox.Resilience` package wraps any `IOutboxDispatcher<T>` implementation in a `ZeroAlloc.Resilience`-generated proxy. This lets you add retry, circuit-breaker, timeout, or bulkhead policies to outbox dispatch without changing dispatcher code.

## Installation

```bash
dotnet add package ZeroAlloc.Outbox
dotnet add package ZeroAlloc.Outbox.Resilience
dotnet add package ZeroAlloc.Resilience
```

`ZeroAlloc.Resilience` carries its source generator; there is no separate generator package.

## Define the Resilience Interface

Declare an interface for your dispatcher that extends `IOutboxDispatcher<T>` and put the resilience attributes on it:

```csharp
using ZeroAlloc.Outbox;
using ZeroAlloc.Resilience;

[Retry(MaxAttempts = 3, BackoffMs = 200)]
[CircuitBreaker(MaxFailures = 5, ResetMs = 10_000)]
public interface IResilientOrderPlacedDispatcher : IOutboxDispatcher<OrderPlaced>
{
}
```

The `ZeroAlloc.Resilience` generator emits an `IResilientOrderPlacedDispatcherResilienceProxy` class that implements this interface. Its constructor takes the dispatcher it wraps, as `IResilientOrderPlacedDispatcher`, and a generated `ResilientOrderPlacedDispatcherResiliencePolicies` singleton. The generator also emits `AddResilientOrderPlacedDispatcherResiliencePolicies()`, which registers that singleton.

## Register

```csharp
// Your real dispatcher implements the resilience interface; the proxy wraps it.
services.AddTransient<IResilientOrderPlacedDispatcher, OrderPlacedDispatcher>();
services.AddResilientOrderPlacedDispatcherResiliencePolicies();

// Standard outbox setup; WithResilience wires the proxy as IOutboxDispatcher<OrderPlaced>.
services.AddOutbox()
        .WithEfCore<AppDbContext>()
        .AddOrderPlacedOutbox()
        .WithResilience<
            OrderPlaced,
            IResilientOrderPlacedDispatcher,
            IResilientOrderPlacedDispatcherResilienceProxy>();
```

`WithResilience<T, TDispatcherInterface, TResilienceProxy>()` registers `TResilienceProxy` as `Transient` and binds it as `IOutboxDispatcher<T>`. Do not also call the generated `AddResilientOrderPlacedDispatcherResilience<TImpl>()`: it registers a proxy as the interface itself, so the outbox's proxy would wrap a second proxy and every policy would run twice.

The registration is NativeAOT-safe; `samples/ZeroAlloc.Outbox.AotSmoke.Adapters` publishes it with `PublishAot=true`.

## Type Parameters

| Parameter | Description |
|-----------|-------------|
| `T` | The outbox message type |
| `TDispatcherInterface` | Your resilience interface (extends `IOutboxDispatcher<T>`) |
| `TResilienceProxy` | The generated proxy class (implements `TDispatcherInterface`) |

## How It Works

The resilience proxy is generated at compile time by the `ZeroAlloc.Resilience` generator. On every `DispatchAsync` call, the proxy applies the declared policies before forwarding to the inner dispatcher. The outbox worker never knows a proxy is involved: it resolves `IOutboxDispatcher<T>` from DI and calls `DispatchAsync` as normal.

The proxy's retries run inside one outbox dispatch attempt. Only when they are exhausted does the worker count a failed attempt and schedule the message's own retry, after `OutboxOptions.RetryBaseDelay`.

## Combining Policies

The policies run in a fixed order, whatever order the attributes are declared in:

```
RateLimit → CircuitBreaker → Timeout → Retry
```

```csharp
[Timeout(Ms = 5_000)]
[Retry(MaxAttempts = 3, BackoffMs = 100, PerAttemptTimeoutMs = 1_000)]
[CircuitBreaker(MaxFailures = 10, ResetMs = 15_000)]
public interface IResilientOrderPlacedDispatcher : IOutboxDispatcher<OrderPlaced> { }
```

Here the circuit breaker is checked first, the timeout bounds the whole dispatch including every retry and backoff, and `PerAttemptTimeoutMs` time-boxes each attempt. See the [ZeroAlloc.Resilience README](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience#readme) for every attribute property.
