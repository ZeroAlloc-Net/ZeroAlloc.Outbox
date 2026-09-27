using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// One container hosts one outbox pipeline. Registering the same store twice is a no-op, and
/// registering a different second store throws instead of silently replacing the first.
/// See ZeroAlloc.Outbox#206.
/// </summary>
public class OutboxRegistrationTests
{
    [Fact]
    public void AddOutbox_CalledTwice_RegistersOneWorker()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOutbox().WithInMemoryStore();
        services.AddOutbox();

        services.Count(d => d.ServiceType == typeof(IHostedService)
                            && d.ImplementationType == typeof(OutboxWorkerService))
            .Should().Be(1);

        using var sp = services.BuildServiceProvider();
        sp.GetServices<IHostedService>().OfType<OutboxWorkerService>().Should().ContainSingle();
    }

    [Fact]
    public void WithInMemoryStore_CalledTwice_KeepsOneStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOutbox().WithInMemoryStore().WithInMemoryStore();

        services.Count(d => d.ServiceType == typeof(IOutboxStore)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IOutboxDashboardStore)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(InMemoryOutboxStore)).Should().Be(1);

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IOutboxStore>().Should().BeSameAs(sp.GetRequiredService<InMemoryOutboxStore>());
    }

    [Fact]
    public void WithEfCore_CalledTwiceWithSameContext_KeepsOneStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DashboardTestDbContext>(opts => opts.UseSqlite("DataSource=:memory:"));

        services.AddOutbox().WithEfCore<DashboardTestDbContext>().WithEfCore<DashboardTestDbContext>();

        services.Count(d => d.ServiceType == typeof(IOutboxStore)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IOutboxDashboardStore)).Should().Be(1);

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOutboxStore>()
            .Should().BeOfType<EfCoreOutboxStore<DashboardTestDbContext>>();
    }

    [Fact]
    public void WithEfCore_WithADifferentContext_Throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddOutbox().WithEfCore<DashboardTestDbContext>();

        var act = () => builder.WithEfCore<ServerClaimDbContext>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithEfCore<ServerClaimDbContext>()*EfCoreOutboxStore<DashboardTestDbContext>*issues/206*");
    }

    [Fact]
    public void WithEfCore_AfterWithInMemoryStore_Throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddOutbox().WithInMemoryStore();

        var act = () => builder.WithEfCore<DashboardTestDbContext>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithEfCore<DashboardTestDbContext>()*InMemoryOutboxStore*");
    }

    [Fact]
    public void WithInMemoryStore_AfterACustomStore_Throws()
    {
        var services = new ServiceCollection();
        services.AddScoped<IOutboxStore, CustomStore>();

        var act = () => services.AddOutbox().WithInMemoryStore();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithInMemoryStore()*CustomStore*");
    }

    [Fact]
    public void WithInMemoryStore_KeepsADashboardStoreTheApplicationRegistered()
    {
        var services = new ServiceCollection();
        var custom = new InMemoryOutboxStore();
        services.AddSingleton<IOutboxDashboardStore>(custom);

        services.AddOutbox().WithInMemoryStore();

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IOutboxDashboardStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void ATestHost_CanSwapTheStore_AfterRemovingTheFirst()
    {
        // The recipe docs/migrating-to-v4.md gives for a test host that replaces the app's store.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DashboardTestDbContext>(opts => opts.UseSqlite("DataSource=:memory:"));
        services.AddOutbox().WithEfCore<DashboardTestDbContext>();

        services.RemoveAll<IOutboxStore>();
        services.RemoveAll<IOutboxDashboardStore>();
        services.AddOutbox().WithInMemoryStore();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOutboxStore>().Should().BeOfType<InMemoryOutboxStore>();
        scope.ServiceProvider.GetRequiredService<IOutboxDashboardStore>().Should().BeOfType<InMemoryOutboxStore>();
    }

    [Fact]
    public void AKeyedStore_DoesNotConflictWithTheDefaultPipeline()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IOutboxStore, CustomStore>("other");

        var act = () => services.AddOutbox().WithInMemoryStore();

        act.Should().NotThrow();
    }

    private sealed class CustomStore : IOutboxStore
    {
        public ValueTask EnqueueAsync(
            string typeName, ReadOnlyMemory<byte> payload, System.Data.Common.DbTransaction? transaction, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> MarkFailedAsync(
            OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
