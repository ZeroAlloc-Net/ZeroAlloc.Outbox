using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Mediator;

namespace ZeroAlloc.Outbox.Mediator.Tests;

public class OutboxMediatorBuilderTests
{
    [Fact]
    public void WithMediator_RegistersDispatcher()
    {
        var services = new ServiceCollection();
        services.AddLogging();

#pragma warning disable IL2026, IL3050
        services.AddOutbox().WithMediator<OrderPlaced>();
#pragma warning restore IL2026, IL3050

        var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetService<IOutboxDispatcher<OrderPlaced>>();

        dispatcher.Should().NotBeNull();
        dispatcher.Should().BeOfType<MediatorOutboxDispatcher<OrderPlaced>>();
    }

    [Fact]
    public void AddOutboxMediator_LegacyShim_StillRegisters()
    {
        var services = new ServiceCollection();

#pragma warning disable CS0618 // exercise the legacy shim
        services.AddOutboxMediator<OrderPlaced>();
#pragma warning restore CS0618

        var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetService<IOutboxDispatcher<OrderPlaced>>();

        dispatcher.Should().NotBeNull();
        dispatcher.Should().BeOfType<MediatorOutboxDispatcher<OrderPlaced>>();
    }

    public sealed record OrderPlaced(string OrderId) : INotification;
}
