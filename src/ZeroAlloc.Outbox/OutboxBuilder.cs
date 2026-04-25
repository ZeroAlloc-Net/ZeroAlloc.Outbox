using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox;

internal sealed class OutboxBuilder(IServiceCollection services) : IOutboxBuilder
{
    public IServiceCollection Services { get; } = services;
}
