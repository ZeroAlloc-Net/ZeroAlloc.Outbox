using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// Broad integration tests that exercise store + worker + dashboard publisher in a
/// single scenario. Per-component behavior is covered in more depth by the other
/// dashboard test suites.
/// </summary>
public sealed class DashboardEndToEndTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InMemory_FullFlow_DispatchPublishesEvent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOutboxInMemory();
        services.AddSingleton<IOutboxTypeDispatcher, SuccessDispatcher>();
        services.AddOutboxDashboardEvents();
        services.Configure<OutboxOptions>(o =>
        {
            o.PollingInterval = TimeSpan.FromMilliseconds(50);
        });

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IOutboxStore>();
        var publisher = provider.GetRequiredService<IOutboxDashboardEventPublisher>();
        using var subscription = publisher.Subscribe();

        await store.EnqueueAsync(SuccessDispatcher.TypeNameConst, new byte[] { 1 }, null, CancellationToken.None);

        var worker = new OutboxWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            provider.GetRequiredService<ILogger<OutboxWorkerService>>());

        using var cts = new CancellationTokenSource(TestTimeout);
        await worker.StartAsync(cts.Token);

        var evt = await ReadUntilAsync<MessageDispatchedEvent>(subscription, cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(evt);
        Assert.Equal(1, evt!.AttemptCount);
    }

    [Fact]
    public async Task EfCore_FullFlow_DispatchPublishesEvent()
    {
        await using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync(CancellationToken.None);

        // Pre-create schema on the shared connection so every scoped DbContext sees it.
        var bootOpts = new DbContextOptionsBuilder<DashboardTestDbContext>()
            .UseSqlite(conn)
            .Options;
        await using (var bootDb = new DashboardTestDbContext(bootOpts))
        {
            await bootDb.Database.EnsureCreatedAsync(CancellationToken.None);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<DashboardTestDbContext>(_ => new DashboardTestDbContext(bootOpts));
        services.AddScoped<EfCoreOutboxStore<DashboardTestDbContext>>();
        services.AddScoped<IOutboxStore>(sp => sp.GetRequiredService<EfCoreOutboxStore<DashboardTestDbContext>>());
        services.AddScoped<IOutboxTypeDispatcher, SuccessDispatcher>();
        services.AddOutboxDashboardEvents();
        services.Configure<OutboxOptions>(o =>
        {
            o.PollingInterval = TimeSpan.FromMilliseconds(50);
        });

        await using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<IOutboxDashboardEventPublisher>();
        using var subscription = publisher.Subscribe();

        using (var scope = provider.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            await store.EnqueueAsync(SuccessDispatcher.TypeNameConst, new byte[] { 1 }, null, CancellationToken.None);
        }

        var worker = new OutboxWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            provider.GetRequiredService<ILogger<OutboxWorkerService>>());

        using var cts = new CancellationTokenSource(TestTimeout);
        await worker.StartAsync(cts.Token);

        var evt = await ReadUntilAsync<MessageDispatchedEvent>(subscription, cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(evt);
        Assert.Equal(1, evt!.AttemptCount);
    }

    [Fact]
    public async Task Requeue_RevivesDeadLetteredMessage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOutboxInMemory();
        services.AddSingleton<IOutboxTypeDispatcher, FailingDispatcher>();
        services.AddOutboxDashboardEvents();
        services.Configure<OutboxOptions>(o =>
        {
            o.MaxAttempts = 1;
            o.PollingInterval = TimeSpan.FromMilliseconds(50);
        });

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IOutboxStore>();
        var dashStore = (IOutboxDashboardStore)provider.GetRequiredService<InMemoryOutboxStore>();
        var publisher = provider.GetRequiredService<IOutboxDashboardEventPublisher>();
        using var subscription = publisher.Subscribe();

        await store.EnqueueAsync(FailingDispatcher.TypeNameConst, new byte[] { 1 }, null, CancellationToken.None);

        var worker = new OutboxWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            provider.GetRequiredService<ILogger<OutboxWorkerService>>());

        using var cts = new CancellationTokenSource(TestTimeout);
        await worker.StartAsync(cts.Token);

        var deadEvt = await ReadUntilAsync<MessageDeadLetteredEvent>(subscription, cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(deadEvt);

        // Verify it is visible in the dead-letter group before requeue.
        var before = await dashStore.GetSnapshotAsync(10, CancellationToken.None);
        before.DeadLettered.Should().ContainSingle();
        var id = before.DeadLettered[0].Id;
        Assert.Empty(before.Pending);

        await dashStore.RequeueAsync(id, CancellationToken.None);

        var after = await dashStore.GetSnapshotAsync(10, CancellationToken.None);
        after.DeadLettered.Should().BeEmpty();
        after.Pending.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    private static async Task<T?> ReadUntilAsync<T>(
        OutboxDashboardSubscription subscription,
        CancellationToken ct)
        where T : OutboxDashboardEvent
    {
        while (await subscription.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (subscription.Reader.TryRead(out var evt))
            {
                if (evt is T typed)
                    return typed;
            }
        }
        return null;
    }

    private sealed class SuccessDispatcher : IOutboxTypeDispatcher
    {
        public const string TypeNameConst = "E2E.Success";
        public string TypeName => TypeNameConst;
        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    private sealed class FailingDispatcher : IOutboxTypeDispatcher
    {
        public const string TypeNameConst = "E2E.Fail";
        public string TypeName => TypeNameConst;
        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }
}
