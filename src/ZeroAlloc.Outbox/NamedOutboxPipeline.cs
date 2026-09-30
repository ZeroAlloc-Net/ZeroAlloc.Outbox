namespace ZeroAlloc.Outbox;

/// <summary>
/// A named outbox pipeline registered with
/// <see cref="OutboxServiceCollectionExtensions.AddOutbox(Microsoft.Extensions.DependencyInjection.IServiceCollection, string, Action{OutboxOptions})"/>.
/// </summary>
/// <remarks>
/// One is registered as a singleton per pipeline name, so resolving
/// <c>IEnumerable&lt;NamedOutboxPipeline&gt;</c> lists the container's named pipelines. The
/// dashboard uses it for its pipeline selector. The default pipeline is not listed.
/// </remarks>
public sealed class NamedOutboxPipeline
{
    internal NamedOutboxPipeline(string name) => Name = name;

    /// <summary>
    /// The pipeline's name: the key of its <see cref="IOutboxStore"/> and the name of its
    /// <see cref="OutboxOptions"/>.
    /// </summary>
    public string Name { get; }
}
