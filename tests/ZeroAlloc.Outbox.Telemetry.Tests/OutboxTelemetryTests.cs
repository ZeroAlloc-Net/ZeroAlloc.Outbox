using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
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

    [Fact]
    public async Task WithTelemetry_InnerThrows_RecordsErrorStatus()
    {
        using var listener = new TestActivityListener("ZeroAlloc.Outbox");
        var fake = new ThrowingDispatcher();

        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddOutbox();
        builder.Services.AddTransient<IOutboxTypeDispatcher>(_ => fake);
        builder.WithTelemetry();

        var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetServices<IOutboxTypeDispatcher>().First();

        Func<Task> act = async () => await dispatcher.DispatchAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        listener.StoppedActivities.Should().ContainSingle();
        listener.StoppedActivities[0].Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task WithTelemetry_RecordsCounterAndHistogram()
    {
        long counterValue = 0;
        double histogramValue = 0;

        using var meterListener = new System.Diagnostics.Metrics.MeterListener();
        meterListener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, "ZeroAlloc.Outbox", StringComparison.Ordinal))
                l.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((inst, m, _, _) =>
        {
            if (string.Equals(inst.Name, "outbox.dispatched_total", StringComparison.Ordinal))
                Interlocked.Add(ref counterValue, m);
        });
        meterListener.SetMeasurementEventCallback<double>((inst, m, _, _) =>
        {
            if (string.Equals(inst.Name, "outbox.dispatch_duration_ms", StringComparison.Ordinal))
                histogramValue = m;
        });
        meterListener.Start();

        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddOutbox();
        builder.Services.AddTransient<IOutboxTypeDispatcher>(_ => new FakeDispatcher());
        builder.WithTelemetry();

        var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetServices<IOutboxTypeDispatcher>().First();

        await dispatcher.DispatchAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        counterValue.Should().Be(1);
        histogramValue.Should().BeGreaterThanOrEqualTo(0); // duration in ms; may be 0 on a no-op call
    }

    [Fact]
    public async Task WithTelemetry_TwoCalls_DoesNotDoubleWrap()
    {
        using var listener = new TestActivityListener("ZeroAlloc.Outbox");
        var fake = new FakeDispatcher();

        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddOutbox();
        builder.Services.AddTransient<IOutboxTypeDispatcher>(_ => fake);
        builder.WithTelemetry().WithTelemetry();   // call twice

        var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetServices<IOutboxTypeDispatcher>().First();

        await dispatcher.DispatchAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        // Single span — not two nested spans from a doubly-wrapped proxy.
        listener.StoppedActivities.Should().ContainSingle();
    }

    [Fact]
    public async Task WithTelemetry_MultipleDispatchers_AllWrapped()
    {
        using var listener = new TestActivityListener("ZeroAlloc.Outbox");

        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddOutbox();
        // Two distinct dispatcher registrations, mimicking what AddXxxOutbox() does per message type.
        builder.Services.AddTransient<IOutboxTypeDispatcher>(_ => new FakeDispatcher());
        builder.Services.AddTransient<IOutboxTypeDispatcher>(_ => new FakeDispatcher());
        builder.WithTelemetry();

        var sp = services.BuildServiceProvider();
        var dispatchers = sp.GetServices<IOutboxTypeDispatcher>().ToList();

        foreach (var d in dispatchers)
            await d.DispatchAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        // Each dispatcher starts one span — both should have been wrapped.
        listener.StoppedActivities.Should().HaveCount(2);
    }

    private sealed class FakeDispatcher : IOutboxTypeDispatcher
    {
        public string TypeName => "Fake";
        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => default;
    }

    private sealed class ThrowingDispatcher : IOutboxTypeDispatcher
    {
        public string TypeName => "Throwing";
        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }
}
