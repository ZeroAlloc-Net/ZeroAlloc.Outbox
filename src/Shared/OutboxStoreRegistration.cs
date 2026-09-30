using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox;

/// <summary>
/// The rules every store adapter applies before it registers <see cref="IOutboxStore"/>: a pipeline
/// has one store, so a second, different store for the same pipeline throws instead of silently
/// replacing the first, and two pipelines never share one outbox table.
/// </summary>
/// <remarks>
/// Compiled into each store adapter as a linked source file, so it adds no public API to
/// ZeroAlloc.Outbox. A pipeline is identified by its name: null for the default pipeline, whose
/// store is unkeyed, or the name given to <c>AddOutbox(name, configure)</c>, whose store is keyed
/// by that name.
/// </remarks>
internal static class OutboxStoreRegistration
{
    /// <summary>
    /// The pipeline <paramref name="builder"/> registers into: its name for a named pipeline, or
    /// null for the default pipeline.
    /// </summary>
    public static string? PipelineOf(IOutboxBuilder builder)
        => builder is INamedOutboxBuilder named ? named.Name : null;

    /// <summary>
    /// The last registration of <paramref name="serviceType"/> in <paramref name="pipeline"/>:
    /// unkeyed for the default pipeline, keyed by the name for a named one.
    /// </summary>
    public static ServiceDescriptor? Find(IServiceCollection services, string? pipeline, Type serviceType)
    {
        ServiceDescriptor? found = null;
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == serviceType && BelongsTo(descriptor, pipeline))
                found = descriptor;
        }

        return found;
    }

    /// <summary>
    /// The object behind a registration's factory delegate, keyed or not, so an adapter can read
    /// back what it registered.
    /// </summary>
    public static object? FactoryTarget(ServiceDescriptor descriptor)
        => descriptor.IsKeyedService
            ? descriptor.KeyedImplementationFactory?.Target
            : descriptor.ImplementationFactory?.Target;

    /// <summary>
    /// Throws when <paramref name="pipeline"/> already has an <see cref="IOutboxStore"/> other
    /// than <paramref name="storeType"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="pipeline">The pipeline's name, or null for the default pipeline.</param>
    /// <param name="storeType">The concrete store type the adapter registers, such as <c>InMemoryOutboxStore</c>.</param>
    /// <param name="method">The adapter call, for the exception message, such as <c>WithInMemoryStore()</c>.</param>
    /// <remarks>
    /// Registering the same store again passes this check. The adapters register everything with
    /// <c>TryAdd</c>, so the repeat is then a no-op. Registrations of other pipelines are ignored.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A different store is already registered for the pipeline.</exception>
    public static void ThrowIfConflicting(IServiceCollection services, string? pipeline, Type storeType, string method)
    {
        ServiceDescriptor? existing = null;
        ServiceDescriptor? existingConcrete = null;
        foreach (var descriptor in services)
        {
            if (!BelongsTo(descriptor, pipeline))
                continue;
            if (descriptor.ServiceType == typeof(IOutboxStore))
                existing = descriptor;
            else if (IsConcreteStore(descriptor.ServiceType))
                existingConcrete = descriptor;
        }

        if (existing is null)
            return;

        // The adapters register IOutboxStore as a factory that forwards to the concrete store, so a
        // factory registration is described by the concrete store registered next to it.
        var existingType = ImplementationTypeOf(existing) ?? existingConcrete?.ServiceType;
        if (existingType == storeType)
            return;

        throw Conflict(method, pipeline, existingType is null ? "a factory-registered store" : Format(existingType));
    }

    /// <summary>
    /// Throws when a pipeline other than <paramref name="pipeline"/> already uses
    /// <paramref name="storeType"/>, because both would poll the same outbox table.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="pipeline">The pipeline's name, or null for the default pipeline.</param>
    /// <param name="storeType">
    /// The concrete store type, which identifies the table it polls, such as
    /// <c>EfCoreOutboxStore&lt;AppDbContext&gt;</c>.
    /// </param>
    /// <param name="method">The adapter call, for the exception message.</param>
    /// <param name="remedy">How to give the pipeline a store of its own, for the exception message.</param>
    /// <exception cref="InvalidOperationException">Another pipeline already uses the store.</exception>
    public static void ThrowIfSharedWithAnotherPipeline(
        IServiceCollection services, string? pipeline, Type storeType, string method, string remedy)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType != storeType || BelongsTo(descriptor, pipeline))
                continue;

            var other = descriptor.IsKeyedService ? descriptor.ServiceKey as string : null;
            if (descriptor.IsKeyedService && other is null)
                continue;

            throw new InvalidOperationException(
                $"ZeroAlloc.Outbox: {method} cannot register {Format(storeType)} for {Describe(pipeline)}, " +
                $"because {Describe(other)} already uses it. Two pipelines on one store poll the same " +
                "outbox table, so each would claim the other's messages and dispatch them with the " +
                $"wrong options. {remedy}");
        }
    }

    /// <summary>Builds the exception for a second store that differs from the first.</summary>
    public static InvalidOperationException Conflict(string method, string? pipeline, string existingStore)
        => new(
            $"ZeroAlloc.Outbox: {method} cannot register an outbox store for {Describe(pipeline)}, " +
            $"because {existingStore} is already registered as its IOutboxStore. A pipeline has one " +
            "store, and before Outbox 4.0 the second store silently replaced the first. Remove one of " +
            "the two registrations, or run the second store as a pipeline of its own with " +
            "services.AddOutbox(name, configure).");

    /// <summary>
    /// True when <paramref name="descriptor"/> belongs to <paramref name="pipeline"/>: unkeyed for
    /// the default pipeline, keyed by the name for a named one.
    /// </summary>
    private static bool BelongsTo(ServiceDescriptor descriptor, string? pipeline)
        => pipeline is null
            ? !descriptor.IsKeyedService
            : descriptor.IsKeyedService
              && descriptor.ServiceKey is string key
              && string.Equals(key, pipeline, StringComparison.Ordinal);

    private static string Describe(string? pipeline)
        => pipeline is null ? "the default outbox pipeline" : $"the outbox pipeline '{pipeline}'";

    /// <summary>
    /// The implementation type of a type or instance registration. Reads the keyed or unkeyed
    /// properties to match the descriptor, since the other set throws.
    /// </summary>
    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor)
        => descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
            : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();

    private static bool IsConcreteStore(Type type)
        => type is { IsClass: true, IsAbstract: false } && typeof(IOutboxStore).IsAssignableFrom(type);

    private static string Format(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        var sb = new StringBuilder(tick < 0 ? name : name[..tick]).Append('<');
        var arguments = type.GetGenericArguments();
        for (var i = 0; i < arguments.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(Format(arguments[i]));
        }

        return sb.Append('>').ToString();
    }
}
