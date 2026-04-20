namespace ZeroAlloc.Outbox;

/// <summary>
/// Dispatches a deserialized outbox message of type <typeparamref name="T"/>.
/// Register a custom implementation to control delivery (e.g. call a mediator, HTTP client, etc.).
/// </summary>
public interface IOutboxDispatcher<T> where T : notnull
{
    ValueTask DispatchAsync(T message, CancellationToken ct);
}
