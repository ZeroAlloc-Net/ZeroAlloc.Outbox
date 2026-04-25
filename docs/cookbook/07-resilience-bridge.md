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
dotnet add package ZeroAlloc.Resilience.Generator
```

## Define the Resilience Interface

Declare an interface for your dispatcher that extends `IOutboxDispatcher<T>` and annotates `DispatchAsync` with resilience attributes:

```csharp
using ZeroAlloc.Outbox;
using ZeroAlloc.Resilience;

[Retry(MaxAttempts = 3, DelayMs = 200, BackoffType = BackoffType.Exponential)]
[CircuitBreaker(FailureThreshold = 5, SamplingDurationMs = 30_000, BreakDurationMs = 10_000)]
public interface IResilientOrderPlacedDispatcher : IOutboxDispatcher<OrderPlaced>
{
}
```

The `ZeroAlloc.Resilience` generator emits a `ResilientOrderPlacedDispatcherProxy` class that implements this interface and wraps your real dispatcher.

## Register

```csharp
// Register your real dispatcher
services.AddTransient<OrderPlacedDispatcher>();

// Standard outbox setup; WithResilience wires the proxy as IOutboxDispatcher<OrderPlaced>.
services.AddOutbox()
        .WithEfCore<AppDbContext>()
        .AddOrderPlacedOutbox()
        .WithResilience<
            OrderPlaced,
            IResilientOrderPlacedDispatcher,
            ResilientOrderPlacedDispatcherProxy>();
```

`WithResilience<T, TDispatcherInterface, TResilienceProxy>()` registers `TResilienceProxy` as `Transient` and binds it as `IOutboxDispatcher<T>`.

## Type Parameters

| Parameter | Description |
|-----------|-------------|
| `T` | The outbox message type |
| `TDispatcherInterface` | Your resilience interface (extends `IOutboxDispatcher<T>`) |
| `TResilienceProxy` | The generated proxy class (implements `TDispatcherInterface`) |

## How It Works

The resilience proxy is generated at compile time by `ZeroAlloc.Resilience.Generator`. On every `DispatchAsync` call, the proxy applies the declared policies before forwarding to the inner dispatcher. The outbox worker never knows a proxy is involved — it resolves `IOutboxDispatcher<T>` from DI and calls `DispatchAsync` as normal.

## Combining Policies

Multiple attributes are combined in declaration order — the first listed policy is outermost:

```csharp
[Timeout(TimeoutMs = 5_000)]
[Retry(MaxAttempts = 3, DelayMs = 100)]
[CircuitBreaker(FailureThreshold = 10, SamplingDurationMs = 60_000, BreakDurationMs = 15_000)]
public interface IResilientOrderPlacedDispatcher : IOutboxDispatcher<OrderPlaced> { }
```

Here, timeout wraps retry, which wraps the circuit breaker — so each attempt is independently time-boxed.
