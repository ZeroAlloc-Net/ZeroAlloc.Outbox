using System.Data.Async;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// Registers ZeroAlloc.ORM as the outbox store.
/// </summary>
public static class OrmOutboxServiceCollectionExtensions
{
    /// <summary>
    /// Persists outbox messages through ZeroAlloc.ORM, against the
    /// <see cref="IAsyncDbConnection"/> already registered in the container.
    /// </summary>
    /// <param name="builder">The outbox builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Register a scoped <see cref="IAsyncDbConnection"/> yourself — this method
    /// deliberately does not, because the connection's lifetime, provider and
    /// connection string belong to the application, not to the outbox.
    /// </para>
    /// <para>
    /// Create the schema with <see cref="OutboxOrmMigrations"/> and the ORM's
    /// <c>MigrationRunner</c>; the store does not create tables on the fly.
    /// </para>
    /// <para>
    /// No <c>IOutboxDashboardStore</c> is registered. That interface is a much
    /// larger surface this adapter does not implement, and registering a
    /// non-functional one would leave the dashboard silently empty rather than
    /// failing where the mistake was made.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddOutbox().WithOrm();
    /// </code>
    /// </example>
    public static IOutboxBuilder WithOrm(this IOutboxBuilder builder)
        => builder.WithOrm(OutboxOrmDialect.Sqlite);

    /// <inheritdoc cref="WithOrm(IOutboxBuilder)" />
    /// <param name="builder">The outbox builder.</param>
    /// <param name="dialect">
    /// Which database the registered connection talks to. Only the batch claim
    /// differs between providers; everything else is plain ANSI.
    /// </param>
    public static IOutboxBuilder WithOrm(
        this IOutboxBuilder builder,
        OutboxOrmDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddScoped(sp =>
            new OrmOutboxStore(sp.GetRequiredService<IAsyncDbConnection>(), dialect));
        builder.Services.AddScoped<IOutboxStore>(sp => sp.GetRequiredService<OrmOutboxStore>());
        return builder;
    }
}
