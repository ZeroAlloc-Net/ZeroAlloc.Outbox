using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox;

/// <summary>
/// The rule every store adapter applies before it registers <see cref="IOutboxStore"/>: one
/// container hosts one outbox pipeline, so a second, different store throws instead of silently
/// replacing the first.
/// </summary>
/// <remarks>
/// Compiled into each store adapter as a linked source file, so it adds no public API to
/// ZeroAlloc.Outbox. Keyed <see cref="IOutboxStore"/> registrations are ignored; they belong
/// to named pipelines, not to the default one.
/// </remarks>
internal static class OutboxStoreRegistration
{
    private const string NamedPipelinesIssue = "https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/206";

    /// <summary>
    /// Throws when a default <see cref="IOutboxStore"/> other than <paramref name="storeType"/> is
    /// already registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="storeType">The concrete store type the adapter registers, such as <c>InMemoryOutboxStore</c>.</param>
    /// <param name="method">The adapter call, for the exception message, such as <c>WithInMemoryStore()</c>.</param>
    /// <remarks>
    /// Registering the same store again passes this check. The adapters register everything with
    /// <c>TryAdd</c>, so the repeat is then a no-op.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A different default store is already registered.</exception>
    public static void ThrowIfConflicting(IServiceCollection services, Type storeType, string method)
    {
        ServiceDescriptor? existing = null;
        ServiceDescriptor? existingConcrete = null;
        foreach (var descriptor in services)
        {
            // Read IsKeyedService first: the Implementation* properties throw on a keyed descriptor.
            if (descriptor.IsKeyedService)
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
        var existingType = existing.ImplementationType
            ?? existing.ImplementationInstance?.GetType()
            ?? existingConcrete?.ServiceType;
        if (existingType == storeType)
            return;

        throw Conflict(method, existingType is null ? "a factory-registered store" : Format(existingType));
    }

    /// <summary>Builds the exception for a second store that differs from the first.</summary>
    public static InvalidOperationException Conflict(string method, string existingStore)
        => new(
            $"ZeroAlloc.Outbox: {method} cannot register an outbox store, because {existingStore} is " +
            "already registered as IOutboxStore. One container hosts one outbox pipeline, and before " +
            "Outbox 4.0 the second store silently replaced the first for every worker. Remove one of " +
            "the two registrations. Support for more than one pipeline per container is tracked in " +
            NamedPipelinesIssue + ".");

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
