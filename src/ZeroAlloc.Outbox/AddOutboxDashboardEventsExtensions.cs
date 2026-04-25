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
    public static IOutboxBuilder WithDashboardEvents(this IOutboxBuilder builder)
    {
        builder.Services.TryAddSingleton<IOutboxDashboardEventPublisher, ChannelOutboxDashboardEventPublisher>();
        return builder;
    }

    /// <summary>
    /// Legacy shim that preserves the v1.x extension shape. Will be removed in the next major.
    /// </summary>
    [Obsolete("Use AddOutbox().WithDashboardEvents() instead. Will be removed in the next major.", DiagnosticId = "ZAOBOX006")]
    public static IServiceCollection AddOutboxDashboardEvents(this IServiceCollection services)
    {
        services.TryAddSingleton<IOutboxDashboardEventPublisher, ChannelOutboxDashboardEventPublisher>();
        return services;
    }
}
