using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Outbox.Mediator;

public static class OutboxMediatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="MediatorOutboxDispatcher{T}"/> that publishes messages of type
    /// <typeparamref name="T"/> to all registered <see cref="INotificationHandler{TNotification}"/>
    /// implementations when the outbox worker dispatches them.
    /// </summary>
    /// <typeparam name="T">
    /// The outbox message type, which must implement <see cref="INotification"/>.
    /// </typeparam>
    /// <remarks>
    /// Call once per message type after <c>AddOutbox()</c>:
    /// <code>
    /// services
    ///     .AddOutbox()
    ///     .AddOutboxMediator&lt;OrderPlacedEvent&gt;()
    ///     .AddOutboxInMemory();
    /// </code>
    /// </remarks>
    public static IServiceCollection AddOutboxMediator<T>(this IServiceCollection services)
        where T : class, INotification
    {
        services.AddTransient<IOutboxDispatcher<T>, MediatorOutboxDispatcher<T>>();
        return services;
    }
}
