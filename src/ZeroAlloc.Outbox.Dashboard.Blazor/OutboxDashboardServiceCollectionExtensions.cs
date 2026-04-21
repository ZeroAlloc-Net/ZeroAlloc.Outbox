using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox.Dashboard.Blazor;

/// <summary>Registration helpers for the ZeroAlloc.Outbox dashboard Blazor component.</summary>
public static class OutboxDashboardServiceCollectionExtensions
{
    /// <summary>
    /// Placeholder for future Blazor-specific services. Call this before
    /// rendering <c>&lt;OutboxDashboard /&gt;</c> in your Blazor app.
    /// </summary>
    public static IServiceCollection AddOutboxDashboardBlazor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Currently no services required — the component is a pure iframe wrapper.
        // This method exists to give consumers a clear extension point for future
        // v2 features (e.g. native Razor re-implementation with SignalR).
        return services;
    }
}
