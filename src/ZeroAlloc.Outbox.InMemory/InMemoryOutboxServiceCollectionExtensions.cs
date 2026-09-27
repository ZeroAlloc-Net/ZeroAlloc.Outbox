using System;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox.InMemory;

public static class InMemoryOutboxServiceCollectionExtensions
{
    /// <summary>Registers the in-memory outbox store (for testing only).</summary>
    public static IOutboxBuilder WithInMemoryStore(this IOutboxBuilder builder)
    {
        builder.Services.AddSingleton<InMemoryOutboxStore>();
        builder.Services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
        builder.Services.AddSingleton<IOutboxDashboardStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
        return builder;
    }
}
