using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// A container hosts one outbox pipeline. Calling this method again adds its
    /// <paramref name="configure"/> delegate but no second worker, and the store adapters throw
    /// when a second, different store is registered.
    /// </para>
    /// <para>
    /// Logging is not registered by this method. When using a bare <c>HostBuilder</c> in tests,
    /// add <c>services.AddLogging()</c> explicitly.
    /// </para>
    /// </remarks>
    public static IOutboxBuilder AddOutbox(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
    {
        services.AddOptions<OutboxOptions>();
        if (configure is not null)
            services.Configure(configure);
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

        // AddHostedService registers through TryAddEnumerable, so calling AddOutbox() twice still
        // starts one worker. OutboxRegistrationTests pins that.
        services.AddHostedService<OutboxWorkerService>();
        return new OutboxBuilder(services);
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
