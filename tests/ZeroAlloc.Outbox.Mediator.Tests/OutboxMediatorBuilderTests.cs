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

        services.AddOutbox().WithMediator<OrderPlaced>();

        var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetService<IOutboxDispatcher<OrderPlaced>>();

        dispatcher.Should().NotBeNull();
        dispatcher.Should().BeOfType<MediatorOutboxDispatcher<OrderPlaced>>();
    }

    public sealed record OrderPlaced(string OrderId) : INotification;
}
