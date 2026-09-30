using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// Named outbox pipelines, ZeroAlloc.Outbox#206: each <c>AddOutbox(name, configure)</c> runs a
/// worker of its own that polls the store keyed by its name with the options of that name, next
/// to the unchanged default pipeline.
/// </summary>
public sealed class NamedPipelineTests
{
    private const int PerPipeline = 5;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Each_Pipeline_Worker_Claims_Only_From_Its_Own_Store_With_Its_Own_Options()
    {
        var defaultStore = new RecordingStore();
        var storeA = new RecordingStore();
        var storeB = new RecordingStore();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IOutboxStore>(defaultStore);
                services.AddKeyedSingleton<IOutboxStore>("a", storeA);
                services.AddKeyedSingleton<IOutboxStore>("b", storeB);
                services.AddOutbox(o => Configure(o, batchSize: 11, "host-default"));
                services.AddOutbox("a", o => Configure(o, batchSize: 3, "host-a"));
                services.AddOutbox("b", o => Configure(o, batchSize: 7, "host-b"));
            })
            .Build();

        await host.StartAsync();
        try
        {
            await Task.WhenAll(
                defaultStore.FirstClaim.WaitAsync(s_timeout),
                storeA.FirstClaim.WaitAsync(s_timeout),
                storeB.FirstClaim.WaitAsync(s_timeout));
        }
        finally
        {
            await host.StopAsync();
        }

        defaultStore.Claims.Should().OnlyContain(c => c.BatchSize == 11 && c.HostId == "host-default");
        storeA.Claims.Should().OnlyContain(c => c.BatchSize == 3 && c.HostId == "host-a");
        storeB.Claims.Should().OnlyContain(c => c.BatchSize == 7 && c.HostId == "host-b");

        static void Configure(OutboxOptions o, int batchSize, string hostId)
        {
            o.BatchSize = batchSize;
            o.HostId = hostId;
            o.PollingInterval = TimeSpan.FromMilliseconds(20);
        }
    }

    [Fact]
    public async Task Two_Pipelines_With_Different_Stores_Each_Dispatch_Only_Their_Own_Rows()
    {
        const string typeName = "NamedPipelines.Ping";
        using var dispatched = new DispatchRecorder(typeName);
        var allDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddDbContext<DashboardTestDbContext>(o => o.UseSqlite(connection));
                services.AddOutbox("channel", o =>
                {
                    o.BatchSize = 2;
                    o.PollingInterval = TimeSpan.FromMilliseconds(20);
                }).WithEfCore<DashboardTestDbContext>();
                services.AddOutbox("workflow", o =>
                {
                    o.BatchSize = 5;
                    o.PollingInterval = TimeSpan.FromMilliseconds(30);
                    o.LeaseDuration = TimeSpan.FromMinutes(30);
                }).WithInMemoryStore();
                services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(typeName, payload =>
                {
                    if (dispatched.Record(payload) == 2 * PerPipeline)
                        allDispatched.TrySetResult();
                }));
            })
            .Build();

        var sent = new List<string>();
        var scope = host.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await scope.ServiceProvider.GetRequiredService<DashboardTestDbContext>().Database.EnsureCreatedAsync();
            sent.AddRange(await EnqueueAsync(scope.ServiceProvider, "channel", typeName, PerPipeline));
            sent.AddRange(await EnqueueAsync(scope.ServiceProvider, "workflow", typeName, PerPipeline));
        }

        await host.StartAsync();
        try
        {
            await allDispatched.Task.WaitAsync(s_timeout);
        }
        finally
        {
            await host.StopAsync();
        }

        dispatched.Dispatches.Select(d => d.Payload).Should().BeEquivalentTo(
            sent, "every message is dispatched exactly once");
        dispatched.Dispatches.Should().OnlyContain(
            d => d.Payload.StartsWith(d.Pipeline + ":", StringComparison.Ordinal),
            "each pipeline's worker dispatches only its own store's rows, tagged with its name");
        dispatched.AttemptPipelines.Should().BeEquivalentTo(
            Enumerable.Repeat("channel", PerPipeline).Concat(Enumerable.Repeat("workflow", PerPipeline)),
            "each worker tags its outbox.dispatch_attempts measurements with its pipeline");
        host.Services.GetService<IOutboxStore>().Should().BeNull("no default pipeline was registered");
    }

    /// <summary>
    /// Enqueues <paramref name="count"/> messages into the store of <paramref name="pipeline"/>,
    /// each with a payload that names the pipeline.
    /// </summary>
    private static async Task<List<string>> EnqueueAsync(
        IServiceProvider services, string pipeline, string typeName, int count)
    {
        var store = services.GetRequiredKeyedService<IOutboxStore>(pipeline);
        var sent = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var payload = $"{pipeline}:{i}";
            sent.Add(payload);
            await store.EnqueueAsync(typeName, Encoding.UTF8.GetBytes(payload), transaction: null, CancellationToken.None)
                .ConfigureAwait(false);
        }

        return sent;
    }

    [Fact]
    public async Task The_Default_Pipeline_Next_To_A_Named_One_Is_Unchanged()
    {
        const string typeName = "NamedPipelines.Default";
        using var dispatched = new DispatchRecorder(typeName);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(20)).WithInMemoryStore();
                services.AddOutbox("other", o => o.PollingInterval = TimeSpan.FromMilliseconds(20)).WithInMemoryStore();
                services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(typeName, payload =>
                {
                    dispatched.Record(payload);
                    delivered.TrySetResult();
                }));
            })
            .Build();

        var defaultStore = host.Services.GetRequiredService<InMemoryOutboxStore>();
        host.Services.GetRequiredService<IOutboxStore>().Should().BeSameAs(defaultStore);
        host.Services.GetRequiredService<IOutboxDashboardStore>().Should().BeSameAs(defaultStore);
        host.Services.GetRequiredKeyedService<InMemoryOutboxStore>("other").Should().NotBeSameAs(defaultStore);

        await defaultStore.EnqueueAsync(typeName, new byte[] { 1 }, null, CancellationToken.None);
        await host.StartAsync();
        try
        {
            await delivered.Task.WaitAsync(s_timeout);
        }
        finally
        {
            await host.StopAsync();
        }

        dispatched.Dispatches.Should().ContainSingle().Which.Pipeline.Should().BeNull(
            "the default pipeline's activity carries no pipeline tag, as before");
        dispatched.AttemptPipelines.Should().Equal(new string?[] { null },
            "the default pipeline's metrics carry no pipeline tag, as before");
        host.Services.GetRequiredKeyedService<InMemoryOutboxStore>("other").AllEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task A_Named_Pipeline_Worker_Publishes_Dashboard_Events_To_Its_Own_Publisher()
    {
        const string typeName = "NamedPipelines.Events";
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutbox().WithInMemoryStore().WithDashboardEvents();
                services.AddOutbox("workflow", o => o.PollingInterval = TimeSpan.FromMilliseconds(20))
                    .WithInMemoryStore()
                    .WithDashboardEvents();
                services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(typeName, _ => { }));
            })
            .Build();

        var workflowPublisher = host.Services.GetRequiredKeyedService<IOutboxDashboardEventPublisher>("workflow");
        using var workflowEvents = workflowPublisher.Subscribe();
        using var defaultEvents = host.Services.GetRequiredService<IOutboxDashboardEventPublisher>().Subscribe();
        await host.Services.GetRequiredKeyedService<IOutboxStore>("workflow")
            .EnqueueAsync(typeName, new byte[] { 1 }, null, CancellationToken.None);

        await host.StartAsync();
        try
        {
            var evt = await workflowEvents.Reader.ReadAsync().AsTask().WaitAsync(s_timeout);
            evt.Should().BeOfType<MessageDispatchedEvent>();
        }
        finally
        {
            await host.StopAsync();
        }

        defaultEvents.Reader.TryRead(out _).Should().BeFalse("the default pipeline's publisher sees nothing of it");
    }

    [Fact]
    public async Task A_Named_Pipeline_Without_A_Store_Fails_The_Host_Start()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutbox("orphan", _ => { });
            })
            .Build();

        var act = () => host.StartAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'orphan' has no store*");
    }

    [Fact]
    public async Task A_Named_Pipeline_With_Invalid_Options_Fails_The_Host_Start()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutbox("bad", o => o.BatchSize = 0).WithInMemoryStore();
            })
            .Build();

        var act = () => host.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>().WithMessage("*BatchSize*");
    }

    [Fact]
    public void Named_Options_Start_From_The_Defaults_Not_From_The_Default_Pipeline()
    {
        var services = new ServiceCollection();
        services.AddOutbox(o => o.BatchSize = 99);
        services.AddOutbox("named", o => o.PollingInterval = TimeSpan.FromSeconds(1));

        using var sp = services.BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<OutboxOptions>>();

        sp.GetRequiredService<IOptions<OutboxOptions>>().Value.BatchSize.Should().Be(99);
        monitor.Get("named").BatchSize.Should().Be(new OutboxOptions().BatchSize);
        monitor.Get("named").PollingInterval.Should().Be(TimeSpan.FromSeconds(1));
    }

    private sealed class TestDispatcher(string typeName, Action<ReadOnlyMemory<byte>> action) : IOutboxTypeDispatcher
    {
        public string TypeName => typeName;

        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            action(payload);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Records each dispatched payload with the <c>outbox.pipeline</c> tag of the
    /// <c>outbox.dispatch</c> activity it was dispatched under, and the <c>outbox.pipeline</c> tag
    /// of each <c>outbox.dispatch_attempts</c> measurement for one message type.
    /// </summary>
    private sealed class DispatchRecorder : IDisposable
    {
        private readonly MeterListener _meterListener = new();

        private readonly ActivityListener _listener = new()
        {
            ShouldListenTo = source => string.Equals(source.Name, "ZeroAlloc.Outbox", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };

        private int _count;

        public DispatchRecorder(string typeName)
        {
            ActivitySource.AddActivityListener(_listener);
            _meterListener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, "ZeroAlloc.Outbox", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "outbox.dispatch_attempts", StringComparison.Ordinal))
                    listener.EnableMeasurementEvents(instrument);
            };
            _meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                string? type = null;
                string? pipeline = null;
                foreach (ref readonly var tag in tags)
                {
                    if (string.Equals(tag.Key, "message.type", StringComparison.Ordinal))
                        type = tag.Value as string;
                    else if (string.Equals(tag.Key, "outbox.pipeline", StringComparison.Ordinal))
                        pipeline = tag.Value as string;
                }

                if (string.Equals(type, typeName, StringComparison.Ordinal))
                    AttemptPipelines.Enqueue(pipeline);
            });
            _meterListener.Start();
        }

        public ConcurrentQueue<string?> AttemptPipelines { get; } = new();

        public ConcurrentQueue<(string Payload, string? Pipeline)> Dispatches { get; } = new();

        /// <summary>Records one dispatch and returns how many were recorded so far.</summary>
        public int Record(ReadOnlyMemory<byte> payload)
        {
            var activity = Activity.Current;
            activity.Should().NotBeNull();
            activity!.OperationName.Should().Be("outbox.dispatch");
            Dispatches.Enqueue((Encoding.UTF8.GetString(payload.Span), activity.GetTagItem("outbox.pipeline") as string));
            return Interlocked.Increment(ref _count);
        }

        public void Dispose()
        {
            _listener.Dispose();
            _meterListener.Dispose();
        }
    }

    /// <summary>A store that claims nothing and records every claim a worker makes on it.</summary>
    private sealed class RecordingStore : IOutboxStore
    {
        private readonly TaskCompletionSource _firstClaim = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<(int BatchSize, string HostId)> Claims { get; } = new();

        public Task FirstClaim => _firstClaim.Task;

        public ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)
        {
            Claims.Enqueue((batchSize, lease.HostId));
            _firstClaim.TrySetResult();
            return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>([]);
        }

        public ValueTask EnqueueAsync(
            string typeName, ReadOnlyMemory<byte> payload, System.Data.Common.DbTransaction? transaction, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> MarkFailedAsync(
            OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
