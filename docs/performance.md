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
- Deserialization allocates what the message itself needs, such as a class instance and its strings, but the infrastructure around it does not. A struct message is not boxed.

## Enqueue serialization

`DispatchingOutboxSerializer`, the serializer `AddOutbox()` selects when `AddSerializerDispatcher()` is registered, writes each message through `ISerializerDispatcher.Serialize<T>(IBufferWriter<byte>, T)` into a buffer rented from `ArrayPool<byte>.Shared`. A struct message is not boxed, and no intermediate buffer is allocated. The written bytes are copied once into an exact-size array, which `OrmOutboxStore` binds as it is. So the payload appears on the heap once, and that array is the only allocation serialization adds.

`EnqueueSerializationBenchmark` runs the generated writer and the generated `SerializerDispatcher` (System.Text.Json format) against a store that keeps nothing, and against `OrmOutboxStore` over SQLite in memory. .NET 10.0.12, BenchmarkDotNet v0.15.8, `--inProcess`. `Allocated`, before ZeroAlloc.Serialisation 2.7.0 and after:

| Benchmark | Before | After |
|---|---:|---:|
| Serialize and enqueue a 3-field struct, fake store | 352 B | **72 B**, the array of its 44-byte payload |
| Serialize and enqueue a 2-field class, fake store | 312 B | **64 B**, the array of its 36-byte payload |
| Serialize and enqueue the class, `OrmOutboxStore` | 4,128 B | **3,816 B** |
| Serialize and enqueue a 32 KB message, `OrmOutboxStore` | 135,520 B | **36,608 B** |

Before, the dispatcher boxed a struct, grew a fresh `ArrayBufferWriter` for every message, and returned a slice of it, which the ORM store then copied again. The last row comes from `OrmOutboxStoreAllocationTests`. `EnqueueSerializationTests` asserts the first two rows exactly: an enqueue allocates the payload array and nothing else.

The in-memory and EF Core stores still copy the payload into the entry or entity they keep, because they hold it after the call returns.

## Dispatch deserialization

On the read side, `DispatchingOutboxSerializer` reads each payload through `ISerializerDispatcher.Deserialize<T>(ReadOnlyMemory<byte>)`, which ZeroAlloc.Serialisation 2.8.0 added. The generated `SerializerDispatcher` returns a struct as itself, where `Deserialize(ReadOnlyMemory<byte>, Type)` returned it boxed as `object`.

`DispatchDeserializationTests` drives the generated `IOutboxTypeDispatcher.DispatchAsync` with the generated `SerializerDispatcher` and a message dispatcher that only records the message. Bytes per dispatch on .NET 10.0, on ZeroAlloc.Serialisation 2.7.0 before and on 2.8.1 after:

| Message | Before | After |
|---|---:|---:|
| 3-field struct, MemoryPack | 40 B, the box | **0 B** |
| 3-field struct, System.Text.Json | 248 B | **208 B**, what System.Text.Json allocates to read it |

The tests assert the second column exactly: 0 B for MemoryPack, and for System.Text.Json the same number of bytes that `JsonSerializer.Deserialize` allocates for the payload on its own. A class message still allocates its instance, which the dispatcher hands on.

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

The worker creates one `IServiceScope` per batch cycle, not per entry. For a batch of 50 entries there is one scope creation and one `ClaimPendingAsync` call regardless of batch size, plus one `RenewLeaseAsync` call per entry right before it is dispatched. Against a real database each renewal is a round trip, so a batch of 50 costs 50 renewals on top of its 50 marks.

The head-to-head dispatch tick below claims and marks, but does not renew, so its figures leave out that per-entry renewal round trip. They measure the abstraction overhead of the claim and mark path, not the full cost of a worker cycle.

`DispatchLoopBenchmark` measures one poll of `OutboxWorkerService` against a store and a dispatcher that complete synchronously and allocate nothing, so every byte is the worker's own. Same run as the head-to-head below.

