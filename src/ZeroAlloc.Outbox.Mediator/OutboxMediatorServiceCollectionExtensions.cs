using System;
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
    ///     .WithMediator&lt;OrderPlacedEvent&gt;()
    ///     .WithInMemoryStore();
    /// </code>
    /// </remarks>
    public static IOutboxBuilder WithMediator<T>(this IOutboxBuilder builder)
        where T : class, INotification
    {
        builder.Services.AddTransient<IOutboxDispatcher<T>, MediatorOutboxDispatcher<T>>();
        return builder;
    }
}
