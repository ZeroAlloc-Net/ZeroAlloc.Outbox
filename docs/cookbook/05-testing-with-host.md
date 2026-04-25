---
id: 05-testing-with-host
title: Testing with Host
sidebar_position: 5
---

# Testing with Host

Run the full outbox pipeline — writer, worker, and dispatcher — in an integration test using `HostBuilder` and the InMemory store.

## Reusable helper

```csharp
public sealed class OutboxTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    public IServiceProvider Services => _host.Services;

    public InMemoryOutboxStore Store =>
        _host.Services.GetRequiredService<InMemoryOutboxStore>();

    private OutboxTestHost(IHost host) => _host = host;

    public static async Task<OutboxTestHost> StartAsync(
        Action<IOutboxBuilder> configure,
        TimeSpan? pollingInterval = null)
    {
        var host = await new HostBuilder()
            .ConfigureServices(services =>
            {
                var builder = services
                    .AddOutbox(o =>
                    {
                        o.PollingInterval = pollingInterval ?? TimeSpan.FromMilliseconds(50);
                    })
                    .WithInMemoryStore();
                configure(builder);
            })
            .StartAsync();

        return new OutboxTestHost(host);
    }

    public Task WaitForDispatchAsync(TimeSpan? timeout = null)
        => Task.Delay(timeout ?? TimeSpan.FromMilliseconds(300));

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
```

## Example test

```csharp
[Fact]
public async Task OrderPlaced_IsDispatchedSuccessfully()
{
    var dispatched = new List<OrderPlaced>();

    await using var host = await OutboxTestHost.StartAsync(builder =>
    {
        builder.AddOrderPlacedOutbox();
        builder.Services.AddTransient<IOutboxDispatcher<OrderPlaced>>(
            _ => new DelegateDispatcher<OrderPlaced>(msg =>
            {
                dispatched.Add(msg);
                return Task.CompletedTask;
            }));
    });

    var writer = host.Services.GetRequiredService<IOutboxWriter<OrderPlaced>>();
    await writer.WriteAsync(new OrderPlaced(42, 99.99m));

    await host.WaitForDispatchAsync();

    dispatched.Should().ContainSingle().Which.OrderId.Should().Be(42);
    host.Store.AllEntries().Single().Status
        .Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);
}
```

## `DelegateDispatcher<T>` helper

```csharp
public sealed class DelegateDispatcher<T>(Func<T, Task> handler) : IOutboxDispatcher<T>
{
    public Task DispatchAsync(T message, CancellationToken ct) => handler(message);
}
```
