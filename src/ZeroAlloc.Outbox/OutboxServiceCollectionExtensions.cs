using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox background worker, default serializer, and options.
    /// Call <c>AddOutboxEfCore</c> or <c>AddOutboxInMemory</c> separately to register the store.
    /// </summary>
    public static IServiceCollection AddOutbox(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
    {
        services.AddOptions<OutboxOptions>();
        if (configure is not null)
            services.Configure(configure);

#pragma warning disable IL2026, IL2091
        services.AddSingleton<IOutboxSerializer, SystemTextJsonOutboxSerializer>();
#pragma warning restore IL2026, IL2091
        services.AddHostedService<OutboxWorkerService>();
        return services;
    }
}
