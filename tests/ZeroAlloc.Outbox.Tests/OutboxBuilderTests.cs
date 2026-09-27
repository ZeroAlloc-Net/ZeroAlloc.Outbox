using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public class OutboxBuilderTests
{
    [Fact]
    public void AddOutbox_ReturnsBuilder_BackedBySameServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var builder = services.AddOutbox();

        builder.Should().NotBeNull();
        builder.Should().BeAssignableTo<IOutboxBuilder>();
        builder.Services.Should().BeSameAs(services);
    }

    [Fact]
    public void WithInMemoryStore_RegistersInMemoryStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOutbox().WithInMemoryStore();

        var sp = services.BuildServiceProvider();
        sp.GetService<IOutboxStore>().Should().NotBeNull();
    }

    [Fact]
    public void WithEfCore_RegistersEfCoreStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DashboardTestDbContext>(opts => opts.UseSqlite("DataSource=:memory:"));

        services.AddOutbox().WithEfCore<DashboardTestDbContext>();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetService<IOutboxStore>().Should().NotBeNull();
    }

    [Fact]
    public void WithDashboardEvents_RegistersPublisher()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddOutbox().WithDashboardEvents();

        services.BuildServiceProvider().GetService<IOutboxDashboardEventPublisher>().Should().NotBeNull();
    }
}
