using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox;
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
    public void AddOutboxInMemory_LegacyShim_StillRegistersStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();

#pragma warning disable CS0618 // exercise the legacy shim
        services.AddOutboxInMemory();
#pragma warning restore CS0618

        services.BuildServiceProvider().GetService<IOutboxStore>().Should().NotBeNull();
    }
}
