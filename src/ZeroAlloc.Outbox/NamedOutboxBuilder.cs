using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox;

internal sealed class NamedOutboxBuilder(IServiceCollection services, string name) : INamedOutboxBuilder
{
    public IServiceCollection Services { get; } = services;

    public string Name { get; } = name;
}
