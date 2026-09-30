using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Outbox;

public static partial class OutboxServiceCollectionExtensions
{
    internal const string MissingSerializerMessage =
        "ZeroAlloc.Outbox: no IOutboxSerializer is configured. Since Outbox 4.0, AddOutbox() no longer " +
        "falls back to reflection-based JSON; choose a serializer explicitly:\n" +
        "  AOT-safe:   annotate your message types with [ZeroAllocSerializable] and call " +
        "services.AddSerializerDispatcher() from ZeroAlloc.Serialisation.\n" +
        "  Reflection: call services.AddOutbox().WithSystemTextJsonSerializer() " +
        "for System.Text.Json, which is not trim- or AOT-safe.\n" +
        "Or register your own IOutboxSerializer. " +
        "See https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/migrating-to-v4.md";

    /// <summary>
    /// Registers the outbox background worker, options, options validation and the default
    /// serializer. Register a store separately, for example with <c>.WithInMemoryStore()</c> or
    /// <c>.WithEfCore&lt;TContext&gt;()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is trim- and AOT-safe. It never registers the reflection-based
    /// <see cref="SystemTextJsonOutboxSerializer"/> on its own. The <see cref="IOutboxSerializer"/>
    /// it registers resolves to the AOT-safe <see cref="DispatchingOutboxSerializer"/> when an
    /// <see cref="ISerializerDispatcher"/> is registered, before or after this call, for example
    /// with <c>services.AddSerializerDispatcher()</c> from <c>ZeroAlloc.Serialisation</c>.
    /// </para>
    /// <para>
    /// When there is none, resolving <see cref="IOutboxSerializer"/> throws an
    /// <see cref="InvalidOperationException"/> that names both options, and so does starting the
    /// host when a registered <see cref="IOutboxTypeDispatcher"/> needs a serializer. Opt in to
    /// reflection-based JSON with <see cref="WithSystemTextJsonSerializer"/>. An
    /// <see cref="IOutboxSerializer"/> the application registered itself before this call is kept.
    /// </para>
    /// <para>
    /// An application that never serializes an outbox payload, such as one that uses the worker
    /// only for ZeroAlloc.Saga commands, needs no serializer at all.
    /// </para>
    /// <para>
    /// This registers the default pipeline. Calling this method again adds its
    /// <paramref name="configure"/> delegate but no second worker, and the store adapters throw
    /// when a second, different default store is registered. To run another pipeline next to it,
    /// with its own store, options and worker, register it under a name with
    /// <see cref="AddOutbox(IServiceCollection, string, Action{OutboxOptions})"/>.
    /// </para>
    /// <para>
    /// Logging is not registered by this method. When using a bare <c>HostBuilder</c> in tests,
    /// add <c>services.AddLogging()</c> explicitly.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the default pipeline's options, or null to keep them.</param>
    /// <returns>A builder for the default pipeline.</returns>
    public static IOutboxBuilder AddOutbox(
        this IServiceCollection services,
        Action<OutboxOptions>? configure)
    {
        AddSharedServices(services);
        if (configure is not null)
            services.Configure(configure);

        // AddHostedService registers through TryAddEnumerable, so calling AddOutbox() twice still
        // starts one worker. OutboxRegistrationTests pins that.
        services.AddHostedService<OutboxWorkerService>();
        return new OutboxBuilder(services);
    }

    // This overload and the one above replace a single AddOutbox with an optional configure
    // parameter, so the named-pipeline overload below does not add parameters after an optional
    // one, which RS0027 rejects. Existing binaries still bind to the two-parameter method.

    /// <inheritdoc cref="AddOutbox(IServiceCollection, Action{OutboxOptions})"/>
    public static IOutboxBuilder AddOutbox(this IServiceCollection services)
        => services.AddOutbox(configure: null);

