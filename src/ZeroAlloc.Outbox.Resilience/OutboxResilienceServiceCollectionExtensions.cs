using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox.Resilience;

public static class OutboxResilienceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a Resilience-generated proxy <typeparamref name="TResilienceProxy"/> as the
    /// <see cref="IOutboxDispatcher{T}"/> for message type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The outbox message type.</typeparam>
    /// <typeparam name="TDispatcherInterface">
    /// A custom dispatcher interface that extends <see cref="IOutboxDispatcher{T}"/> and carries
    /// Resilience attributes (<c>[Retry]</c>, <c>[CircuitBreaker]</c>, etc.). The Resilience
    /// source generator emits the proxy from this interface.
    /// </typeparam>
    /// <typeparam name="TResilienceProxy">
    /// The source-generated Resilience proxy class for <typeparamref name="TDispatcherInterface"/>.
    /// </typeparam>
    /// <remarks>
    /// Define your dispatcher interface with Resilience attributes, implement it, then wire everything:
    /// <code>
    /// // 1. Define an interface with Resilience attributes:
    /// [Retry(MaxAttempts = 5, BackoffMs = 500)]
    /// public interface IOrderEventDispatcher : IOutboxDispatcher&lt;OrderCreated&gt; { }
    ///
    /// // 2. Implement it:
    /// public sealed class OrderEventDispatcher : IOrderEventDispatcher
    /// {
    ///     public ValueTask DispatchAsync(OrderCreated message, CancellationToken ct) { ... }
    /// }
    ///
    /// // 3. Register in DI (the Resilience generator emits IOrderEventDispatcherResilienceProxy):
    /// services
    ///     .AddOutbox()
    ///     .WithResilience&lt;OrderCreated, IOrderEventDispatcher, IOrderEventDispatcherResilienceProxy&gt;();
    /// </code>
    /// The proxy wraps the inner <typeparamref name="TDispatcherInterface"/> implementation and
    /// is resolved as <see cref="IOutboxDispatcher{T}"/> by the outbox worker.
    /// </remarks>
    public static IOutboxBuilder WithResilience<
        T,
        TDispatcherInterface,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TResilienceProxy>(
        this IOutboxBuilder builder)
        where T : notnull
        where TDispatcherInterface : class, IOutboxDispatcher<T>
        where TResilienceProxy : class, TDispatcherInterface
    {
        builder.Services.AddTransient<TResilienceProxy>();
        builder.Services.AddTransient<IOutboxDispatcher<T>>(sp => sp.GetRequiredService<TResilienceProxy>());
        return builder;
    }
}
