using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public sealed class OutboxWorkerTests
{
    [Fact]
    public async Task Worker_DispatchesPendingMessage_OnSinglePoll()
    {
        var dispatched = new List<string>();
        var tcs = new TaskCompletionSource();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutboxInMemory();
                services.AddOutbox(o =>
                {
                    o.BatchSize = 10;
                    o.PollingInterval = TimeSpan.FromMilliseconds(50);
                });
                services.AddSingleton<IOutboxTypeDispatcher>(
                    new TestDispatcher("MyApp.Ping", (_, _) =>
                    {
                        dispatched.Add("Ping");
                        tcs.TrySetResult();
                        return ValueTask.CompletedTask;
                    }));
            })
            .Build();

        var store = host.Services.GetRequiredService<IOutboxStore>();
        await store.EnqueueAsync("MyApp.Ping", new byte[] { 1 }, null, default);

        await host.StartAsync();
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        dispatched.Should().ContainSingle().Which.Should().Be("Ping");
    }

    [Fact]
    public async Task Worker_DeadLetters_AfterMaxAttempts()
    {
        int dispatchCalls = 0;

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutboxInMemory();
                services.AddOutbox(o =>
                {
                    o.MaxAttempts = 2;
                    o.BatchSize = 10;
                    o.PollingInterval = TimeSpan.FromMilliseconds(20);
                    o.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
                });
                services.AddSingleton<IOutboxTypeDispatcher>(
                    new TestDispatcher("MyApp.Fail", (_, _) =>
                    {
                        dispatchCalls++;
                        throw new InvalidOperationException("simulated failure");
                    }));
            })
            .Build();

        var store = host.Services.GetRequiredService<InMemoryOutboxStore>();
        await store.EnqueueAsync("MyApp.Fail", new byte[] { 1 }, null, default);

        await host.StartAsync();

        // Poll until dead-lettered (at most 3s)
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var all = store.AllEntries();
            if (all.Count > 0 && AllExhausted(all))
                break;
            await Task.Delay(30);
        }

        static bool AllExhausted(IReadOnlyList<InMemoryOutboxStore.InMemoryOutboxEntry> entries)
        {
            foreach (var e in entries)
            {
                if (e.RetryCount < 2 && e.Status != InMemoryOutboxStore.InMemoryEntryStatus.DeadLetter)
                    return false;
            }
            return true;
        }

        await host.StopAsync();

        dispatchCalls.Should().BeGreaterThanOrEqualTo(2);
        var finalEntry = store.AllEntries().First();
        finalEntry.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.DeadLetter);
    }

    [Fact]
    public async Task Worker_NoDispatcher_DeadLettersMessage()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutboxInMemory();
                services.AddOutbox(o =>
                {
                    o.BatchSize = 10;
                    o.PollingInterval = TimeSpan.FromMilliseconds(30);
                });
                // No dispatcher registered for "MyApp.Unknown"
            })
            .Build();

        var store = host.Services.GetRequiredService<InMemoryOutboxStore>();
        await store.EnqueueAsync("MyApp.Unknown", new byte[] { 1 }, null, default);

        await host.StartAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var polled = store.AllEntries();
            if (polled.Count > 0 && AllDeadLettered(polled))
                break;
            await Task.Delay(30);
        }

        static bool AllDeadLettered(IReadOnlyList<InMemoryOutboxStore.InMemoryOutboxEntry> entries)
        {
            foreach (var e in entries)
            {
                if (e.Status != InMemoryOutboxStore.InMemoryEntryStatus.DeadLetter)
                    return false;
            }
            return true;
        }

        await host.StopAsync();

        // After StopAsync, verify the entry was dead-lettered
        var all = store.AllEntries();
        all.Should().ContainSingle()
           .Which.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.DeadLetter);
    }

    [Fact]
    public async Task Worker_Skips_An_Entry_Whose_Lease_Was_Lost()
    {
        using var leaseLost = new LeaseLostListener("LeaseLost.Ping");
        var lost = NewEntry("LeaseLost.Ping");
        var kept = NewEntry("LeaseLost.Ping");
        var store = new ScriptedStore([lost, kept]) { LostLease = lost.Id, Last = kept.Id };
        var dispatched = new List<OutboxMessageId>();

        using var host = BuildHost(store, services => services.AddSingleton<IOutboxTypeDispatcher>(
            new TestDispatcher("LeaseLost.Ping", (payload, _) =>
            {
                lock (dispatched) dispatched.Add(new OutboxMessageId(new Guid(payload.Span)));
                return ValueTask.CompletedTask;
            })));

        await host.StartAsync();
        await store.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        dispatched.Should().Equal(kept.Id);
        store.Marked.Should().Equal(kept.Id);
        leaseLost.Measurements.Should().Equal(("LeaseLost.Ping", "renew-failed", 1L));
    }

    [Fact]
    public async Task Worker_Does_Not_Dead_Letter_A_Missing_Dispatcher_Entry_Whose_Lease_Was_Lost()
    {
        var lost = NewEntry("MyApp.Unknown");
        var kept = NewEntry("MyApp.Ping");
        var store = new ScriptedStore([lost, kept]) { LostLease = lost.Id, Last = kept.Id };

        using var host = BuildHost(store, services => services.AddSingleton<IOutboxTypeDispatcher>(
            new TestDispatcher("MyApp.Ping", (_, _) => ValueTask.CompletedTask)));

        await host.StartAsync();
        await store.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        store.Marked.Should().Equal(kept.Id);
    }

    [Fact]
    public async Task Worker_Continues_When_Another_Host_Already_Completed_A_Dispatched_Message()
    {
        // The dispatch ran, but by the time it finished another host had already completed
        // the message, so MarkSucceededAsync reports it did not move the row. That is not a
        // dispatch failure: no MarkFailed follows, and the rest of the batch carries on.
        using var leaseLost = new LeaseLostListener("Elsewhere.Ping");
        var first = NewEntry("Elsewhere.Ping");
        var second = NewEntry("Elsewhere.Ping");
        var store = new ScriptedStore([first, second]) { CompletedElsewhere = { first.Id }, Last = second.Id };
        var dispatched = new List<OutboxMessageId>();

        using var host = BuildHost(store, services => services.AddSingleton<IOutboxTypeDispatcher>(
            new TestDispatcher("Elsewhere.Ping", (payload, _) =>
            {
                lock (dispatched) dispatched.Add(new OutboxMessageId(new Guid(payload.Span)));
                return ValueTask.CompletedTask;
            })));

        await host.StartAsync();
        await store.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        dispatched.Should().Equal(first.Id, second.Id);
        store.Marked.Should().Equal(first.Id, second.Id);
        leaseLost.Measurements.Should().Equal(("Elsewhere.Ping", "completed-elsewhere", 1L));
    }

    [Fact]
    public async Task Worker_Continues_When_A_Failure_Or_Dead_Letter_Mark_Finds_The_Message_Completed_Elsewhere()
    {
        using var leaseLost = new LeaseLostListener("Elsewhere.Fail", "Elsewhere.Unknown");
        var failing = NewEntry("Elsewhere.Fail");
        var orphan = NewEntry("Elsewhere.Unknown");
        var ok = NewEntry("Elsewhere.Ok");
        var store = new ScriptedStore([failing, orphan, ok])
        {
            CompletedElsewhere = { failing.Id, orphan.Id },
            Last = ok.Id,
        };

        using var host = BuildHost(store, services =>
        {
            services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(
                "Elsewhere.Fail", (_, _) => throw new InvalidOperationException("simulated failure")));
            services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(
                "Elsewhere.Ok", (_, _) => ValueTask.CompletedTask));
        });

        await host.StartAsync();
        await store.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        store.Marked.Should().Equal(failing.Id, orphan.Id, ok.Id);
        leaseLost.Measurements.Should().BeEquivalentTo(new[]
        {
            ("Elsewhere.Fail", "completed-elsewhere", 1L),
            ("Elsewhere.Unknown", "completed-elsewhere", 1L),
        });
    }

    [Fact]
    public async Task A_Host_Whose_Lease_Expired_Mid_Dispatch_Keeps_Processing_After_Another_Host_Completed_It()
    {
        // Host A claims the first message with a short lease and dispatches slowly. While it
        // does, the lease expires and host B claims and completes the message. When A
        // finishes, its MarkSucceededAsync finds the row already dispatched: it must not
        // throw, and A's next batch must still be processed.
        using var leaseLost = new LeaseLostListener("Race.First");
        InMemoryOutboxStore store = null!;
        var firstDispatches = 0;
        var secondDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? hostBClaimedFirst = null;

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutbox(o =>
                {
                    o.BatchSize = 1;
                    o.PollingInterval = TimeSpan.FromMilliseconds(20);
                    o.LeaseDuration = TimeSpan.FromMilliseconds(50);
                }).WithInMemoryStore();
                services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher("Race.First", async (_, ct) =>
                {
                    Interlocked.Increment(ref firstDispatches);
                    await Task.Delay(150, ct).ConfigureAwait(false);
                    var leaseB = new OutboxLease("host-b", TimeSpan.FromMinutes(1));
                    var claimedByB = await store.ClaimPendingAsync(1, leaseB, ct).ConfigureAwait(false);
                    hostBClaimedFirst = claimedByB.Count == 1 && string.Equals(claimedByB[0].TypeName, "Race.First", StringComparison.Ordinal);
                    await store.MarkSucceededAsync(claimedByB[0].Id, leaseB, ct).ConfigureAwait(false);
                }));
                services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher("Race.Second", (_, _) =>
                {
                    secondDispatched.TrySetResult();
                    return ValueTask.CompletedTask;
                }));
            })
            .Build();

        store = host.Services.GetRequiredService<InMemoryOutboxStore>();
        await store.EnqueueAsync("Race.First", new byte[] { 1 }, null, default);
        await Task.Delay(20);
        await store.EnqueueAsync("Race.Second", new byte[] { 2 }, null, default);

        await host.StartAsync();
        await secondDispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        hostBClaimedFirst.Should().BeTrue();
        firstDispatches.Should().Be(1);
        store.AllEntries().Should().OnlyContain(e => e.Status == InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);
        leaseLost.Measurements.Should().Equal(("Race.First", "completed-elsewhere", 1L));
    }

    [Fact]
    public async Task Stopping_Mid_Batch_Releases_The_Unprocessed_Leases_So_Another_Host_Can_Claim_Them_At_Once()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddOutbox(o =>
                {
                    o.BatchSize = 10;
                    o.PollingInterval = TimeSpan.FromMilliseconds(20);
                    o.LeaseDuration = TimeSpan.FromMinutes(10);
                }).WithInMemoryStore();
                services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher("Stop.Slow", async (_, ct) =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }));
            })
            .Build();

        var store = host.Services.GetRequiredService<InMemoryOutboxStore>();
        for (var i = 0; i < 3; i++)
            await store.EnqueueAsync("Stop.Slow", new byte[] { (byte)i }, null, default);

        await host.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        // Without the release these rows stay leased for ten minutes. The in-flight entry
        // is released too: its dispatch was cancelled and never marked.
        var reclaimed = await store.ClaimPendingAsync(
            10, new OutboxLease("other-host", TimeSpan.FromMinutes(1)), default);
        reclaimed.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_Dispatch_That_Completes_After_Stop_Began_Is_Marked_Succeeded_And_Not_Released()
    {
        // The dispatcher ignores the stopping token and finishes after StopAsync has cancelled
        // it. The message went out, so it must end Succeeded, not be handed back as pending for
        // another host to dispatch again. The store honours its token as a database would.
        var (store, first, second) = await RunUntilStoppedMidDispatchAsync("StopAfter.Ok", fail: false);

        var entry = store.Inner.AllEntries().First(e => e.Id == first);
        entry.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);

        // Only the entry that never started is released; the dispatched one is not claimable.
        var reclaimed = await store.Inner.ClaimPendingAsync(
            10, new OutboxLease("other-host", TimeSpan.FromMinutes(1)), default);
        reclaimed.Select(e => e.Id).Should().Equal(second);
    }

    [Fact]
    public async Task A_Dispatch_That_Fails_After_Stop_Began_Records_The_Failed_Attempt()
    {
        // Same as above on the failure path: the attempt ran and failed, so it must count
        // towards MaxAttempts and wait out its retry delay rather than being released.
        var (store, first, second) = await RunUntilStoppedMidDispatchAsync("StopAfter.Fail", fail: true);

        var entry = store.Inner.AllEntries().First(e => e.Id == first);
        entry.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Pending);
        entry.RetryCount.Should().Be(1);
        entry.LockedBy.Should().BeNull();
        entry.NextRetryAt.Should().BeAfter(DateTimeOffset.UtcNow);

        var reclaimed = await store.Inner.ClaimPendingAsync(
            10, new OutboxLease("other-host", TimeSpan.FromMinutes(1)), default);
        reclaimed.Select(e => e.Id).Should().Equal(second);
    }

    [Fact]
    public async Task A_Store_Error_Mid_Batch_Releases_The_Unfinished_Leases()
    {
        // A renewal fails with a database error on the second of three entries. The first was
        // dispatched and marked; the second and third were never dispatched, and must be handed
        // back at once rather than stay leased for the full LeaseDuration.
        var store = new TokenHonouringStore { FailRenewOfType = "RenewError.Second" };
        await store.Inner.EnqueueAsync("RenewError.First", new byte[] { 1 }, null, default);
        await Task.Delay(5);
        await store.Inner.EnqueueAsync("RenewError.Second", new byte[] { 2 }, null, default);
        await Task.Delay(5);
        await store.Inner.EnqueueAsync("RenewError.Third", new byte[] { 3 }, null, default);
        var ids = store.Inner.AllEntries().OrderBy(e => e.CreatedAt).Select(e => e.Id).ToArray();

        using var host = BuildHost(store, services =>
        {
            services.PostConfigure<OutboxOptions>(o =>
            {
                o.LeaseDuration = TimeSpan.FromMinutes(10);
                o.PollingInterval = TimeSpan.FromMinutes(10);
            });
            foreach (var type in new[] { "RenewError.First", "RenewError.Second", "RenewError.Third" })
            {
                services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(
                    type, (_, _) => ValueTask.CompletedTask));
            }
        });

        await host.StartAsync();
        var released = await store.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        released.Should().Equal(ids[1], ids[2]);
        store.Inner.AllEntries().First(e => e.Id == ids[0]).Status
            .Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);
        var reclaimed = await store.Inner.ClaimPendingAsync(
            10, new OutboxLease("other-host", TimeSpan.FromMinutes(1)), default);
        reclaimed.Select(e => e.Id).Should().Equal(ids[1], ids[2]);
    }

    /// <summary>
    /// Enqueues two messages of <paramref name="typeName"/>, starts a worker whose dispatcher
    /// waits for the stopping token to be cancelled and then completes anyway, or fails when
    /// <paramref name="fail"/> is set, and stops the host once that dispatch is in flight.
    /// </summary>
    private static async Task<(TokenHonouringStore Store, OutboxMessageId First, OutboxMessageId Second)>
        RunUntilStoppedMidDispatchAsync(string typeName, bool fail)
    {
        var store = new TokenHonouringStore();
        await store.Inner.EnqueueAsync(typeName, new byte[] { 1 }, null, default).ConfigureAwait(false);
        await Task.Delay(5).ConfigureAwait(false);
        await store.Inner.EnqueueAsync(typeName, new byte[] { 2 }, null, default).ConfigureAwait(false);
        var ids = store.Inner.AllEntries().OrderBy(e => e.CreatedAt).Select(e => e.Id).ToArray();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(store, services =>
        {
            services.PostConfigure<OutboxOptions>(o => o.LeaseDuration = TimeSpan.FromMinutes(10));
            services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(typeName, async (_, ct) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Ignore the stop: the message has gone out regardless.
                }

                if (fail) throw new InvalidOperationException("simulated failure after stop");
            }));
        });

        await host.StartAsync().ConfigureAwait(false);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await host.StopAsync().ConfigureAwait(false);

        return (store, ids[0], ids[1]);
    }

    [Fact]
    public async Task Worker_Claims_With_The_Configured_Lease()
    {
        var store = new ScriptedStore([]);

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IOutboxStore>(store);
                services.AddOutbox(o =>
                {
                    o.PollingInterval = TimeSpan.FromMilliseconds(20);
                    o.HostId = "host-under-test";
                    o.LeaseDuration = TimeSpan.FromSeconds(42);
                });
            })
            .Build();

        await host.StartAsync();
        var lease = await store.Claimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        lease.Should().Be(new OutboxLease("host-under-test", TimeSpan.FromSeconds(42)));
    }

    [Fact]
    public async Task Worker_Marks_With_The_Lease_It_Claimed_The_Batch_With()
    {
        var ok = NewEntry("BatchLease.Ok");
        var failing = NewEntry("BatchLease.Fail");
        var orphan = NewEntry("BatchLease.Unknown");
        var store = new ScriptedStore([ok, failing, orphan]) { Last = orphan.Id };

        using var host = BuildHost(store, services =>
        {
            services.PostConfigure<OutboxOptions>(o =>
            {
                o.HostId = "batch-lease-host";
                o.LeaseDuration = TimeSpan.FromSeconds(42);
            });
            services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(
                "BatchLease.Ok", (_, _) => ValueTask.CompletedTask));
            services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(
                "BatchLease.Fail", (_, _) => throw new InvalidOperationException("simulated failure")));
        });

        await host.StartAsync();
        await store.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        var expected = new OutboxLease("batch-lease-host", TimeSpan.FromSeconds(42));
        store.Marked.Should().Equal(ok.Id, failing.Id, orphan.Id);
        store.MarkLeases.Should().Equal(expected, expected, expected);
    }

    [Fact]
    public async Task A_Dispatcher_Cancelling_On_Its_Own_Is_A_Failed_Attempt_And_The_Worker_Keeps_Running()
    {
        // An HttpClient timeout surfaces as TaskCanceledException although nobody cancelled the
        // stopping token. That is a failed dispatch: it must be recorded as a failure, the rest of
        // the batch must run, and the worker must keep polling instead of stopping the host.
        var timedOut = NewEntry("Cancelled.Timeout");
        var ok = NewEntry("Cancelled.Ok");
        var store = new ScriptedStore([timedOut, ok]) { Last = ok.Id };

        using var host = BuildHost(store, services =>
        {
            services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(
                "Cancelled.Timeout", (_, _) => throw new TaskCanceledException("simulated HTTP timeout")));
            services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(
                "Cancelled.Ok", (_, _) => ValueTask.CompletedTask));
        });

        await host.StartAsync();
        await store.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await store.PolledAgain.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        store.Failed.Should().Equal(timedOut.Id);
        store.Marked.Should().Equal(timedOut.Id, ok.Id);
    }

    [Fact]
    public async Task A_Publisher_Cancelling_On_Its_Own_After_A_Successful_Dispatch_Does_Not_Fail_The_Message()
    {
        // The dispatch succeeded and was marked, then the dashboard publish timed out. That event
        // is dropped: the message must not go down the failure path, and the worker keeps going.
        var first = NewEntry("PublishCancelled.Ping");
        var second = NewEntry("PublishCancelled.Ping");
        var store = new ScriptedStore([first, second]) { Last = second.Id };

        using var host = BuildHost(store, services =>
        {
            services.AddSingleton<IOutboxDashboardEventPublisher>(new CancellingPublisher());
            services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(
                "PublishCancelled.Ping", (_, _) => ValueTask.CompletedTask));
        });

        await host.StartAsync();
        await store.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await store.PolledAgain.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        store.Failed.Should().BeEmpty("a failed dashboard publish is not a failed dispatch");
        store.Marked.Should().Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task A_Dashboard_Publish_Slower_Than_The_Bookkeeping_Budget_Does_Not_Abort_The_Batch()
    {
        // The first publish outlasts the 5-second budget the worker gives a mark. It must be
        // judged against the stopping token, not that budget: the batch carries on and the
        // second entry is marked in the same batch rather than released.
        var first = NewEntry("SlowPublish.Ping");
        var second = NewEntry("SlowPublish.Ping");
        var store = new ScriptedStore([first, second]) { Last = second.Id };

        using var host = BuildHost(store, services =>
        {
            services.AddSingleton<IOutboxDashboardEventPublisher>(new SlowFirstPublisher(TimeSpan.FromSeconds(6)));
            services.AddSingleton<IOutboxTypeDispatcher>(new TestDispatcher(
                "SlowPublish.Ping", (_, _) => ValueTask.CompletedTask));
        });

        await host.StartAsync();
        await store.Done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await host.StopAsync();

        store.Marked.Should().Equal(first.Id, second.Id);
        store.Released.Should().BeEmpty("the batch finished; nothing was left to release");
    }

    [Fact]
    public async Task A_Claim_Cancelled_Other_Than_By_Stopping_Does_Not_Stop_The_Worker()
    {
        // A store call can surface a cancellation of its own, such as a command timeout, while
        // the host is not stopping. The worker must log it and claim again on a later cycle.
        var entry = NewEntry("ClaimCancelled.Ping");
        var store = new ScriptedStore([entry]) { CancelledClaims = 1, Last = entry.Id };

        using var host = BuildHost(store, services => services.AddSingleton<IOutboxTypeDispatcher>(
            new TestDispatcher("ClaimCancelled.Ping", (_, _) => ValueTask.CompletedTask)));

        await host.StartAsync();
        await store.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();

        store.Marked.Should().Equal(entry.Id);
        store.Failed.Should().BeEmpty();
    }

    private static IHost BuildHost(IOutboxStore store, Action<IServiceCollection> configure)
        => new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(store);
                services.AddOutbox(o =>
                {
                    o.BatchSize = 10;
                    o.PollingInterval = TimeSpan.FromMilliseconds(20);
                });
                configure(services);
            })
            .Build();

    // The payload carries the id, so the dispatcher can report which entry it saw.
    private static OutboxEntry NewEntry(string typeName)
    {
        var id = OutboxMessageId.New();
        return new OutboxEntry
        {
            Id = id,
            TypeName = typeName,
            RawPayload = id.Value.ToByteArray(),
            RetryCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Records <c>outbox.lease.lost</c> measurements for the given message types only, so
    /// tests running in parallel on the shared static meter don't see each other's counts.
    /// </summary>
    private sealed class LeaseLostListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly HashSet<string> _types;
        private readonly List<(string Type, string? Reason, long Value)> _measurements = [];

        public LeaseLostListener(params string[] types)
        {
            _types = new HashSet<string>(types, StringComparer.Ordinal);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, "ZeroAlloc.Outbox", StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "outbox.lease.lost", StringComparison.Ordinal))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>(OnMeasurement);
            _listener.Start();
        }

        public IReadOnlyList<(string Type, string? Reason, long Value)> Measurements
        {
            get
            {
                lock (_measurements) return [.. _measurements];
            }
        }

        public void Dispose() => _listener.Dispose();

        private void OnMeasurement(
            Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            string? type = null;
            string? reason = null;
            foreach (ref readonly var entry in tags)
            {
                // KeyValuePair is not a readonly struct: copy it once, explicitly.
                var tag = entry;
                if (string.Equals(tag.Key, "message.type", StringComparison.Ordinal)) type = tag.Value as string;
                else if (string.Equals(tag.Key, "reason", StringComparison.Ordinal)) reason = tag.Value as string;
            }

            if (type is null || !_types.Contains(type)) return;
            lock (_measurements) _measurements.Add((type, reason, value));
        }
    }

    /// <summary>
    /// The first publish takes <c>delay</c>, honouring its token; every later one completes at once.
    /// </summary>
    private sealed class SlowFirstPublisher(TimeSpan delay) : IOutboxDashboardEventPublisher
    {
        private int _calls;

        public async ValueTask PublishAsync(OutboxDashboardEvent evt, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        public OutboxDashboardSubscription Subscribe()
            => throw new NotSupportedException("The worker only publishes.");
    }

    /// <summary>Every publish fails with a cancellation that no caller requested.</summary>
    private sealed class CancellingPublisher : IOutboxDashboardEventPublisher
    {
        public ValueTask PublishAsync(OutboxDashboardEvent evt, CancellationToken ct)
            => throw new TaskCanceledException("simulated publish timeout");

        public OutboxDashboardSubscription Subscribe()
            => throw new NotSupportedException("The worker only publishes.");
    }

    /// <summary>
    /// Fails the first <see cref="CancelledClaims"/> claims with a cancellation the stopping
    /// token did not cause, then hands out one batch, then nothing. Renewal fails for <see cref="LostLease"/>, as if
    /// another host had claimed it after this host's lease expired, and every mark on an id
    /// in <see cref="CompletedElsewhere"/> reports that the row had already moved on. It records
    /// each mark, the lease it carried, and which marks were failures.
    /// </summary>
    private sealed class ScriptedStore(IReadOnlyList<OutboxEntry> batch) : IOutboxStore
    {
        private int _claims;

        public OutboxMessageId LostLease { get; init; }
        public HashSet<OutboxMessageId> CompletedElsewhere { get; } = [];
        public OutboxMessageId Last { get; init; }
        public int CancelledClaims { get; init; }

        public List<OutboxMessageId> Marked { get; } = [];
        public List<OutboxLease> MarkLeases { get; } = [];
        public List<OutboxMessageId> Failed { get; } = [];
        public List<OutboxMessageId> Released { get; } = [];
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes on the first claim after the batch, proving the worker still polls.</summary>
        public TaskCompletionSource PolledAgain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<OutboxLease> Claimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, System.Data.Common.DbTransaction? transaction, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)
        {
            Claimed.TrySetResult(lease);
            var claim = Interlocked.Increment(ref _claims) - CancelledClaims;
            if (claim <= 0) throw new OperationCanceledException("simulated store timeout");
            if (claim == 2) PolledAgain.TrySetResult();
            return ValueTask.FromResult(claim == 1 ? batch : (IReadOnlyList<OutboxEntry>)[]);
        }

        public ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => ValueTask.FromResult(id != LostLease);

        public ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
        {
            lock (Released) Released.AddRange(ids);
            return ValueTask.FromResult(0);
        }

        public ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => Record(id, lease, failed: false);

        public ValueTask<bool> MarkFailedAsync(
            OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
            => Record(id, lease, failed: true);

        public ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
            => Record(id, lease, failed: false);

        private ValueTask<bool> Record(OutboxMessageId id, OutboxLease lease, bool failed)
        {
            lock (Marked)
            {
                Marked.Add(id);
                MarkLeases.Add(lease);
                if (failed) Failed.Add(id);
            }

            if (id == Last) Done.TrySetResult();
            return ValueTask.FromResult(!CompletedElsewhere.Contains(id));
        }
    }

    /// <summary>
    /// An <see cref="InMemoryOutboxStore"/> that, like a database driver, throws when its token
    /// is already cancelled. It can fail the first renewal of one message type with a simulated
    /// database error, and it reports the ids of the first release.
    /// </summary>
    private sealed class TokenHonouringStore : IOutboxStore
    {
        private int _renewFailed;

        public InMemoryOutboxStore Inner { get; } = new();
        public string? FailRenewOfType { get; init; }
        public TaskCompletionSource<IReadOnlyList<OutboxMessageId>> Released { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, System.Data.Common.DbTransaction? transaction, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Inner.EnqueueAsync(typeName, payload, transaction, ct);
        }

        public ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Inner.ClaimPendingAsync(batchSize, lease, ct);
        }

        public ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var entry = Inner.AllEntries().First(e => e.Id == id);
            if (string.Equals(entry.TypeName, FailRenewOfType, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _renewFailed, 1) == 0)
            {
                throw new InvalidOperationException("simulated database error");
            }

            return Inner.RenewLeaseAsync(id, lease, ct);
        }

        public async ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var released = await Inner.ReleaseLeasesAsync(ids, lease, ct).ConfigureAwait(false);
            Released.TrySetResult([.. ids]);
            return released;
        }

        public ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Inner.MarkSucceededAsync(id, lease, ct);
        }

        public ValueTask<bool> MarkFailedAsync(
            OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Inner.MarkFailedAsync(id, retryCount, nextRetryAt, lease, ct);
        }

        public ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Inner.DeadLetterAsync(id, error, lease, ct);
        }
    }

    private sealed class TestDispatcher(
        string typeName,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> action) : IOutboxTypeDispatcher
    {
        public string TypeName => typeName;
        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => action(payload, ct);
    }
}
