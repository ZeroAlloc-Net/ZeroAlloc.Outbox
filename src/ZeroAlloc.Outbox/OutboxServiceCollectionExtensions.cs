using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox;

public static partial class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox background worker, serializer, and options.
    /// Call <c>AddOutboxEfCore</c> or <c>AddOutboxInMemory</c> separately to register the store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When an <see cref="ISerializerDispatcher"/> has already been registered (e.g. via
    /// <c>services.AddSerializerDispatcher()</c> from <c>ZeroAlloc.Serialisation</c>), the
    /// AOT-safe <see cref="DispatchingOutboxSerializer"/> is used automatically. Otherwise the
    /// reflection-based <see cref="SystemTextJsonOutboxSerializer"/> is registered as a fallback.
    /// </para>
    /// <para>
    /// For full AOT safety, annotate your message types with <c>[ZeroAllocSerializable]</c>,
    /// call <c>services.AddSerializerDispatcher()</c> before this method, and suppress the
    /// <c>IL2026</c>/<c>IL3050</c> warnings on the <c>AddOutbox</c> call site.
    /// </para>
    /// <para>
    /// Logging is not registered by this method. When using a bare <c>HostBuilder</c> in tests,
    /// add <c>services.AddLogging()</c> explicitly.
    /// </para>
    /// </remarks>
    [RequiresUnreferencedCode("AddOutbox may register SystemTextJsonOutboxSerializer which uses reflection-based JSON. Call services.AddSerializerDispatcher() first for AOT-safe serialisation.")]
    [RequiresDynamicCode("AddOutbox may register SystemTextJsonOutboxSerializer which may require runtime code generation. Call services.AddSerializerDispatcher() first for AOT-safe serialisation.")]
    public static IServiceCollection AddOutbox(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
    {
        services.AddOptions<OutboxOptions>();
        if (configure is not null)
            services.Configure(configure);

        // Prefer DispatchingOutboxSerializer when ISerializerDispatcher is registered (AOT-safe).
        // Fall back to the reflection-based SystemTextJsonOutboxSerializer otherwise.
        services.TryAddSingleton<IOutboxSerializer>(sp =>
        {
            var dispatcher = sp.GetService<ISerializerDispatcher>();
            if (dispatcher is not null)
                return new DispatchingOutboxSerializer(dispatcher);

#pragma warning disable IL2026, IL3050
            return new SystemTextJsonOutboxSerializer();
#pragma warning restore IL2026, IL3050
        });

        services.AddHostedService<OutboxWorkerService>();
        return services;
    }
}
