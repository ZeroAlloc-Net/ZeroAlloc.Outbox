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
