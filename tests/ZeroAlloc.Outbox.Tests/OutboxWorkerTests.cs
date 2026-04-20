using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public sealed class OutboxWorkerTests
{
    [Fact]
    public async Task Worker_DispatchesPendingMessage_OnSinglePoll()
    {
        var dispatched = new List<string>();
        var tcs = new TaskCompletionSource();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutboxInMemory();
                services.AddOutbox(o =>
                {
                    o.BatchSize = 10;
                    o.PollingInterval = TimeSpan.FromMilliseconds(50);
                });
                services.AddSingleton<IOutboxTypeDispatcher>(
                    new TestDispatcher("MyApp.Ping", (_, _) =>
                    {
                        dispatched.Add("Ping");
                        tcs.TrySetResult();
                        return ValueTask.CompletedTask;
                    }));
            })
            .Build();

        var store = host.Services.GetRequiredService<IOutboxStore>();
        await store.EnqueueAsync("MyApp.Ping", new byte[] { 1 }, null, default);

        await host.StartAsync();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        dispatched.Should().ContainSingle().Which.Should().Be("Ping");
    }

    [Fact]
    public async Task Worker_DeadLetters_AfterMaxAttempts()
    {
        int dispatchCalls = 0;

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutboxInMemory();
                services.AddOutbox(o =>
                {
                    o.MaxAttempts = 2;
                    o.BatchSize = 10;
                    o.PollingInterval = TimeSpan.FromMilliseconds(20);
                    o.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
                });
                services.AddSingleton<IOutboxTypeDispatcher>(
                    new TestDispatcher("MyApp.Fail", (_, _) =>
                    {
                        dispatchCalls++;
                        throw new InvalidOperationException("simulated failure");
                    }));
            })
            .Build();

        var store = host.Services.GetRequiredService<InMemoryOutboxStore>();
        await store.EnqueueAsync("MyApp.Fail", new byte[] { 1 }, null, default);

        await host.StartAsync();

        // Poll until dead-lettered (at most 3s)
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var all = store.AllEntries();
            if (all.Count > 0 && AllExhausted(all))
                break;
            await Task.Delay(30);
        }

        static bool AllExhausted(IReadOnlyList<InMemoryOutboxStore.InMemoryOutboxEntry> entries)
        {
            foreach (var e in entries)
            {
                if (e.RetryCount < 2 && e.Status != InMemoryOutboxStore.InMemoryEntryStatus.DeadLetter)
                    return false;
            }
            return true;
        }

        await host.StopAsync();

        dispatchCalls.Should().BeGreaterOrEqualTo(2);
        var finalEntry = store.AllEntries().First();
        finalEntry.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.DeadLetter);
    }

    [Fact]
    public async Task Worker_NoDispatcher_DeadLettersMessage()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutboxInMemory();
                services.AddOutbox(o =>
                {
                    o.BatchSize = 10;
                    o.PollingInterval = TimeSpan.FromMilliseconds(30);
                });
                // No dispatcher registered for "MyApp.Unknown"
            })
            .Build();

        var store = host.Services.GetRequiredService<InMemoryOutboxStore>();
        await store.EnqueueAsync("MyApp.Unknown", new byte[] { 1 }, null, default);

        await host.StartAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var polled = store.AllEntries();
            if (polled.Count > 0 && AllDeadLettered(polled))
                break;
            await Task.Delay(30);
        }

        static bool AllDeadLettered(IReadOnlyList<InMemoryOutboxStore.InMemoryOutboxEntry> entries)
        {
            foreach (var e in entries)
            {
                if (e.Status != InMemoryOutboxStore.InMemoryEntryStatus.DeadLetter)
                    return false;
            }
            return true;
        }

        await host.StopAsync();

        // After StopAsync, verify the entry was dead-lettered
        var all = store.AllEntries();
        all.Should().ContainSingle()
           .Which.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.DeadLetter);
    }

    private sealed class TestDispatcher(
        string typeName,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> action) : IOutboxTypeDispatcher
    {
        public string TypeName => typeName;
        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => action(payload, ct);
    }
}
