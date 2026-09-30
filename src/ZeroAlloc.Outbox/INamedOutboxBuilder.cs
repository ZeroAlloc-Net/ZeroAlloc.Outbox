namespace ZeroAlloc.Outbox;

/// <summary>
/// Fluent builder for a named outbox pipeline, returned by
/// <see cref="OutboxServiceCollectionExtensions.AddOutbox(Microsoft.Extensions.DependencyInjection.IServiceCollection, string, Action{OutboxOptions})"/>.
/// </summary>
/// <remarks>
/// The store adapters check for this interface: on a named builder, <c>WithEfCore&lt;TContext&gt;()</c>,
/// <c>WithOrm()</c> and <c>WithInMemoryStore()</c> register an <see cref="IOutboxStore"/> keyed by
/// <see cref="Name"/> instead of the default pipeline's store. Every other <c>With*</c> extension
/// registers a service all pipelines share, such as the serializer or the message dispatchers.
/// </remarks>
public interface INamedOutboxBuilder : IOutboxBuilder
{
    /// <summary>
    /// The pipeline's name. It keys the pipeline's <see cref="IOutboxStore"/>, names its
    /// <see cref="OutboxOptions"/>, and tags its worker's telemetry as <c>outbox.pipeline</c>.
    /// </summary>
    string Name { get; }
}
