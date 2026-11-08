using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// A named pipeline on the ORM store, the shape ZeroAlloc.Outbox#206 was raised for: its own
/// worker drains the ORM outbox table with the pipeline's own options.
/// </summary>
public sealed class OrmNamedPipelineTests
{
    private const string TypeName = "OrmNamedPipeline.Run";

    [Fact]
    public async Task A_Named_Orm_Pipeline_Dispatches_Its_Rows()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var connection = await fx.ConnectAsync();
        await using var _ = connection;

        var dispatched = new ConcurrentQueue<byte>();
        var allDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(connection);
        services.AddOutbox("workflow", o =>
        {
            o.BatchSize = 2;
            o.PollingInterval = TimeSpan.FromMilliseconds(20);
            o.LeaseDuration = TimeSpan.FromMinutes(30);
        }).WithOrm(OutboxOrmDialect.Sqlite);
        services.AddSingleton<IOutboxTypeDispatcher>(new RecordingDispatcher(payload =>
        {
            dispatched.Enqueue(payload.Span[0]);
            if (dispatched.Count == 5)
                allDispatched.TrySetResult();
        }));

        await using var sp = services.BuildServiceProvider();
        await using (var scope = sp.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("workflow");
            for (byte i = 0; i < 5; i++)
                await store.EnqueueAsync(TypeName, new[] { i }, transaction: null, CancellationToken.None);
        }

        var worker = sp.GetServices<IHostedService>().OfType<OutboxWorkerService>().Should().ContainSingle().Subject;
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await allDispatched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        dispatched.Order().Should().Equal(new byte[] { 0, 1, 2, 3, 4 }, "each message is dispatched exactly once");
    }

    private static void Pipeline(IServiceCollection services, string name, string table)
        => services.AddOutbox(name, _ => { }).WithOrm(OutboxOrmDialect.Sqlite, o => o.TableName = table);

    [Fact]
    public void Two_pipelines_on_the_container_connection_with_different_tables_register()
    {
        var services = new ServiceCollection();
        Pipeline(services, "a", "OutboxA");

        var act = () => Pipeline(services, "b", "OutboxB");

        act.Should().NotThrow();
    }

    [Fact]
    public void Two_pipelines_on_the_container_connection_with_the_same_table_throw()
    {
        var services = new ServiceCollection();
        Pipeline(services, "a", "OutboxA");

        var act = () => Pipeline(services, "b", "OutboxA");

        act.Should().Throw<InvalidOperationException>().WithMessage("*OutboxA*");
    }

    [Fact]
    public void Reregistering_one_pipeline_with_another_table_throws()
    {
        var services = new ServiceCollection();
        Pipeline(services, "a", "OutboxA");

        var act = () => Pipeline(services, "a", "OutboxB");

        act.Should().Throw<InvalidOperationException>().WithMessage("*OutboxA*OutboxB*");
    }

    [Fact]
    public void Reregistering_with_the_same_table_is_a_noop()
    {
        var services = new ServiceCollection();
        var builder = services.AddOutbox("a", _ => { }).WithOrm(OutboxOrmDialect.Sqlite, o => o.TableName = "OutboxA");
        var count = services.Count;

        builder.WithOrm(OutboxOrmDialect.Sqlite, o => o.TableName = "OutboxA");

        services.Count.Should().Be(count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_table_name_throws_ArgumentException_at_registration(string? table)
    {
        var act = () => new ServiceCollection().AddOutbox().WithOrm(OutboxOrmDialect.Sqlite, o => o.TableName = table!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Tables_differing_only_in_quoting_are_different_tables()
    {
        var services = new ServiceCollection();
        Pipeline(services, "a", "OutboxA");

        var act = () => Pipeline(services, "b", "\"OutboxA\"");

        act.Should().NotThrow();
    }

    private sealed class RecordingDispatcher(Action<ReadOnlyMemory<byte>> action) : IOutboxTypeDispatcher
    {
        public string TypeName => OrmNamedPipelineTests.TypeName;

        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            action(payload);
            return ValueTask.CompletedTask;
        }
    }
}
