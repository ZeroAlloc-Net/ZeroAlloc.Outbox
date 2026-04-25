using ZeroAlloc.Mediator;

namespace ZeroAlloc.Outbox.Mediator;

/// <summary>
/// Dispatches a deserialized outbox message of type <typeparamref name="T"/> by publishing
/// it to all registered <see cref="INotificationHandler{TNotification}"/> implementations.
/// </summary>
/// <remarks>
/// <typeparamref name="T"/> must implement <see cref="INotification"/>.
/// Register via <see cref="OutboxMediatorServiceCollectionExtensions.AddOutboxMediator{T}"/>.
/// </remarks>
public sealed class MediatorOutboxDispatcher<T> : IOutboxDispatcher<T>
    where T : class, INotification
{
    private readonly IEnumerable<INotificationHandler<T>> _handlers;

    public MediatorOutboxDispatcher(IEnumerable<INotificationHandler<T>> handlers)
        => _handlers = handlers;

    public async ValueTask DispatchAsync(T message, CancellationToken ct)
    {
        foreach (var handler in _handlers)
            await handler.Handle(message, ct).ConfigureAwait(false);
    }
}
