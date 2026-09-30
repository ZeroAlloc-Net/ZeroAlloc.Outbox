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
