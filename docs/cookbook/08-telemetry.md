---
id: 08-telemetry
title: OpenTelemetry integration
sidebar_position: 8
---

# OpenTelemetry integration

The `ZeroAlloc.Outbox.Telemetry` bridge package wires source-generated OpenTelemetry instrumentation into the outbox dispatch pipeline. One fluent call:

```csharp
services.AddOutbox()
        .WithEfCore<AppDbContext>()
        .AddOrderPlacedOutbox()
        .WithTelemetry();
```

Every registered `IOutboxTypeDispatcher` is wrapped in a generated proxy that emits:

- **Span** `outbox.dispatch` per call (ActivitySource: `ZeroAlloc.Outbox`)
- **Counter** `outbox.dispatched_total`
- **Histogram** `outbox.dispatch_duration_ms`

`WithTelemetry()` is idempotent — calling it twice does not double-wrap.

## Subscribing in OTel

```csharp
services.AddOpenTelemetry()
        .WithTracing(t => t.AddSource("ZeroAlloc.Outbox"))
        .WithMetrics(m => m.AddMeter("ZeroAlloc.Outbox"));
```

## Composition with WithResilience

Recommended order: telemetry **outermost** (so spans capture retries), resilience inner.

```csharp
services.AddOutbox()
        .AddOrderPlacedOutbox()
        .WithResilience<OrderPlaced, IResilientOrderPlacedDispatcher, ResilientOrderPlacedDispatcherProxy>()
        .WithTelemetry();
```
