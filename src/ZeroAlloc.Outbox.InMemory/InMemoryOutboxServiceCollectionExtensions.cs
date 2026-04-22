using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox.InMemory;

public static class InMemoryOutboxServiceCollectionExtensions
{
    /// <summary>Registers the in-memory outbox store (for testing only).</summary>
    public static IServiceCollection AddOutboxInMemory(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryOutboxStore>();
        services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
        services.AddSingleton<IOutboxDashboardStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
        return services;
    }
}
