using System.Collections.Concurrent;
using System.Data.Async;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// A named pipeline's ORM store on a connection of its own, ZeroAlloc.Outbox#256: a keyed
/// <see cref="IAsyncDbConnection"/> or a connection factory, so ORM pipelines can target different
/// databases. The default pipeline keeps the container's connection.
/// </summary>
public sealed class OrmPipelineConnectionTests
{
    private const string TypeName = "OrmPipelineConnection.Run";

    [Fact]
    public async Task Three_Orm_Pipelines_On_Three_Databases_Each_Write_And_Dispatch_Their_Own_Rows()
    {
        await using var defaultDb = new SqliteFixture();
        await using var keyedDb = new SqliteFixture();
        await using var factoryDb = new SqliteFixture();
        await Task.WhenAll(defaultDb.MigrateAsync(), keyedDb.MigrateAsync(), factoryDb.MigrateAsync());
        await using var defaultConnection = await defaultDb.ConnectAsync();
        await using var keyedConnection = await keyedDb.ConnectAsync();
        await using var factoryConnection = await factoryDb.ConnectAsync();

        var dispatched = new ConcurrentQueue<byte>();
        var allDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(defaultConnection);
        services.AddKeyedSingleton("workflow-db", keyedConnection);
        services.AddOutbox(Fast).WithOrm(OutboxOrmDialect.Sqlite);
        services.AddOutbox("keyed", Fast).WithOrm(OutboxOrmDialect.Sqlite, connectionKey: "workflow-db");
        services.AddOutbox("factory", Fast).WithOrm(OutboxOrmDialect.Sqlite, _ => factoryConnection);
        services.AddSingleton<IOutboxTypeDispatcher>(new RecordingDispatcher(payload =>
        {
            dispatched.Enqueue(payload.Span[0]);
            if (dispatched.Count == 3)
                allDispatched.TrySetResult();
        }));

        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOutboxStore>().EnqueueAsync(TypeName, new byte[] { 0 }, null, default);
            await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("keyed").EnqueueAsync(TypeName, new byte[] { 1 }, null, default);
            await scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("factory").EnqueueAsync(TypeName, new byte[] { 2 }, null, default);
        }

        (await defaultDb.CountAsync()).Should().Be(1);
        (await keyedDb.CountAsync()).Should().Be(1);
        (await factoryDb.CountAsync()).Should().Be(1);

        var workers = sp.GetServices<IHostedService>().OfType<OutboxWorkerService>().ToList();
        workers.Should().HaveCount(3);
        await Task.WhenAll(workers.Select(w => w.StartAsync(CancellationToken.None)));
        try
        {
            await allDispatched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await Task.WhenAll(workers.Select(w => w.StopAsync(CancellationToken.None)));
        }

        dispatched.Order().Should().Equal(new byte[] { 0, 1, 2 }, "each message is dispatched exactly once");

        static void Fast(OutboxOptions o) => o.PollingInterval = TimeSpan.FromMilliseconds(20);
    }

    [Fact]
    public void Two_Pipelines_On_Different_Keyed_Connections_Do_Not_Conflict()
    {
        var services = new ServiceCollection();

        var act = () =>
        {
            services.AddOutbox().WithOrm(OutboxOrmDialect.Postgres);
            services.AddOutbox("a", _ => { }).WithOrm(OutboxOrmDialect.Postgres, connectionKey: "db-a");
            services.AddOutbox("b", _ => { }).WithOrm(OutboxOrmDialect.Postgres, connectionKey: "db-b");
            services.AddOutbox("c", _ => { }).WithOrm(OutboxOrmDialect.Postgres, _ => throw new NotSupportedException());
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void Two_Pipelines_On_The_Same_Keyed_Connection_Throw()
    {
        var services = new ServiceCollection();
        services.AddOutbox("a", _ => { }).WithOrm(OutboxOrmDialect.Postgres, connectionKey: "db");

        var act = () => services.AddOutbox("b", _ => { }).WithOrm(OutboxOrmDialect.Postgres, connectionKey: "db");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*pipeline 'b'*pipeline 'a' already uses it*IAsyncDbConnection keyed 'db'*");
    }

    [Fact]
    public void A_Named_Pipeline_On_The_Containers_Connection_Next_To_The_Default_Still_Throws()
    {
        var services = new ServiceCollection();
        services.AddOutbox().WithOrm(OutboxOrmDialect.Postgres);

        var act = () => services.AddOutbox("a", _ => { }).WithOrm(OutboxOrmDialect.Postgres);

        act.Should().Throw<InvalidOperationException>().WithMessage("*container's IAsyncDbConnection*");
    }

    [Fact]
    public void The_Same_Keyed_Connection_Twice_On_One_Pipeline_Keeps_One_Store()
    {
        var services = new ServiceCollection();

        services.AddOutbox("a", _ => { }).WithOrm(OutboxOrmDialect.Postgres, connectionKey: "db");
        services.AddOutbox("a", _ => { }).WithOrm(OutboxOrmDialect.Postgres, connectionKey: "db");

        services.Count(d => d.ServiceType == typeof(OrmOutboxStore)).Should().Be(1);
    }

    [Fact]
    public void Another_Connection_For_The_Same_Pipeline_Throws()
    {
        var services = new ServiceCollection();
        services.AddOutbox("a", _ => { }).WithOrm(OutboxOrmDialect.Postgres, connectionKey: "db-1");

        var act = () => services.AddOutbox("a", _ => { }).WithOrm(OutboxOrmDialect.Postgres, connectionKey: "db-2");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*pipeline 'a'*keyed 'db-1', not on the IAsyncDbConnection keyed 'db-2'*");
    }

    [Fact]
    public async Task A_Keyed_Connection_Is_Resolved_In_The_Stores_Scope()
    {
        await using var fx = new SqliteFixture();
        var services = new ServiceCollection();
        var opened = 0;
        services.AddKeyedScoped<IAsyncDbConnection>("db", (_, _) =>
        {
            Interlocked.Increment(ref opened);
            return fx.ConnectAsync().GetAwaiter().GetResult();
        });
        services.AddOutbox("a", _ => { }).WithOrm(OutboxOrmDialect.Sqlite, connectionKey: "db");

        await using var sp = services.BuildServiceProvider();
        for (var i = 0; i < 2; i++)
        {
            await using var scope = sp.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("a").Should().BeOfType<OrmOutboxStore>();
        }

        opened.Should().Be(2, "the scoped keyed connection is resolved, and disposed, per scope");
    }

    [Fact]
    public void A_Null_Connection_Key_Or_Factory_Throws()
    {
        var builder = new ServiceCollection().AddOutbox("a", _ => { });

        ((Action)(() => builder.WithOrm(OutboxOrmDialect.Sqlite, connectionKey: null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => builder.WithOrm(OutboxOrmDialect.Sqlite, (Func<IServiceProvider, IAsyncDbConnection>)null!)))
            .Should().Throw<ArgumentNullException>();
    }

    private sealed class RecordingDispatcher(Action<ReadOnlyMemory<byte>> action) : IOutboxTypeDispatcher
    {
        public string TypeName => OrmPipelineConnectionTests.TypeName;

        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            action(payload);
            return ValueTask.CompletedTask;
        }
    }
}
