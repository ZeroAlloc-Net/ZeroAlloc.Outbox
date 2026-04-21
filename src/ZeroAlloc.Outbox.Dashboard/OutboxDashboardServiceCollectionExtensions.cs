using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox.Dashboard;

/// <summary>
/// DI extensions for configuring the ZeroAlloc.Outbox dashboard.
/// Reserved for future use — real service registrations land in Tasks 8-11.
/// </summary>
public static class OutboxDashboardServiceCollectionExtensions
{
    /// <summary>Registers services required by the outbox dashboard.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddOutboxDashboard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Stub: no services registered yet. Real registrations land in Tasks 8-11.
        return services;
    }
}
