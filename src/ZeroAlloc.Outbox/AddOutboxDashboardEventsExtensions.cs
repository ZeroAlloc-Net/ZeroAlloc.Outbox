using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ZeroAlloc.Outbox;

public static class AddOutboxDashboardEventsExtensions
{
    /// <summary>
    /// Registers <see cref="IOutboxDashboardEventPublisher"/> for in-process dashboard event
    /// delivery. The dashboard package (<c>ZeroAlloc.Outbox.Dashboard</c>) subscribes to this
    /// publisher to stream events to SSE clients. Opt-in: the core outbox worker runs without it.
    /// </summary>
    /// <remarks>
    /// On a named pipeline's builder, from <c>AddOutbox(name, configure)</c>, this registers a
    /// publisher of the pipeline's own, keyed by its name. The pipeline's worker publishes to it,
    /// and the dashboard streams it when that pipeline is selected.
    /// </remarks>
    public static IOutboxBuilder WithDashboardEvents(this IOutboxBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder is INamedOutboxBuilder named)
            builder.Services.TryAddKeyedSingleton<IOutboxDashboardEventPublisher, ChannelOutboxDashboardEventPublisher>(named.Name);
        else
            builder.Services.TryAddSingleton<IOutboxDashboardEventPublisher, ChannelOutboxDashboardEventPublisher>();
        return builder;
    }
}
