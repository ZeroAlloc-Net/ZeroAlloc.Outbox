using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// Regression tests ensuring the README's setup snippets produce a service provider
/// from which the dashboard endpoints can resolve their dependencies.
/// </summary>
public sealed class DashboardDIResolutionTests
{
    [Fact]
    public void AddOutboxInMemory_ResolvesIOutboxDashboardStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOutbox();
        services.AddOutboxInMemory();
        services.AddOutboxDashboardEvents();

        using var provider = services.BuildServiceProvider();

        // Mirrors what MapOutboxDashboard's DI-injected handlers do:
        var dashboard = provider.GetRequiredService<IOutboxDashboardStore>();
        Assert.NotNull(dashboard);

        var publisher = provider.GetRequiredService<IOutboxDashboardEventPublisher>();
        Assert.NotNull(publisher);

        // Same instance as IOutboxStore (they share state).
        var store = provider.GetRequiredService<IOutboxStore>();
        Assert.Same(store, dashboard);
    }

    [Fact]
    public void AddOutboxEfCore_ResolvesIOutboxDashboardStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DashboardTestDbContext>(opts => opts.UseSqlite("DataSource=:memory:"));
        services.AddOutbox();
        services.AddOutboxEfCore<DashboardTestDbContext>();
        services.AddOutboxDashboardEvents();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var dashboard = scope.ServiceProvider.GetRequiredService<IOutboxDashboardStore>();
        Assert.NotNull(dashboard);

        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        Assert.Same(store, dashboard);
    }
}
