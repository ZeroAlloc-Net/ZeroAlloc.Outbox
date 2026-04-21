using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public sealed class WorkerDashboardEventsTests
{
    [Fact]
    public async Task MessageDispatched_PublishesMessageDispatchedEvent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOutboxInMemory();
        services.AddSingleton<IOutboxTypeDispatcher, SuccessfulTypeDispatcher>();
        services.AddOutboxDashboardEvents();
        services.Configure<OutboxOptions>(opt =>
        {
            opt.PollingInterval = TimeSpan.FromMilliseconds(50);
        });

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IOutboxStore>();
        var pub = provider.GetRequiredService<IOutboxDashboardEventPublisher>();
        using var sub = pub.Subscribe();

        await store.EnqueueAsync(SuccessfulTypeDispatcher.Name, new byte[] { 1 }, null, CancellationToken.None);

        var worker = new OutboxWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            provider.GetRequiredService<ILogger<OutboxWorkerService>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);

        var evt = await sub.Reader.ReadAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsType<MessageDispatchedEvent>(evt);
    }

    [Fact]
    public async Task DispatchFailure_PublishesMessageFailedEvent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOutboxInMemory();
        services.AddSingleton<IOutboxTypeDispatcher, FailingTypeDispatcher>();
        services.AddOutboxDashboardEvents();
        services.Configure<OutboxOptions>(opt =>
        {
            opt.MaxAttempts = 5; // so a single failure does not dead-letter
            opt.PollingInterval = TimeSpan.FromMilliseconds(50);
            opt.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
        });

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IOutboxStore>();
        var pub = provider.GetRequiredService<IOutboxDashboardEventPublisher>();
        using var sub = pub.Subscribe();

        await store.EnqueueAsync(FailingTypeDispatcher.Name, new byte[] { 1 }, null, CancellationToken.None);

        var worker = new OutboxWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            provider.GetRequiredService<ILogger<OutboxWorkerService>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);

        var evt = await sub.Reader.ReadAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsType<MessageFailedEvent>(evt);
    }

    [Fact]
    public async Task DispatchExhaustion_PublishesMessageDeadLetteredEvent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOutboxInMemory();
        services.AddSingleton<IOutboxTypeDispatcher, FailingTypeDispatcher>();
        services.AddOutboxDashboardEvents();
        services.Configure<OutboxOptions>(opt =>
        {
            opt.MaxAttempts = 1; // first failure dead-letters
            opt.PollingInterval = TimeSpan.FromMilliseconds(50);
        });

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IOutboxStore>();
        var pub = provider.GetRequiredService<IOutboxDashboardEventPublisher>();
        using var sub = pub.Subscribe();

        await store.EnqueueAsync(FailingTypeDispatcher.Name, new byte[] { 1 }, null, CancellationToken.None);

        var worker = new OutboxWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            provider.GetRequiredService<ILogger<OutboxWorkerService>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);

        var evt = await sub.Reader.ReadAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsType<MessageDeadLetteredEvent>(evt);
    }

    [Fact]
    public async Task NoDispatcherRegistered_PublishesMessageDeadLetteredEvent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOutboxInMemory();
        services.AddOutboxDashboardEvents();
        services.Configure<OutboxOptions>(opt =>
        {
            opt.PollingInterval = TimeSpan.FromMilliseconds(50);
        });

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IOutboxStore>();
        var pub = provider.GetRequiredService<IOutboxDashboardEventPublisher>();
        using var sub = pub.Subscribe();

        await store.EnqueueAsync("Unknown.Type", new byte[] { 1 }, null, CancellationToken.None);

        var worker = new OutboxWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            provider.GetRequiredService<ILogger<OutboxWorkerService>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(cts.Token);

        var evt = await sub.Reader.ReadAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.IsType<MessageDeadLetteredEvent>(evt);
    }

    private sealed class SuccessfulTypeDispatcher : IOutboxTypeDispatcher
    {
        public const string Name = "Test.Success";
        public string TypeName => Name;
        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class FailingTypeDispatcher : IOutboxTypeDispatcher
    {
        public const string Name = "Test.Fail";
        public string TypeName => Name;
        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }
}