| Poll | Allocated |
|---|---:|
| Idle, nothing claimed | 128 B, the poll's DI scope |
| Claims and dispatches 50 entries | 3,269 B |

The idle poll creates the scope because a scoped store, such as the EF Core one, can only be resolved inside it; it resolves no dispatcher. `OutboxWorkerAllocationTests` asserts that an idle poll allocates nothing beyond that scope.

## Head-to-head vs hand-rolled SQLite outbox

<!-- BENCH:START -->
_Last refreshed: 2026-10-21_

Correctness-matched comparison: both rows use the **same SQLite-in-memory connection** (different tables), both wrap each enqueue in a transaction, both run a claim-then-dispatch-then-commit cycle on the dispatch tick. Storage variance cancels out — the remaining delta is the cost of the `IOutboxWriter<T>` + `IOutboxStore` + serializer abstraction layer.

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9711)
12th Gen Intel Core i9-12900HK 2.50GHz, 1 CPU, 20 logical and 14 physical cores
.NET SDK 10.0.402
  [Host] : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v3
```

> Allocations are exact. Speed is shown as same-run ratios; absolute timings are not published.

Each benchmark ran out of process in three launches on an otherwise idle machine, pinned to two performance cores. `Ratio` is ZA.Outbox's mean time relative to the hand-rolled row of the same operation in the same run, with the lowest and highest of the three launches in brackets.

| Operation | Hand-rolled | ZA.Outbox | Allocated, hand-rolled → ZA.Outbox |
|---|---:|---:|---:|
| Enqueue (1 message, transactional) | 1.00 | **1.03** (1.02–1.04) | 2,128 B → 2,184 B, +3% |
| Dispatch tick (10 messages) | 1.00 | **1.05** (1.00–1.12) | 12,184 B → **11,520 B, −5%** |

**Honest reading**: ZA.Outbox adds **near-zero abstraction overhead** vs writing the same correctness-matched SQLite outbox by hand. The 3–5% time delta is the `IOutboxWriter<T>` + `IOutboxStore` interface dispatch + serializer call overhead. Memory allocation is within ±5% of hand-rolled, and lower on the dispatch tick.

The value of the abstraction is the `[OutboxMessage]` attribute + the typed writer + composability with other ZA packages (dispatcher, dashboard, resilience, telemetry bridges) — paid for at near-no runtime cost.
<!-- BENCH:END -->

## Self-benchmark

The [benchmarks/ZeroAlloc.Outbox.Benchmarks](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/tree/main/benchmarks/ZeroAlloc.Outbox.Benchmarks) project also contains `WriteAsyncBenchmark` — a measurement of the generator-emitted `OrderPlacedOutboxWriter.WriteAsync` dispatch cost in isolation from the store.

The setup uses a fake `IOutboxStore` that records into an in-memory counter and a fake serializer that returns a pre-allocated 32-byte payload. This isolates the writer's own path from the store and serializer — the claim is that the generator-emitted forwarder allocates `0 B/op`. Whatever allocation the consumer's actual store or serializer introduces is theirs, not the library's.

```bash
dotnet run --project benchmarks/ZeroAlloc.Outbox.Benchmarks -c Release -- --filter "*" --launchCount 3
```

What to watch:

- **Allocated column**: must read `0 B/op`. The writer should forward directly to `_store.EnqueueAsync(...)` without any intermediate allocation. A regression points at a new step in the emitted `WriteAsync` — most likely an accidental boxing or a `ValueTask → Task` coercion
- **Time**: compare a run against the previous commit's in the same session, not against published figures. A clear slowdown suggests the generator is doing more work per call than before

The benchmark does NOT exercise the dispatch / worker / retry paths. Those are store-dependent and covered by the store-specific test projects (`ZeroAlloc.Outbox.Tests`, `ZeroAlloc.Outbox.EfCore.Tests`).
