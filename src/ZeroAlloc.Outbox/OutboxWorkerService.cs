using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Hosted service that polls the outbox store and dispatches pending messages.
/// Creates a DI scope per batch cycle so that scoped services (e.g. EF Core DbContext)
/// are correctly isolated. Dead-letters messages after <see cref="OutboxOptions.MaxAttempts"/> failures.
/// </summary>
/// <remarks>
/// Each cycle claims a batch under one <see cref="OutboxLease"/> built from
/// <see cref="OutboxOptions.HostId"/> and <see cref="OutboxOptions.LeaseDuration"/>, then renews
/// the lease on each message right before handling it. A message whose lease was lost, because
/// it expired and another host may have claimed it, is skipped rather than dispatched. A mark
/// that finds the message claimed or completed by another host is counted on
/// <c>outbox.lease.lost</c> and the batch carries on. Once a dispatch has run, its outcome is
/// recorded even when the host is stopping, under a short budget of its own. When the batch ends
/// early, because the worker is stopping or a store call failed, the worker releases the leases
/// it has not finished, so another host can claim them at once. Only a cancellation of the
/// stopping token stops the worker: a dispatcher that throws
/// <see cref="OperationCanceledException"/> on its own, such as on an HTTP timeout, has failed
/// that dispatch attempt, which is retried or dead-lettered like any other failure.
/// </remarks>
public sealed class OutboxWorkerService : BackgroundService
{
    private static readonly ActivitySource _activitySource = new("ZeroAlloc.Outbox");
    private static readonly Meter _meter = new("ZeroAlloc.Outbox");
    private static readonly Counter<long> _dispatchAttempts = _meter.CreateCounter<long>("outbox.dispatch_attempts");
    private static readonly Counter<long> _retries = _meter.CreateCounter<long>("outbox.retries");
    private static readonly Counter<long> _deadLetters = _meter.CreateCounter<long>("outbox.dead_letters");
    private static readonly Counter<long> _leaseLost = _meter.CreateCounter<long>("outbox.lease.lost");
    private static readonly Histogram<double> _dispatchDurationMs = _meter.CreateHistogram<double>("outbox.dispatch_duration_ms");

    /// <summary>
    /// Budget for store bookkeeping that runs whether or not the host is stopping: recording the
    /// outcome of a dispatch that ran, and releasing the leases a batch did not finish.
    /// </summary>
    private static readonly TimeSpan s_bookkeepingTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxWorkerService> _logger;

    public OutboxWorkerService(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxOptions> options,
        ILogger<OutboxWorkerService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsStopping(ex, stoppingToken))
            {
                // A cancellation that did not come from the stopping token, such as a timeout
                // inside the store, is an error in this batch, not a request to stop the host.
                _logger.LogError(ex, "Unhandled error in outbox worker batch.");
            }

