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
    /// <see cref="IAsyncDbConnection"/>, so each pipeline needs a table of its own there, set with
    /// <see cref="WithOrm(IOutboxBuilder, OutboxOrmDialect, Action{OrmOutboxOptions})"/>. To point a named
    /// pipeline at a database of its own, use
    /// <see cref="WithOrm(INamedOutboxBuilder, OutboxOrmDialect, object)"/> or
    /// <see cref="WithOrm(INamedOutboxBuilder, OutboxOrmDialect, Func{IServiceProvider, IAsyncDbConnection})"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IOutboxStore"/> is already registered for the same pipeline, such as
    /// another store adapter or the ORM store with another dialect, connection or table. Or another
    /// pipeline already uses the ORM store on the container's connection and the same table, and
    /// would poll it too.
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

        Register(builder, dialect, OrmConnectionSource.Container, DefaultTableName);
        return builder;
    }

    /// <summary>
    /// Persists a named pipeline's outbox messages through ZeroAlloc.ORM, against the
    /// <see cref="IAsyncDbConnection"/> registered in the container under
    /// <paramref name="connectionKey"/>.
    /// </summary>
    /// <param name="builder">The named pipeline's builder, from <c>AddOutbox(name, configure)</c>.</param>
    /// <param name="dialect">Which database the keyed connection talks to.</param>
    /// <param name="connectionKey">
    /// The service key of the <see cref="IAsyncDbConnection"/> to use, such as
    /// <c>services.AddKeyedScoped&lt;IAsyncDbConnection&gt;("workflow-db", ...)</c>.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Lets pipelines target different databases: each pipeline's store resolves its own keyed
    /// connection, in the worker's scope. Register the keyed connection yourself; its lifetime,
    /// provider and connection string belong to the application. The default pipeline's
    /// <see cref="WithOrm(IOutboxBuilder, OutboxOrmDialect)"/> is unchanged.
    /// </para>
    /// <para>
    /// Calling this again with the same dialect and key is a no-op.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IOutboxStore"/> is already registered for the pipeline, including the
    /// ORM store with another dialect, connection or table. Or another pipeline already uses the ORM
    /// store on the same keyed connection and the same table, and would poll it too.
    /// </exception>
    /// <example>
    /// <code>
    /// services.AddKeyedScoped&lt;IAsyncDbConnection&gt;("workflow-db", (sp, _) => OpenWorkflowDb());
    /// services.AddOutbox("workflow", o => o.LeaseDuration = TimeSpan.FromMinutes(30))
    ///         .WithOrm(OutboxOrmDialect.Postgres, connectionKey: "workflow-db");
    /// </code>
    /// </example>
    public static IOutboxBuilder WithOrm(
        this INamedOutboxBuilder builder,
        OutboxOrmDialect dialect,
        object connectionKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionKey);

        Register(builder, dialect, OrmConnectionSource.Keyed(connectionKey), DefaultTableName);
        return builder;
    }

    /// <summary>
    /// Persists a named pipeline's outbox messages through ZeroAlloc.ORM, against the
    /// <see cref="IAsyncDbConnection"/> <paramref name="connection"/> returns.
    /// </summary>
    /// <param name="builder">The named pipeline's builder, from <c>AddOutbox(name, configure)</c>.</param>
    /// <param name="dialect">Which database the connection talks to.</param>
    /// <param name="connection">
    /// Returns the connection for one scope of the pipeline's worker, or of whoever resolves the
    /// pipeline's store. Called each time the scoped store is created.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The outbox does not dispose the connection <paramref name="connection"/> returns, since it
    /// does not own it. Return a connection whose lifetime the application manages, such as one
    /// resolved from <see cref="IServiceProvider"/>. To have the container own it, register it
    /// keyed and use <see cref="WithOrm(INamedOutboxBuilder, OutboxOrmDialect, object)"/>.
    /// </para>
    /// <para>
    /// Calling this again with the same dialect and the same delegate instance is a no-op. Two
    /// pipelines registered with different delegates are assumed to use different databases.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IOutboxStore"/> is already registered for the pipeline, including the
    /// ORM store with another dialect, connection or table.
    /// </exception>
    public static IOutboxBuilder WithOrm(
        this INamedOutboxBuilder builder,
        OutboxOrmDialect dialect,
        Func<IServiceProvider, IAsyncDbConnection> connection)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connection);

        Register(builder, dialect, OrmConnectionSource.FromFactory(connection), DefaultTableName);
        return builder;
    }

    /// <summary>
    /// Persists outbox messages through ZeroAlloc.ORM, against the container's
    /// <see cref="IAsyncDbConnection"/>, in the table <paramref name="configure"/> sets.
    /// </summary>
    /// <param name="builder">The outbox builder.</param>
    /// <param name="dialect">Which database the registered connection talks to.</param>
    /// <param name="configure">Sets the <see cref="OrmOutboxOptions"/>, such as the table name.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// Two pipelines can share the container's connection when they use different tables. The table
    /// name is compared with ordinal string equality; see <see cref="OrmOutboxOptions.TableName"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><see cref="OrmOutboxOptions.TableName"/> is null or blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IOutboxStore"/> is already registered for the pipeline, including the
    /// ORM store with another dialect, connection or table. Or another pipeline already uses the ORM
    /// store on the container's connection and the same table.
    /// </exception>
    /// <example>
    /// <code>
    /// services.AddOutbox("workflow", o => { }).WithOrm(OutboxOrmDialect.Postgres, o => o.TableName = "WorkflowOutbox");
    /// </code>
    /// </example>
    public static IOutboxBuilder WithOrm(
        this IOutboxBuilder builder,
        OutboxOrmDialect dialect,
        Action<OrmOutboxOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Register(builder, dialect, OrmConnectionSource.Container, TableNameFrom(configure));
        return builder;
    }

    /// <summary>
    /// Persists a named pipeline's outbox messages through ZeroAlloc.ORM, against the
    /// <see cref="IAsyncDbConnection"/> registered under <paramref name="connectionKey"/>, in the
    /// table <paramref name="configure"/> sets.
    /// </summary>
    /// <param name="builder">The named pipeline's builder, from <c>AddOutbox(name, configure)</c>.</param>
    /// <param name="dialect">Which database the keyed connection talks to.</param>
    /// <param name="connectionKey">The service key of the <see cref="IAsyncDbConnection"/> to use.</param>
    /// <param name="configure">Sets the <see cref="OrmOutboxOptions"/>, such as the table name.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><see cref="OrmOutboxOptions.TableName"/> is null or blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IOutboxStore"/> is already registered for the pipeline, including the
    /// ORM store with another dialect, connection or table. Or another pipeline already uses the ORM
    /// store on the same keyed connection and the same table.
    /// </exception>
    public static IOutboxBuilder WithOrm(
        this INamedOutboxBuilder builder,
        OutboxOrmDialect dialect,
        object connectionKey,
        Action<OrmOutboxOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionKey);

        Register(builder, dialect, OrmConnectionSource.Keyed(connectionKey), TableNameFrom(configure));
        return builder;
    }

    /// <summary>
    /// Persists a named pipeline's outbox messages through ZeroAlloc.ORM, against the
    /// <see cref="IAsyncDbConnection"/> <paramref name="connection"/> returns, in the table
    /// <paramref name="configure"/> sets.
    /// </summary>
    /// <param name="builder">The named pipeline's builder, from <c>AddOutbox(name, configure)</c>.</param>
    /// <param name="dialect">Which database the connection talks to.</param>
    /// <param name="connection">
    /// Returns the connection for one scope. The outbox does not dispose it; see
    /// <see cref="WithOrm(INamedOutboxBuilder, OutboxOrmDialect, Func{IServiceProvider, IAsyncDbConnection})"/>.
    /// </param>
    /// <param name="configure">Sets the <see cref="OrmOutboxOptions"/>, such as the table name.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><see cref="OrmOutboxOptions.TableName"/> is null or blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// A different <see cref="IOutboxStore"/> is already registered for the pipeline, including the
    /// ORM store with another dialect, connection or table.
    /// </exception>
    public static IOutboxBuilder WithOrm(
        this INamedOutboxBuilder builder,
        OutboxOrmDialect dialect,
        Func<IServiceProvider, IAsyncDbConnection> connection,
        Action<OrmOutboxOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connection);

        Register(builder, dialect, OrmConnectionSource.FromFactory(connection), TableNameFrom(configure));
        return builder;
    }

    private const string DefaultTableName = "OutboxMessages";

    private static string TableNameFrom(Action<OrmOutboxOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OrmOutboxOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.TableName))
        {
            throw new ArgumentException(
                "ZeroAlloc.Outbox: OrmOutboxOptions.TableName must not be null or blank.", nameof(configure));
        }

        return options.TableName;
    }

    private static void Register(
        IOutboxBuilder builder, OutboxOrmDialect dialect, OrmConnectionSource connection, string tableName)
    {
        var services = builder.Services;
        var pipeline = OutboxStoreRegistration.PipelineOf(builder);
        OutboxStoreRegistration.ThrowIfConflicting(services, pipeline, typeof(OrmOutboxStore), "WithOrm()");

        // The same store with another dialect or connection conflicts too; TryAdd would silently
        // keep the first.
        var existing = OutboxStoreRegistration.Find(services, pipeline, typeof(OrmOutboxStore));
        if (existing is not null
            && OutboxStoreRegistration.FactoryTarget(existing) is OrmOutboxStoreFactory registered)
        {
            if (registered.Dialect != dialect)
            {
                throw OutboxStoreRegistration.Conflict(
                    "WithOrm()", pipeline, $"OrmOutboxStore with dialect {registered.Dialect}, not {dialect},");
            }

            if (!registered.Connection.Equals(connection))
            {
                throw OutboxStoreRegistration.Conflict(
                    "WithOrm()", pipeline,
                    $"OrmOutboxStore on {registered.Connection}, not on {connection},");
            }

            if (!string.Equals(registered.TableName, tableName, StringComparison.Ordinal))
            {
                throw OutboxStoreRegistration.Conflict(
                    "WithOrm()", pipeline,
                    $"OrmOutboxStore on table {registered.TableName}, not on table {tableName},");
            }
        }

        OutboxStoreRegistration.ThrowIfSharedWithAnotherPipeline(
            services, pipeline, typeof(OrmOutboxStore), "WithOrm()",
            $"Both use {connection} and the table {tableName}. Give each pipeline a table of its own with " +
            "WithOrm(dialect, configure), or a connection of its own with WithOrm(dialect, connectionKey) " +
            "or WithOrm(dialect, connection) on its named builder.",
            other => OutboxStoreRegistration.FactoryTarget(other) is not OrmOutboxStoreFactory otherFactory
                     || (otherFactory.Connection.Equals(connection)
                         && string.Equals(otherFactory.TableName, tableName, StringComparison.Ordinal)));

        var factory = new OrmOutboxStoreFactory(dialect, connection, tableName);
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
    }

    /// <summary>
    /// Creates the store with the dialect, connection and table it was registered with. A named class
    /// rather than a lambda, so a later <c>WithOrm</c> call can read them back from the registration.
    /// </summary>
    private sealed class OrmOutboxStoreFactory(
        OutboxOrmDialect dialect, OrmConnectionSource connection, string tableName)
    {
        public OutboxOrmDialect Dialect { get; } = dialect;

        public OrmConnectionSource Connection { get; } = connection;

        public string TableName { get; } = tableName;

        public OrmOutboxStore Create(IServiceProvider sp)
            => new(Connection.Resolve(sp), Dialect, TableName);

        public OrmOutboxStore CreateKeyed(IServiceProvider sp, object? key)
            => Create(sp);
    }

    /// <summary>
    /// Where a store gets its <see cref="IAsyncDbConnection"/>: the container's unkeyed one, a
    /// keyed one, or a factory. Two sources are equal when they reach the same connection.
    /// </summary>
    private sealed class OrmConnectionSource : IEquatable<OrmConnectionSource>
    {
        public static readonly OrmConnectionSource Container = new(key: null, factory: null);

        private readonly object? _key;
        private readonly Func<IServiceProvider, IAsyncDbConnection>? _factory;

        private OrmConnectionSource(object? key, Func<IServiceProvider, IAsyncDbConnection>? factory)
        {
            _key = key;
            _factory = factory;
        }

        public static OrmConnectionSource Keyed(object key) => new(key, factory: null);

        public static OrmConnectionSource FromFactory(Func<IServiceProvider, IAsyncDbConnection> factory)
            => new(key: null, factory);

        public IAsyncDbConnection Resolve(IServiceProvider sp)
        {
            if (_factory is not null)
            {
                return _factory(sp) ?? throw new InvalidOperationException(
                    "ZeroAlloc.Outbox: the connection factory passed to WithOrm returned null.");
            }

            return _key is null
                ? sp.GetRequiredService<IAsyncDbConnection>()
                : sp.GetRequiredKeyedService<IAsyncDbConnection>(_key);
        }

        public bool Equals(OrmConnectionSource? other)
            => other is not null
               && Equals(_key, other._key)
               && ReferenceEquals(_factory, other._factory);

        public override bool Equals(object? obj) => Equals(obj as OrmConnectionSource);

        public override int GetHashCode() => HashCode.Combine(_key, _factory);

        public override string ToString()
            => _factory is not null ? "a connection factory"
                : _key is null ? "the container's IAsyncDbConnection"
                : $"the IAsyncDbConnection keyed '{_key}'";
    }
}
