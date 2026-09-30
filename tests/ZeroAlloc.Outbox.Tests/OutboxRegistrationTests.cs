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
/// A pipeline has one store. Registering the same store twice is a no-op, and registering a
/// different second store for the same pipeline throws instead of silently replacing the first.
/// Named pipelines, ZeroAlloc.Outbox#206, each get a keyed store and a worker of their own.
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
            .WithMessage("*WithEfCore<ServerClaimDbContext>()*EfCoreOutboxStore<DashboardTestDbContext>*AddOutbox(name, configure)*");
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

    [Fact]
    public void AddOutbox_WithName_CalledTwice_RegistersOneWorkerPerName()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOutbox().WithInMemoryStore();
        services.AddOutbox("a", _ => { }).WithInMemoryStore();
        services.AddOutbox("a", _ => { });
        services.AddOutbox("b", _ => { }).WithInMemoryStore();
        services.AddOutbox();

        using var sp = services.BuildServiceProvider();
        sp.GetServices<IHostedService>().OfType<OutboxWorkerService>().Should().HaveCount(3,
            "the default pipeline and the pipelines 'a' and 'b' each have one worker");
    }

    [Fact]
    public void AddOutbox_WithName_BeforeTheDefault_StillRegistersTheDefaultWorker()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOutbox("a", _ => { }).WithInMemoryStore();
        services.AddOutbox().WithInMemoryStore();

        using var sp = services.BuildServiceProvider();
        sp.GetServices<IHostedService>().OfType<OutboxWorkerService>().Should().HaveCount(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AddOutbox_WithAnEmptyName_Throws(string name)
    {
        var act = () => new ServiceCollection().AddOutbox(name, _ => { });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddOutbox_WithName_ReturnsANamedBuilder()
    {
        var services = new ServiceCollection();

        var builder = services.AddOutbox("workflow", _ => { });

        builder.Name.Should().Be("workflow");
        builder.Services.Should().BeSameAs(services);
    }

    [Fact]
    public void WithInMemoryStore_OnANamedPipeline_RegistersAKeyedStoreOnly()
    {
        var services = new ServiceCollection();

        services.AddOutbox("a", _ => { }).WithInMemoryStore();

        using var sp = services.BuildServiceProvider();
        sp.GetService<IOutboxStore>().Should().BeNull();
        sp.GetService<IOutboxDashboardStore>().Should().BeNull();
        sp.GetRequiredKeyedService<IOutboxStore>("a")
            .Should().BeSameAs(sp.GetRequiredKeyedService<InMemoryOutboxStore>("a"));
    }

    [Fact]
    public void WithEfCore_OnANamedPipeline_RegistersAKeyedStoreOnly()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DashboardTestDbContext>(opts => opts.UseSqlite("DataSource=:memory:"));

        services.AddOutbox("a", _ => { }).WithEfCore<DashboardTestDbContext>();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetService<IOutboxStore>().Should().BeNull();
        scope.ServiceProvider.GetService<IOutboxDashboardStore>().Should().BeNull();
        scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("a")
            .Should().BeOfType<EfCoreOutboxStore<DashboardTestDbContext>>();
    }

    [Fact]
    public void TheSameStore_TwiceOnANamedPipeline_KeepsOneRegistration()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DashboardTestDbContext>(opts => opts.UseSqlite("DataSource=:memory:"));

        services.AddOutbox("a", _ => { }).WithEfCore<DashboardTestDbContext>();
        services.AddOutbox("a", _ => { }).WithEfCore<DashboardTestDbContext>();

        services.Count(d => d.ServiceType == typeof(IOutboxStore)).Should().Be(1);
    }

    [Fact]
    public void ADifferentStore_ForTheSameNamedPipeline_Throws()
    {
        // A duplicate registration for one pipeline does not silently win: it throws, as it does
        // for the default pipeline.
        var services = new ServiceCollection();
        services.AddOutbox("a", _ => { }).WithInMemoryStore();

        var act = () => services.AddOutbox("a", _ => { }).WithEfCore<DashboardTestDbContext>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithEfCore<DashboardTestDbContext>()*outbox pipeline 'a'*InMemoryOutboxStore*");
    }

    [Fact]
    public void ACustomKeyedStore_ForTheSameNamedPipeline_Throws()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IOutboxStore, CustomStore>("a");

        var act = () => services.AddOutbox("a", _ => { }).WithInMemoryStore();

        act.Should().Throw<InvalidOperationException>().WithMessage("*WithInMemoryStore()*'a'*CustomStore*");
    }

    [Fact]
    public void DifferentStores_OnDifferentPipelines_DoNotConflict()
    {
        var services = new ServiceCollection();

        var act = () =>
        {
            services.AddOutbox().WithEfCore<DashboardTestDbContext>();
            services.AddOutbox("a", _ => { }).WithEfCore<ServerClaimDbContext>();
            services.AddOutbox("b", _ => { }).WithInMemoryStore();
            services.AddOutbox("c", _ => { }).WithInMemoryStore();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void TheSameDbContext_OnTwoPipelines_Throws()
    {
        var services = new ServiceCollection();
        services.AddOutbox().WithEfCore<DashboardTestDbContext>();

        var act = () => services.AddOutbox("a", _ => { }).WithEfCore<DashboardTestDbContext>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*EfCoreOutboxStore<DashboardTestDbContext>*'a'*default outbox pipeline already uses it*");
    }

    [Fact]
    public void TheSameDbContext_OnTwoNamedPipelines_Throws()
    {
        var services = new ServiceCollection();
        services.AddOutbox("a", _ => { }).WithEfCore<DashboardTestDbContext>();

        var act = () => services.AddOutbox("b", _ => { }).WithEfCore<DashboardTestDbContext>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*pipeline 'b'*pipeline 'a' already uses it*");
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