            try
            {
                await Task.Delay(_options.PollingInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
#pragma warning disable MA0004 // ConfigureAwait cannot be applied to 'await using' — scope disposal runs on thread pool
        await using var scope = _scopeFactory.CreateAsyncScope();
#pragma warning restore MA0004
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var publisher = scope.ServiceProvider.GetService<IOutboxDashboardEventPublisher>();

        var dispatchers = new Dictionary<string, IOutboxTypeDispatcher>(StringComparer.Ordinal);
        foreach (var d in scope.ServiceProvider.GetRequiredService<IEnumerable<IOutboxTypeDispatcher>>())
            dispatchers[d.TypeName] = d;

        var lease = new OutboxLease(_options.HostId, _options.LeaseDuration);
        var entries = await store.ClaimPendingAsync(_options.BatchSize, lease, ct).ConfigureAwait(false);

        var progress = new BatchProgress();
        try
        {
            while (progress.Next < entries.Count)
            {
                ct.ThrowIfCancellationRequested();
                await ProcessEntryAsync(store, publisher, dispatchers, entries, progress, lease, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Stopping mid-batch, or a store call failed. Hand back what this host claimed but
            // did not finish, including an entry whose dispatch was cancelled or never ran, so
            // another host need not wait out the lease. An entry whose dispatch ran is already
            // behind progress.Next and keeps its lease: releasing it would get it dispatched twice.
            await ReleaseUnfinishedAsync(store, entries, progress.Next, lease, ex, ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Best-effort release of the leases on <paramref name="entries"/> from index
    /// <paramref name="from"/> on. A failure is logged, not thrown: the leases then simply
    /// expire after <see cref="OutboxOptions.LeaseDuration"/>, and the caller's exception is the
    /// one that surfaces.
    /// </summary>
    private async Task ReleaseUnfinishedAsync(
        IOutboxStore store, IReadOnlyList<OutboxEntry> entries, int from, OutboxLease lease, Exception cause,
        CancellationToken stoppingToken)
    {
        if (from >= entries.Count) return;

        var ids = new OutboxMessageId[entries.Count - from];
        for (var i = from; i < entries.Count; i++)
            ids[i - from] = entries[i].Id;

        var reason = IsStopping(cause, stoppingToken) ? "Stopping mid-batch" : "Batch failed";

        // The stopping token may already be cancelled, so the release gets its own budget.
        using var timeout = new CancellationTokenSource(s_bookkeepingTimeout);
        try
        {
            var released = await store.ReleaseLeasesAsync(ids, lease, timeout.Token).ConfigureAwait(false);
            _logger.LogInformation(
                "{Reason}: released {Released} of {Unfinished} unfinished outbox leases.",
                reason, released, ids.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Reason}: could not release {Count} unfinished outbox leases; they expire after {LeaseDuration}.",
                reason, ids.Length, lease.Duration);
        }
    }

    private async Task ProcessEntryAsync(
        IOutboxStore store,
        IOutboxDashboardEventPublisher? publisher,
        Dictionary<string, IOutboxTypeDispatcher> dispatchers,
        IReadOnlyList<OutboxEntry> entries,
        BatchProgress progress,
        OutboxLease lease,
        CancellationToken ct)
    {
        var entry = entries[progress.Next];
        var tag = new TagList { { "message.type", entry.TypeName } };

        // Renew before acting on the message at all, including dead-lettering it for a
        // missing dispatcher. If the lease expired while earlier entries in the batch were
        // handled, another host may already own this message.
        if (!await store.RenewLeaseAsync(entry.Id, lease, ct).ConfigureAwait(false))
        {
            progress.Next++;
            _leaseLost.Add(1, new TagList { { "message.type", entry.TypeName }, { "reason", "renew-failed" } });
            _logger.LogWarning(
                "Lease on outbox message {Id} ({TypeName}) was lost before dispatch; skipping it. "
                + "Consider raising OutboxOptions.LeaseDuration.",
                entry.Id, entry.TypeName);
            return;
        }

        if (!dispatchers.TryGetValue(entry.TypeName, out var dispatcher))
        {
            await DeadLetterMissingDispatcherAsync(store, publisher, entry, lease, ct).ConfigureAwait(false);
            progress.Next++;
            return;
        }

        using var activity = _activitySource.StartActivity("outbox.dispatch");
        activity?.SetTag("message.type", entry.TypeName);
        var startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            Exception? failure = null;
            try
            {
                await dispatcher.DispatchAsync(entry.Payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsStopping(ex, ct))
            {
                // Includes a cancellation the dispatcher raised on its own, such as an HttpClient
                // timeout: that is a failed dispatch attempt, retried like any other failure.
                failure = ex;
            }

            // The dispatch ran to an outcome, so the batch is done with this entry: releasing it
            // now would let another host dispatch it again.
            progress.Next++;
            await RecordOutcomeAsync(store, publisher, entry, lease, failure, tag, activity, ct).ConfigureAwait(false);
        }
        finally
        {
            _dispatchDurationMs.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, tag);
        }
    }

    /// <summary>
    /// Records the outcome of a dispatch that ran: marks it succeeded, or hands the failure to
    /// the retry and dead-letter handling.
    /// </summary>
    /// <remarks>
    /// The outcome is recorded even when the host is stopping, so it runs under a budget of its
    /// own rather than the stopping token. Were the mark cancelled, the message would stay
    /// pending and another host would dispatch it again, or a failed attempt would go uncounted.
    /// The dashboard event that follows is published on <paramref name="stoppingToken"/>, not
    /// on that budget.
    /// </remarks>
    private async Task RecordOutcomeAsync(
        IOutboxStore store,
        IOutboxDashboardEventPublisher? publisher,
        OutboxEntry entry,
        OutboxLease lease,
        Exception? failure,
        TagList tag,
        Activity? activity,
        CancellationToken stoppingToken)
    {
        using var bookkeeping = new CancellationTokenSource(s_bookkeepingTimeout);

        if (failure is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, failure.Message);
            await HandleDispatchFailureAsync(store, publisher, entry, lease, failure, bookkeeping.Token, stoppingToken)
                .ConfigureAwait(false);
            return;
        }

        var moved = await store.MarkSucceededAsync(entry.Id, lease, bookkeeping.Token).ConfigureAwait(false);

        _dispatchAttempts.Add(1, tag);
        activity?.SetStatus(ActivityStatusCode.Ok);

        if (!moved)
        {
            RecordCompletedElsewhere(entry, "succeeded");
            return;
        }

        await SafePublishAsync(
            publisher,
            new MessageDispatchedEvent(entry.Id, DateTimeOffset.UtcNow, entry.RetryCount + 1),
            stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A mark changed nothing: the message is no longer pending under this host's lease. This
    /// host's lease may have run out mid-dispatch and another host claimed the message, or an
    /// operator cancelled it. That is not a dispatch failure, so it is counted and logged, and
    /// the batch carries on.
    /// </summary>
    private void RecordCompletedElsewhere(OutboxEntry entry, string outcome)
    {
        _leaseLost.Add(1, new TagList { { "message.type", entry.TypeName }, { "reason", "completed-elsewhere" } });
        _logger.LogWarning(
            "Outbox message {Id} ({TypeName}) is no longer pending under this host's lease, so marking it "
            + "{Outcome} changed nothing. Another host claimed it after the lease expired, or it was "
            + "cancelled or completed elsewhere.",
            entry.Id, entry.TypeName, outcome);
    }

    /// <summary>
    /// True only for a cancellation caused by <paramref name="stoppingToken"/>, which means the
    /// host is stopping. Any other exception, including a cancellation raised by a dispatcher or
    /// a store on its own, is an ordinary failure.
    /// </summary>
    private static bool IsStopping(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException && stoppingToken.IsCancellationRequested;

    /// <summary>
    /// Publishes a dashboard event. Any failure, including a timeout of the publisher's own, is
    /// logged and the event dropped; only a stop propagates.
    /// </summary>
    /// <remarks>
    /// Always given the stopping token, never the bookkeeping budget: a slow publisher must not
    /// use up a budget meant for the store, and a timeout on that budget would read as a stop.
    /// By the time an event is published the batch cursor is past the entry, so a stop that
    /// propagates from here releases nothing that was dispatched.
    /// </remarks>
    private async ValueTask SafePublishAsync(
        IOutboxDashboardEventPublisher? publisher,
        OutboxDashboardEvent evt,
        CancellationToken stoppingToken)
    {
        if (publisher is null) return;
        try
        {
            await publisher.PublishAsync(evt, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsStopping(ex, stoppingToken))
        {
            // Dashboard publish errors must not break core dispatch.
            _logger.LogWarning(ex, "IOutboxDashboardEventPublisher.PublishAsync threw; event dropped.");
        }
    }

    private async Task DeadLetterMissingDispatcherAsync(
        IOutboxStore store,
        IOutboxDashboardEventPublisher? publisher,
        OutboxEntry entry,
        OutboxLease lease,
        CancellationToken ct)
    {
        var error = $"No dispatcher for type '{entry.TypeName}'.";
        _logger.LogWarning(
            "No dispatcher registered for outbox type '{TypeName}'. Dead-lettering message {Id}.",
            entry.TypeName, entry.Id);
        if (!await store.DeadLetterAsync(entry.Id, error, lease, ct).ConfigureAwait(false))
        {
            RecordCompletedElsewhere(entry, "dead-lettered");
            return;
        }

        _deadLetters.Add(1, new TagList { { "message.type", entry.TypeName } });

        // No dispatch attempt was made; report the stored retry count as total attempts.
        await SafePublishAsync(
            publisher,
            new MessageDeadLetteredEvent(entry.Id, error, entry.RetryCount),
            ct).ConfigureAwait(false);
    }

    private async Task HandleDispatchFailureAsync(
        IOutboxStore store,
        IOutboxDashboardEventPublisher? publisher,
        OutboxEntry entry,
        OutboxLease lease,
        Exception ex,
        CancellationToken ct,
        CancellationToken stoppingToken)
    {
        int newRetryCount = entry.RetryCount + 1;

        if (newRetryCount >= _options.MaxAttempts)
        {
            _logger.LogError(ex,
                "Message {Id} ({TypeName}) exhausted {MaxAttempts} attempts. Dead-lettering.",
                entry.Id, entry.TypeName, _options.MaxAttempts);
            if (!await store.DeadLetterAsync(entry.Id, ex.Message, lease, ct).ConfigureAwait(false))
            {
                RecordCompletedElsewhere(entry, "dead-lettered");
                return;
            }

            _deadLetters.Add(1, new TagList { { "message.type", entry.TypeName } });

            await SafePublishAsync(
                publisher,
                new MessageDeadLetteredEvent(entry.Id, ex.Message, newRetryCount),
                stoppingToken).ConfigureAwait(false);
            return;
        }

        // exponent = newRetryCount - 1 → first retry waits base, second waits 2×base, third waits 4×base
        var delay = TimeSpan.FromMilliseconds(
            _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, newRetryCount - 1));
        var nextRetry = DateTimeOffset.UtcNow.Add(delay);

        _logger.LogWarning(ex,
            "Message {Id} ({TypeName}) failed (attempt {Attempt}/{Max}). Retry at {NextRetry}.",
            entry.Id, entry.TypeName, newRetryCount, _options.MaxAttempts, nextRetry);

        if (!await store.MarkFailedAsync(entry.Id, newRetryCount, nextRetry, lease, ct).ConfigureAwait(false))
        {
            RecordCompletedElsewhere(entry, "failed");
            return;
        }

        _retries.Add(1, new TagList { { "message.type", entry.TypeName } });

        await SafePublishAsync(
            publisher,
            new MessageFailedEvent(entry.Id, ex.Message, newRetryCount, nextRetry),
            stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Index of the first entry in the batch this host has not finished with. It moves past an
    /// entry once that entry's dispatch has run, before its outcome is recorded, so a batch that
    /// ends early never releases a message that was already dispatched.
    /// </summary>
    private sealed class BatchProgress
    {
        public int Next;
    }
}
