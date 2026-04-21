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
    public static IServiceCollection AddOutboxDashboardEvents(this IServiceCollection services)
    {
        services.TryAddSingleton<IOutboxDashboardEventPublisher, ChannelOutboxDashboardEventPublisher>();
        return services;
    }
}
