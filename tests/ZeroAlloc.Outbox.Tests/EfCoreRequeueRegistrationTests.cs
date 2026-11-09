using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Tests;

public sealed class EfCoreRequeueRegistrationTests
{
    [Fact]
    public void WithEfCore_ResolvesIOutboxRequeue_ForTheDefaultPipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DashboardTestDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        services.AddOutbox().WithEfCore<DashboardTestDbContext>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var requeue = scope.ServiceProvider.GetRequiredService<IOutboxRequeue>();

        requeue.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<IOutboxStore>());
        requeue.Should().BeOfType<EfCoreOutboxStore<DashboardTestDbContext>>();
    }

    [Fact]
    public void WithEfCore_ResolvesIOutboxRequeue_ForANamedPipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DashboardTestDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        services.AddOutbox("channel", _ => { }).WithEfCore<DashboardTestDbContext>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var requeue = scope.ServiceProvider.GetRequiredKeyedService<IOutboxRequeue>("channel");

        requeue.Should().BeSameAs(scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("channel"));
        scope.ServiceProvider.GetService<IOutboxRequeue>().Should().BeNull("the default pipeline has none");
    }
}
