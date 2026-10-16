# ZeroAlloc.Outbox

[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.Outbox.svg)](https://www.nuget.org/packages/ZeroAlloc.Outbox)
[![Build](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/actions/workflows/ci.yml/badge.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/LICENSE)
[![AOT](https://img.shields.io/badge/AOT--Compatible-passing-brightgreen)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?style=flat&logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

Source-generated transactional outbox for .NET: mark a message with `[OutboxMessage]` and get a typed writer, a polling worker with retry and dead-lettering, and NativeAOT support, within a few percent of a hand-written outbox.

## Why ZeroAlloc.Outbox

- **Within 2–9% of a hand-rolled outbox.** Against a correctness-matched SQLite outbox on the same connection, an enqueue is +2% time and a 10-message dispatch tick +9%, with 7% less allocated.
- **Allocates only the payload.** An enqueue allocates just the exact-size payload array, 72 B for a small struct message. The worker loop adds nothing per message beyond deserialising it, and a MemoryPack struct deserialises with 0 B. All three are asserted by tests.
- **Works under NativeAOT and trimming.** Writers and dispatchers are generated at compile time, with no `Type.GetType` or `MakeGenericType`. CI publishes and runs an AOT app with IL warnings as errors.
- **Compile-time diagnostics.** ZO0001 to ZO0007 flag message types the generator cannot handle, such as interfaces, nested types or colliding names, at build time.

## Install

```bash
dotnet add package ZeroAlloc.Outbox
dotnet add package ZeroAlloc.Outbox.InMemory
```

The source generator ships inside `ZeroAlloc.Outbox`. The quick start uses the in-memory store; production apps use the ORM or EF Core store below.

## Quick start

```csharp
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.InMemory;
using ZeroAlloc.Serialisation;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerializerDispatcher(); // generated, AOT-safe
builder.Services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(100))
    .WithInMemoryStore()       // or .WithOrm() / .WithEfCore<AppDbContext>() in production
    .AddOrderPlacedOutbox();   // generated for the [OutboxMessage] type
builder.Services.AddSingleton<IOutboxDispatcher<OrderPlaced>, SendOrderPlaced>();
using var host = builder.Build();
await host.StartAsync();

await using (var scope = host.Services.CreateAsyncScope())
{
    var outbox = scope.ServiceProvider.GetRequiredService<IOutboxWriter<OrderPlaced>>();
    await outbox.WriteAsync(new OrderPlaced(42, 9.95m)); // pass your DbTransaction for an atomic write
}
await Task.Delay(500); // the worker claims the message and dispatches it
await host.StopAsync();

[OutboxMessage, ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record OrderPlaced(int OrderId, decimal Amount);

[JsonSerializable(typeof(OrderPlaced))]
internal sealed partial class AppJson : JsonSerializerContext;

public sealed class SendOrderPlaced : IOutboxDispatcher<OrderPlaced>
{
    public ValueTask DispatchAsync(OrderPlaced message, CancellationToken ct)
    {
        Console.WriteLine($"Dispatched order {message.OrderId}"); // publish to your bus here
        return ValueTask.CompletedTask;
    }
}
```

`ZeroAllocSerializable` and the generated `AddSerializerDispatcher()` come from [ZeroAlloc.Serialisation](https://github.com/ZeroAlloc-Net/ZeroAlloc.Serialisation), which `ZeroAlloc.Outbox` brings in. If you don't need AOT, `.WithSystemTextJsonSerializer()` is a one-line reflection-based alternative. The [getting started guide](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/getting-started.md) walks through the EF Core setup.

## What you get

- A typed `IOutboxWriter<T>` per message type that writes on your own database transaction.
- Typed dispatchers: implement `IOutboxDispatcher<T>` once and send to any bus, HTTP endpoint or mailbox.
- A background worker with lease-based claiming, so several hosts never dispatch the same row twice.
- Exponential-backoff retry and dead-lettering with the last failure reason.
- Named pipelines, each with its own store, options and worker.

Stores and integrations:

- [`ZeroAlloc.Outbox.Orm`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/store-adapters.md): raw SQL through ZeroAlloc.ORM on SQLite, PostgreSQL or SQL Server, NativeAOT on net10.0.
- [`ZeroAlloc.Outbox.EfCore`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/store-adapters.md): store on your existing `DbContext`.
- [`ZeroAlloc.Outbox.InMemory`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/testing.md): in-process store for unit and integration tests.
- [`ZeroAlloc.Outbox.Mediator`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/cookbook/03-mediator-integration.md): dispatch through ZeroAlloc.Mediator `IMediator.Publish`.
- [`ZeroAlloc.Outbox.Resilience`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/cookbook/07-resilience-bridge.md): wrap dispatchers in a ZeroAlloc.Resilience policy.
- [`ZeroAlloc.Outbox.Telemetry`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/cookbook/08-telemetry.md): OpenTelemetry spans and metrics around dispatch.
- [`ZeroAlloc.Outbox.Dashboard`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/dashboard.md): live web dashboard to inspect, requeue, cancel or force-dispatch messages. Always put it behind authorization.
- [`ZeroAlloc.Outbox.Dashboard.Blazor`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/dashboard.md#blazor-component): the dashboard as a Blazor component.

![Outbox dashboard, pending tab](https://raw.githubusercontent.com/ZeroAlloc-Net/ZeroAlloc.Outbox/main/docs/screenshots/pending-desktop.png)

## Performance

Against a hand-rolled SQLite outbox on the same connection, both transactional. .NET 10.0.7, i9-12900HK, BenchmarkDotNet v0.15.4.

| Operation | Hand-rolled | ZA.Outbox | Overhead |
|---|---:|---:|---:|
| Enqueue (1 message, transactional) | 6.86 µs / 2.08 KB | **6.99 µs / 2.13 KB** | +2% time, +2% alloc |
| Dispatch tick (10 messages) | 105.4 µs / 11.9 KB | **115.0 µs / 11.09 KB** | +9% time, **−7% alloc** |

Full numbers, including enqueue serialisation and dispatch deserialisation budgets: [docs/performance.md](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/performance.md).

## Learn more

- Documentation: [outbox.zeroalloc.net](https://outbox.zeroalloc.net)
- Feature reference: [docs/index.md](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/index.md), diagnostics: [docs/diagnostics.md](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/diagnostics.md)
- Upgrading: [migrating to v3](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/migrating-to-v3.md), [migrating to v4](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/migrating-to-v4.md)
- Related: [ZeroAlloc.Saga](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga), [ZeroAlloc.EventSourcing](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing), [ZeroAlloc.Mediator](https://github.com/ZeroAlloc-Net/ZeroAlloc.Mediator), [ZeroAlloc.Serialisation](https://github.com/ZeroAlloc-Net/ZeroAlloc.Serialisation)
- Contributing: issues and pull requests are welcome on [GitHub](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues)
- License: MIT, see [LICENSE](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/LICENSE)
