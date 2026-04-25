using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox;

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
}
