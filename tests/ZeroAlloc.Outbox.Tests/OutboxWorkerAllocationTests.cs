using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// What one worker batch allocates. The store and dispatcher complete synchronously, so the whole
/// batch runs on the test thread and <see cref="GC.GetAllocatedBytesForCurrentThread"/> sees all of it.
/// </summary>
/// <remarks>
/// Not run in parallel: another test's ActivityListener or MeterListener on "ZeroAlloc.Outbox"
/// would make the worker allocate activities and measurements on this thread.
/// </remarks>
[Collection(nameof(OutboxWorkerAllocationTests))]
[CollectionDefinition(nameof(OutboxWorkerAllocationTests), DisableParallelization = true)]
public sealed class OutboxWorkerAllocationTests
{
    private const string TypeName = "MyApp.Ping";

    [Fact]
    public void IdlePoll_ResolvesNoDispatcher()
    {
        var resolutions = 0;
        var store = new SyncStore(0);
        using var provider = BuildProvider(store, services =>
            services.AddTransient<IOutboxTypeDispatcher>(_ =>
            {
                resolutions++;
                return new NoOpDispatcher();
            }));
        var worker = CreateWorker(provider);

        for (var i = 0; i < 5; i++)
            RunBatch(worker);

        resolutions.Should().Be(0, "an idle poll has nothing to dispatch");
    }

    [Fact]
    public void IdlePoll_AllocatesNothingBeyondItsScope()
    {
        var store = new SyncStore(0);
        using var provider = BuildProvider(store, services =>
            services.AddSingleton<IOutboxTypeDispatcher>(new NoOpDispatcher()));
        var worker = CreateWorker(provider);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var scopeOnly = Measure(() =>
        {
            var scope = scopeFactory.CreateAsyncScope();
            _ = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            scope.DisposeAsync().GetAwaiter().GetResult();
        });
        var idle = Measure(() => RunBatch(worker));

        idle.Should().BeLessThanOrEqualTo(scopeOnly,
            "the scope is the store's, and nothing else is needed until something is claimed");
    }

    [Fact]
    public void DispatchLoop_AllocatesNothingPerMessage()
    {
        using var oneProvider = BuildProvider(new SyncStore(1), services =>
            services.AddSingleton<IOutboxTypeDispatcher>(new NoOpDispatcher()));
        using var manyProvider = BuildProvider(new SyncStore(33), services =>
            services.AddSingleton<IOutboxTypeDispatcher>(new NoOpDispatcher()));
        var one = CreateWorker(oneProvider);
        var many = CreateWorker(manyProvider);

        var oneBytes = Measure(() => RunBatch(one));
        var manyBytes = Measure(() => RunBatch(many));

        (manyBytes - oneBytes).Should().Be(0,
            "with no dashboard publisher, recording a dispatch outcome allocates nothing per message");
    }

    private static ServiceProvider BuildProvider(SyncStore store, Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        configure(services);
        return services.BuildServiceProvider();
    }

    private static OutboxWorkerService CreateWorker(IServiceProvider provider) => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new OutboxOptions { HostId = "alloc-test", BatchSize = 64 }),
        NullLogger<OutboxWorkerService>.Instance);

    private static void RunBatch(OutboxWorkerService worker)
    {
        // Checked without an assertion library, which would allocate inside the measurement.
        var task = worker.ProcessBatchAsync(CancellationToken.None);
        if (!task.IsCompletedSuccessfully)
            throw new InvalidOperationException("Every call in the batch should complete synchronously.");
    }

    private static long Measure(Action action)
    {
        for (var i = 0; i < 20; i++)
            action();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++)
            action();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 10;
    }

    private sealed class NoOpDispatcher : IOutboxTypeDispatcher
    {
        public string TypeName => OutboxWorkerAllocationTests.TypeName;

        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>Hands out the same claimed batch on every poll, and every call succeeds synchronously.</summary>
    private sealed class SyncStore : IOutboxStore
    {
        private readonly IReadOnlyList<OutboxEntry> _batch;

        public SyncStore(int count)
        {
            var batch = new List<OutboxEntry>(count);
            for (var i = 0; i < count; i++)
            {
                batch.Add(new OutboxEntry
                {
                    Id = OutboxMessageId.New(),
                    TypeName = OutboxWorkerAllocationTests.TypeName,
                    RawPayload = [1],
                    RetryCount = 0,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                });
            }

            _batch = batch.AsReadOnly();
        }

        public ValueTask EnqueueAsync(
            string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)
            => new(_batch);

        public ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct) => new(true);

        public ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
            => new(0);

        public ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct) => new(true);

        public ValueTask<bool> MarkFailedAsync(
            OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
            => new(true);

        public ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
            => new(true);
    }
}