    /// <summary>
    /// Registers a named outbox pipeline: a worker of its own that polls the pipeline's own store
    /// with the pipeline's own options. Register its store on the returned builder, for example
    /// with <c>.WithOrm(dialect)</c> or <c>.WithEfCore&lt;TContext&gt;()</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">
    /// The pipeline's name. It keys the pipeline's <see cref="IOutboxStore"/> and names its
    /// <see cref="OutboxOptions"/>. Must not be empty or whitespace.
    /// </param>
    /// <param name="configure">Configures the pipeline's options.</param>
    /// <returns>A builder for the pipeline, on which the store adapters register a keyed store.</returns>
    /// <remarks>
    /// <para>
    /// The pipeline's store is registered as a keyed <see cref="IOutboxStore"/> with
    /// <paramref name="name"/> as its key. A generated <c>Add{Name}Outbox()</c> called on the returned
    /// builder registers that message's <see cref="IOutboxWriter{T}"/> keyed by
    /// <paramref name="name"/>; inject it with <c>[FromKeyedServices(name)]</c> to write into the
    /// pipeline. The unkeyed writer keeps writing to the default pipeline. The pipeline's options are the named <see cref="OutboxOptions"/> read through
    /// <c>IOptionsMonitor&lt;OutboxOptions&gt;.Get(name)</c>. They start from the
    /// <see cref="OutboxOptions"/> defaults, not from the default pipeline's options, and are
    /// validated like them when the host starts.
    /// </para>
    /// <para>
    /// The pipeline's worker tags its metrics and <c>outbox.dispatch</c> activities with
    /// <c>outbox.pipeline</c> set to <paramref name="name"/>, and its log entries with an
    /// <c>OutboxPipeline</c> scope. It publishes dashboard events to the
    /// <see cref="IOutboxDashboardEventPublisher"/> keyed by <paramref name="name"/>, which
    /// <c>WithDashboardEvents()</c> on the returned builder registers. The pipeline is listed as a
    /// <see cref="NamedOutboxPipeline"/>, so the dashboard can offer it in its pipeline selector
    /// when its store has an <see cref="IOutboxDashboardStore"/>.
    /// </para>
    /// <para>
    /// Every pipeline shares the serializer and the message dispatchers, so a message type
    /// registered once can be dispatched by any pipeline. A container may register named
    /// pipelines without the default one.
    /// </para>
    /// <para>
    /// Calling this again with the same name adds its <paramref name="configure"/> delegate but no
    /// second worker, and the store adapters throw when a second, different store is registered
    /// for the same name, or when two pipelines would poll the same outbox table.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <example>
    /// <code>
    /// services.AddOutbox().WithEfCore&lt;AppDbContext&gt;();
    /// services.AddOutbox("workflow", o => o.LeaseDuration = TimeSpan.FromMinutes(30))
    ///         .WithOrm(OutboxOrmDialect.Postgres);
    /// </code>
    /// </example>
    public static INamedOutboxBuilder AddOutbox(
        this IServiceCollection services,
        string name,
        Action<OutboxOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        AddSharedServices(services);
        services.Configure(name, configure);

        // One worker per name. AddHostedService cannot tell two factory registrations apart, so
        // the worker is added only when no registration carries a factory for this name yet.
        var registered = false;
        foreach (var descriptor in services)
        {
            // Read IsKeyedService first: ImplementationFactory throws on a keyed descriptor.
            if (!descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationFactory?.Target is OutboxPipelineWorkerFactory factory
                && string.Equals(factory.Name, name, StringComparison.Ordinal))
            {
                registered = true;
                break;
            }
        }

        if (!registered)
        {
            services.AddSingleton<IHostedService>(new OutboxPipelineWorkerFactory(name).Create);
            services.AddSingleton(new NamedOutboxPipeline(name));
        }

        return new NamedOutboxBuilder(services, name);
    }

    /// <summary>
    /// The services every pipeline shares: options, options validation and the serializer.
    /// </summary>
    private static void AddSharedServices(IServiceCollection services)
    {
        services.AddOptions<OutboxOptions>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<OutboxOptions>, OutboxOptionsValidator>());

        // The dispatcher is looked up when the serializer is first resolved, so
        // AddSerializerDispatcher() may run before or after AddOutbox().
        services.TryAddSingleton<IOutboxSerializer>(sp =>
        {
            var dispatcher = sp.GetService<ISerializerDispatcher>();
            return dispatcher is not null
                ? new DispatchingOutboxSerializer(dispatcher)
                : throw new InvalidOperationException(MissingSerializerMessage);
        });
    }

    /// <summary>
    /// Creates a named pipeline's worker. A named class rather than a lambda, so a later
    /// <c>AddOutbox(name, configure)</c> call can read the name back from the registration.
    /// </summary>
    private sealed class OutboxPipelineWorkerFactory(string name)
    {
        public string Name { get; } = name;

        public IHostedService Create(IServiceProvider sp)
            => new OutboxWorkerService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                Name,
                sp.GetRequiredService<IOptionsMonitor<OutboxOptions>>().Get(Name),
                sp.GetRequiredService<ILogger<OutboxWorkerService>>());
    }

    /// <summary>
    /// Serializes outbox payloads with the reflection-based
    /// <see cref="SystemTextJsonOutboxSerializer"/>.
    /// </summary>
    /// <remarks>
    /// This replaces every <see cref="IOutboxSerializer"/> registered so far, including the
    /// <see cref="DispatchingOutboxSerializer"/> default that <c>AddOutbox()</c> selects when an
    /// <see cref="ISerializerDispatcher"/> is registered. It is not trim- or AOT-safe; for NativeAOT,
    /// call <c>services.AddSerializerDispatcher()</c> instead and leave this out.
    /// </remarks>
    [RequiresUnreferencedCode("WithSystemTextJsonSerializer registers SystemTextJsonOutboxSerializer, which uses reflection-based System.Text.Json and may not work after trimming. For trim- and AOT-safe serialisation, call services.AddSerializerDispatcher() instead.")]
    [RequiresDynamicCode("WithSystemTextJsonSerializer registers SystemTextJsonOutboxSerializer, which uses reflection-based System.Text.Json and may need runtime code generation. For AOT-safe serialisation, call services.AddSerializerDispatcher() instead.")]
    public static IOutboxBuilder WithSystemTextJsonSerializer(this IOutboxBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.RemoveAll<IOutboxSerializer>();
        builder.Services.AddSingleton<IOutboxSerializer>(new SystemTextJsonOutboxSerializer());
        return builder;
    }
}
