using ZeroAlloc.Mediator;

namespace ZeroAlloc.Outbox.Mediator;

/// <summary>
/// Dispatches a deserialized outbox message of type <typeparamref name="T"/> by publishing
/// it to all registered <see cref="INotificationHandler{TNotification}"/> implementations.
/// </summary>
/// <remarks>
/// <typeparamref name="T"/> must implement <see cref="INotification"/>.
/// Register via <see cref="OutboxMediatorServiceCollectionExtensions.WithMediator{T}"/>.
/// </remarks>
public sealed class MediatorOutboxDispatcher<T> : IOutboxDispatcher<T>
    where T : class, INotification
{
    // An array: a foreach over it allocates no enumerator, unlike one over IEnumerable<T>.
    private readonly INotificationHandler<T>[] _handlers;

    public MediatorOutboxDispatcher(IEnumerable<INotificationHandler<T>> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        // The container resolves the sequence as an array; any other sequence is copied once here.
        _handlers = handlers as INotificationHandler<T>[] ?? handlers.ToArray();
    }

    public async ValueTask DispatchAsync(T message, CancellationToken ct)
    {
        foreach (var handler in _handlers)
            await handler.Handle(message, ct).ConfigureAwait(false);
    }
}
