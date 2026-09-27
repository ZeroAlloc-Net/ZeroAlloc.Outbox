using System.Data.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// <para>
    /// Calling this again with the same dialect is a no-op.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IOutboxStore"/> is already registered, such as another store adapter
    /// or the ORM store with another dialect. One container hosts one outbox pipeline.
    /// </exception>
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

        var services = builder.Services;
        OutboxStoreRegistration.ThrowIfConflicting(services, typeof(OrmOutboxStore), "WithOrm()");

        // The same store with another dialect conflicts too; TryAdd would silently keep the first.
        var existing = services.LastOrDefault(d => !d.IsKeyedService && d.ServiceType == typeof(OrmOutboxStore));
        if (existing?.ImplementationFactory?.Target is OrmOutboxStoreFactory registered
            && registered.Dialect != dialect)
        {
            throw OutboxStoreRegistration.Conflict(
                "WithOrm()", $"OrmOutboxStore with dialect {registered.Dialect}, not {dialect},");
        }

        services.TryAddScoped(new OrmOutboxStoreFactory(dialect).Create);
        services.TryAddScoped<IOutboxStore>(sp => sp.GetRequiredService<OrmOutboxStore>());
        return builder;
    }

    /// <summary>
    /// Creates the store with the dialect it was registered with. A named class rather than a
    /// lambda, so a later <c>WithOrm</c> call can read the dialect back from the registration.
    /// </summary>
    private sealed class OrmOutboxStoreFactory(OutboxOrmDialect dialect)
    {
        public OutboxOrmDialect Dialect { get; } = dialect;

        public OrmOutboxStore Create(IServiceProvider sp)
            => new(sp.GetRequiredService<IAsyncDbConnection>(), Dialect);
    }
}
