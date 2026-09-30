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
    /// <para>
    /// On a named pipeline's builder, from <c>AddOutbox(name, configure)</c>, this registers the
    /// store as an <see cref="OrmOutboxStore"/> and an <see cref="IOutboxStore"/> keyed by the
    /// pipeline's name. The store still uses the container's unkeyed
    /// <see cref="IAsyncDbConnection"/>, so only one pipeline in a container can use the ORM store.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IOutboxStore"/> is already registered for the same pipeline, such as
    /// another store adapter or the ORM store with another dialect. Or another pipeline already
    /// uses the ORM store, and would poll the same table.
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
        var pipeline = OutboxStoreRegistration.PipelineOf(builder);
        OutboxStoreRegistration.ThrowIfConflicting(services, pipeline, typeof(OrmOutboxStore), "WithOrm()");

        // The same store with another dialect conflicts too; TryAdd would silently keep the first.
        var existing = OutboxStoreRegistration.Find(services, pipeline, typeof(OrmOutboxStore));
        if (existing is not null
            && OutboxStoreRegistration.FactoryTarget(existing) is OrmOutboxStoreFactory registered
            && registered.Dialect != dialect)
        {
            throw OutboxStoreRegistration.Conflict(
                "WithOrm()", pipeline, $"OrmOutboxStore with dialect {registered.Dialect}, not {dialect},");
        }

        OutboxStoreRegistration.ThrowIfSharedWithAnotherPipeline(
            services, pipeline, typeof(OrmOutboxStore), "WithOrm()",
            "The ORM store uses the container's IAsyncDbConnection, so only one pipeline can use it. " +
            "Use another store adapter for the other pipeline, such as WithEfCore<TContext>().");

        var factory = new OrmOutboxStoreFactory(dialect);
        if (pipeline is null)
        {
            services.TryAddScoped(factory.Create);
            services.TryAddScoped<IOutboxStore>(sp => sp.GetRequiredService<OrmOutboxStore>());
        }
        else
        {
            services.TryAddKeyedScoped(pipeline, factory.CreateKeyed);
            services.TryAddKeyedScoped<IOutboxStore>(
                pipeline, static (sp, key) => sp.GetRequiredKeyedService<OrmOutboxStore>(key));
        }

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

        public OrmOutboxStore CreateKeyed(IServiceProvider sp, object? key)
            => Create(sp);
    }
}
