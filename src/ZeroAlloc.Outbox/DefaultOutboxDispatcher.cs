namespace ZeroAlloc.Outbox;

/// <summary>
/// Fallback dispatcher registered when no custom <see cref="IOutboxDispatcher{T}"/> is found.
/// Always throws to surface the misconfiguration at dispatch time.
/// </summary>
public sealed class DefaultOutboxDispatcher<T> : IOutboxDispatcher<T> where T : notnull
{
    public ValueTask DispatchAsync(T message, CancellationToken ct)
        => throw new InvalidOperationException(
            $"No IOutboxDispatcher<{typeof(T).Name}> is registered. " +
            "Register a custom dispatcher or install ZeroAlloc.Outbox.Mediator.");
}
