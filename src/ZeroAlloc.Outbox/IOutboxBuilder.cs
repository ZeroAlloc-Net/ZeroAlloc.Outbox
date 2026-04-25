using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Fluent builder returned by <see cref="OutboxServiceCollectionExtensions.AddOutbox"/>.
/// Exposes the underlying <see cref="IServiceCollection"/> via <see cref="Services"/>;
/// downstream packages add <c>With*</c> extensions on this interface.
/// </summary>
public interface IOutboxBuilder
{
    IServiceCollection Services { get; }
}
