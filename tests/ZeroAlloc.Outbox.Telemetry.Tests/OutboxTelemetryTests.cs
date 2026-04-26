using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.Telemetry.Tests;

public class OutboxTelemetryTests
{
    [Fact]
    public async Task WithTelemetry_HotPathCall_StartsActivity()
    {
        using var listener = new TestActivityListener("ZeroAlloc.Outbox");
        var fake = new FakeDispatcher();

        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddOutbox();
        builder.Services.AddTransient<IOutboxTypeDispatcher>(_ => fake);
        builder.WithTelemetry();

        var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetServices<IOutboxTypeDispatcher>().First();

        await dispatcher.DispatchAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        listener.StoppedActivities.Should().ContainSingle();
        listener.StoppedActivities[0].DisplayName.Should().Be("outbox.dispatch");
    }

    private sealed class FakeDispatcher : IOutboxTypeDispatcher
    {
        public string TypeName => "Fake";
        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => default;
    }
}
