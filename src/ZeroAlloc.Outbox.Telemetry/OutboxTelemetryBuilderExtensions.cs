using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Outbox.Telemetry;

/// <summary>
/// Fluent extensions on <see cref="IOutboxBuilder"/> that wire ZeroAlloc.Telemetry
/// instrumentation into every registered <see cref="IOutboxTypeDispatcher"/>.
/// </summary>
public static class OutboxTelemetryBuilderExtensions
{
    /// <summary>
    /// Decorates every registered <see cref="IOutboxTypeDispatcher"/> with a source-generated
    /// OpenTelemetry proxy. Spans, counters, and histograms are emitted on
    /// <see cref="IOutboxTypeDispatcher.DispatchAsync"/>. ActivitySource: "ZeroAlloc.Outbox".
    /// Safe to call multiple times — already-decorated dispatchers are skipped.
    /// </summary>
    public static IOutboxBuilder WithTelemetry(this IOutboxBuilder builder)
    {
        var services = builder.Services;
        // Idempotence sentinel — once we've decorated, don't decorate again.
        if (services.Any(d => d.ServiceType == typeof(OutboxTelemetryMarker))) return builder;
        services.AddSingleton<OutboxTelemetryMarker>();

        for (int i = 0; i < services.Count; i++)
        {
            var d = services[i];
            if (d.ServiceType != typeof(IOutboxTypeDispatcher)) continue;

            var captured = d;
            services[i] = ServiceDescriptor.Describe(
                typeof(IOutboxTypeDispatcher),
                sp =>
                {
                    var inner = (IOutboxTypeDispatcher)CreateFromDescriptor(captured, sp);
                    return new InstrumentedOutboxDispatcher(inner);
                },
                captured.Lifetime);
        }
        return builder;
    }

    private static object CreateFromDescriptor(ServiceDescriptor d, IServiceProvider sp)
    {
        if (d.ImplementationInstance is not null) return d.ImplementationInstance;
        if (d.ImplementationFactory is not null) return d.ImplementationFactory(sp);
        return ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);
    }

    /// <summary>Marker singleton used to make <see cref="WithTelemetry"/> idempotent.</summary>
    private sealed class OutboxTelemetryMarker { }
}
