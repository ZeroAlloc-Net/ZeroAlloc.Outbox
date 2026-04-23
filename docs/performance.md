---
id: performance
title: Performance
sidebar_position: 10
---

# Performance

## Zero-allocation design

The "ZeroAlloc" name refers to the dispatch path:

- The `IOutboxTypeDispatcher` implementation is generated at compile time — no `Dictionary<string, Delegate>`, no `MethodInfo.Invoke`, no generic instantiation at runtime.
- The type discriminator (`TypeName` string) is a compile-time constant in the generated class — no string formatting on the hot path.
- Deserialization still allocates (the message object itself is a heap allocation), but the infrastructure around it does not.

## AOT / trimmer safety

All dispatch code paths are:
- Concrete types (no `Type.GetType(string)`, no `MakeGenericType`)
- Registered through standard DI (`services.AddTransient<IOutboxTypeDispatcher, ...>`)
- Compatible with `PublishAot=true` and `TrimmerRootDescriptor` trimming

The `[OutboxMessage]` attribute and the generator are pure Roslyn — there is no runtime reflection.

## Source generation vs reflection

| Concern | Reflection-based | ZeroAlloc.Outbox (generated) |
|---------|-----------------|------------------------------|
| Dispatcher lookup | `Dictionary<Type, MethodInfo>` | `Dictionary<string, IOutboxTypeDispatcher>` (value-type entries) |
| Deserialization call | `MethodInfo.MakeGenericMethod(...).Invoke(...)` | Direct generic call in generated class |
| DI registration | `services.AddTransient(typeof(IDispatcher<>), ...)` | `services.AddTransient<IOutboxTypeDispatcher, OrderPlacedTypeDispatcher>()` |
| AOT safe | No | Yes |
| IL trimmer safe | No | Yes |

## Worker overhead

The worker creates one `IServiceScope` per batch cycle, not per entry. For a batch of 50 entries there is one scope creation and one `FetchPendingAsync` query regardless of batch size.

## Benchmark

The [benchmarks/ZeroAlloc.Outbox.Benchmarks](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/tree/main/benchmarks/ZeroAlloc.Outbox.Benchmarks) project contains `WriteAsyncBenchmark` — a measurement of the generator-emitted `OrderPlacedOutboxWriter.WriteAsync` dispatch cost in isolation from the store.

The setup uses a fake `IOutboxStore` that records into an in-memory counter and a fake serializer that returns a pre-allocated 32-byte payload. This isolates the writer's own path from the store and serializer — the claim is that the generator-emitted forwarder allocates `0 B/op`. Whatever allocation the consumer's actual store or serializer introduces is theirs, not the library's.

```bash
dotnet run --project benchmarks/ZeroAlloc.Outbox.Benchmarks -c Release --filter "*"
```

What to watch:

- **Allocated column**: must read `0 B/op`. The writer should forward directly to `_store.EnqueueAsync(...)` without any intermediate allocation. A regression points at a new step in the emitted `WriteAsync` — most likely an accidental boxing or a `ValueTask → Task` coercion
- **Mean column**: stays within a few nanoseconds of a direct interface call. Meaningful departure suggests the generator is doing more work per call than in the previous commit

The benchmark does NOT exercise the dispatch / worker / retry paths. Those are store-dependent and covered by the store-specific test projects (`ZeroAlloc.Outbox.Tests`, `ZeroAlloc.Outbox.EfCore.Tests`).
