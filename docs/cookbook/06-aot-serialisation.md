---
id: 06-aot-serialisation
title: AOT-Safe Serialisation
sidebar_position: 6
---

# AOT-Safe Serialisation

By default, `ZeroAlloc.Outbox` serialises message payloads with `System.Text.Json` via `SystemTextJsonOutboxSerializer`. This works in most scenarios but is not compatible with Native AOT because it relies on reflection at runtime.

The `ZeroAlloc.Outbox.Mediator` package solves this by plugging in a `DispatchingOutboxSerializer` that delegates all serialisation to a registered `ISerializerDispatcher` — the AOT-safe runtime dispatch surface from the `ZeroAlloc.Serialisation` package.

## Installation

```bash
dotnet add package ZeroAlloc.Outbox
dotnet add package ZeroAlloc.Serialisation
```

## How It Works

`AddOutbox()` checks the DI container for a registered `ISerializerDispatcher`. If one is present, it automatically registers `DispatchingOutboxSerializer` as `IOutboxSerializer` instead of the default STJ implementation:

```csharp
// pseudo-code — what AddOutbox() does internally
services.TryAddSingleton<IOutboxSerializer>(sp =>
{
    var dispatcher = sp.GetService<ISerializerDispatcher>();
    if (dispatcher is not null)
        return new DispatchingOutboxSerializer(dispatcher);
    return new SystemTextJsonOutboxSerializer(); // reflection path, not AOT-safe
});
```

No extra configuration call is needed — just register an `ISerializerDispatcher` before calling `AddOutbox()`.

## Registration

```csharp
// Register your ZeroAlloc.Serialisation dispatcher first
services.AddSerializerDispatcher(options =>
{
    options.Register<OrderPlaced>();
    options.Register<InvoiceIssued>();
});

// AddOutbox() picks it up automatically
services.AddOutbox()
        .WithEfCore<AppDbContext>();
```

## Annotating Message Types

Mark each outbox message type with `[ZeroAllocSerializable]` so the `ZeroAlloc.Serialisation` source generator includes it in the dispatcher:

```csharp
using ZeroAlloc.Serialisation;

[OutboxMessage]
[ZeroAllocSerializable]
public sealed record OrderPlaced(int OrderId, decimal Amount);
```

The serialisation generator emits the `JsonTypeInfo<T>` and wires it into the dispatcher — no `JsonSerializerContext` configuration required by hand.

## Verifying AOT Compatibility

Build with `PublishAot=true`. A correctly configured project produces no `IL2026`/`IL3050` warnings from the outbox stack:

```bash
dotnet publish -r linux-x64 -p:PublishAot=true
```

If you still see warnings, confirm that `ISerializerDispatcher` is registered **before** `AddOutbox()` in your composition root, and that every outbox message type carries `[ZeroAllocSerializable]`.

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
services.AddOutbox();
```

`DispatchingOutboxSerializer` is sealed and AOT-safe — it passes the `Type` token received from the caller through to your dispatcher without reflection.
