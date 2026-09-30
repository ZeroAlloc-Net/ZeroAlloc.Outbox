---
id: 06-aot-serialisation
title: AOT-Safe Serialisation
sidebar_position: 6
---

# AOT-Safe Serialisation

`ZeroAlloc.Outbox` stores each message payload as bytes, through an `IOutboxSerializer`. `AddOutbox()` itself is trim- and AOT-safe, and since 4.0 it never picks a reflection-based serializer on its own. You choose one of two:

| Choice | Registration | Trim / AOT |
|--------|--------------|------------|
| `DispatchingOutboxSerializer` | `services.AddSerializerDispatcher()` from `ZeroAlloc.Serialisation` | Safe |
| `SystemTextJsonOutboxSerializer` | `services.AddOutbox().WithSystemTextJsonSerializer()` | Not safe: `[RequiresUnreferencedCode]`, `[RequiresDynamicCode]` |

This page covers the AOT-safe one. `DispatchingOutboxSerializer` delegates to an `ISerializerDispatcher`, the AOT-safe runtime dispatch surface that the `ZeroAlloc.Serialisation` source generator emits.

## Installation

```bash
dotnet add package ZeroAlloc.Outbox
```

`ZeroAlloc.Outbox` depends on `ZeroAlloc.Serialisation`, whose source generator emits the dispatcher.

## Annotating Message Types

Mark each outbox message type with `[ZeroAllocSerializable]` so the `ZeroAlloc.Serialisation` source generator emits a serializer for it and includes it in the dispatcher. For the System.Text.Json format, declare a `JsonSerializerContext` for the type in the same project; the generated serializer routes through it, so no reflection is involved:

```csharp
using System.Text.Json.Serialization;
using ZeroAlloc.Outbox;
using ZeroAlloc.Serialisation;

[OutboxMessage]
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record OrderPlaced(int OrderId, decimal Amount);

[JsonSerializable(typeof(OrderPlaced))]
internal sealed partial class OrderPlacedJsonContext : JsonSerializerContext;
```

## Closed generic messages

A [generic message type](../message-types.md#generic-message-types) such as `Envelope<T>` is serialized per closed construction. Declare each one on the assembly, which needs `ZeroAlloc.Serialisation` 2.5 or later, and for System.Text.Json add it to a `JsonSerializerContext`:

```csharp
[assembly: ZeroAllocSerializable(typeof(Envelope<Order>), SerializationFormat.SystemTextJson)]

[JsonSerializable(typeof(Envelope<Order>))]
internal sealed partial class OutboxJsonContext : JsonSerializerContext;
```

`AddSerializerDispatcher()` then covers `Envelope<Order>` too, and the generated outbox dispatcher deserializes it with no reflection.

A closed generic outbox message is NativeAOT-safe only with the **System.Text.Json** or **MemoryPack** format. The MessagePack format cannot yet serialize a closed generic type without runtime code generation; that is tracked in [ZeroAlloc.Serialisation#184](https://github.com/ZeroAlloc-Net/ZeroAlloc.Serialisation/issues/184).

## Registration

```csharp
// Emitted by the ZeroAlloc.Serialisation generator; registers ISerializerDispatcher.
services.AddSerializerDispatcher();

services.AddOutbox()
        .WithEfCore<AppDbContext>()
        .AddOrderPlacedOutbox();
```

`AddOutbox()` looks the dispatcher up when the serializer is first resolved, so the order of the two calls does not matter. There are no warnings to suppress at the call site.

## When no serializer is configured

If neither `AddSerializerDispatcher()` nor `WithSystemTextJsonSerializer()` was called, and the application did not register its own `IOutboxSerializer`, resolving the serializer throws an `InvalidOperationException` that names both options. `OutboxWorkerService` builds every `IOutboxTypeDispatcher` when it starts, and the generated ones need the serializer, so the error surfaces as a failed host start rather than on the first message.

An application whose dispatchers never deserialize an outbox payload, such as one that uses the worker only for ZeroAlloc.Saga commands, needs no serializer at all.

## Precedence

`WithSystemTextJsonSerializer()` replaces any serializer registered before it, including the dispatcher-backed default, so an explicit opt-in always wins. A serializer the application registers itself before `AddOutbox()` is kept. See [Dependency Injection](../dependency-injection.md#addoutbox) for the full order.

## Verifying AOT Compatibility

Build with `PublishAot=true`. A correctly configured project produces no `IL2026`/`IL3050` warnings from the outbox stack:

```bash
dotnet publish -r linux-x64 -p:PublishAot=true
```

If you see `IL2026`/`IL3050` pointing at `WithSystemTextJsonSerializer`, remove that call and use `AddSerializerDispatcher()`. The repository's `samples/ZeroAlloc.Outbox.AotSmoke` publishes this exact setup under NativeAOT in CI.

## Implementing a Custom Dispatcher

If `ZeroAlloc.Serialisation` doesn't fit your needs, implement `ISerializerDispatcher` directly:

```csharp
using ZeroAlloc.Serialisation;

public sealed class MessagePackDispatcher : ISerializerDispatcher
{
    public ReadOnlyMemory<byte> Serialize(object value, Type type) =>
        MessagePackSerializer.Serialize(type, value);

    public object? Deserialize(ReadOnlyMemory<byte> data, Type type) =>
        MessagePackSerializer.Deserialize(type, data);
}

// Registration
services.AddSingleton<ISerializerDispatcher, MessagePackDispatcher>();
services.AddOutbox();   // selects DispatchingOutboxSerializer over your dispatcher
```

`DispatchingOutboxSerializer` is sealed and AOT-safe — it passes the `Type` token received from the caller through to your dispatcher without reflection.
