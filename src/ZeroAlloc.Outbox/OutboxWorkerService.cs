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
public sealed class OutboxWorkerService : BackgroundService
{
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
#pragma warning disable CA1031 // broad exception catch in background loop is intentional
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
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

        var entries = await store.FetchPendingAsync(_options.BatchSize, ct).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessEntryAsync(store, publisher, dispatchers, entry, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessEntryAsync(
        IOutboxStore store,
        IOutboxDashboardEventPublisher? publisher,
        Dictionary<string, IOutboxTypeDispatcher> dispatchers,
        OutboxEntry entry,
        CancellationToken ct)
    {
        if (!dispatchers.TryGetValue(entry.TypeName, out var dispatcher))
        {
            await DeadLetterMissingDispatcherAsync(store, publisher, entry, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await dispatcher.DispatchAsync(entry.Payload, ct).ConfigureAwait(false);
            await store.MarkSucceededAsync(entry.Id, ct).ConfigureAwait(false);

            await SafePublishAsync(
                publisher,
                new MessageDispatchedEvent(entry.Id, DateTimeOffset.UtcNow, entry.RetryCount + 1),
                ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            await HandleDispatchFailureAsync(store, publisher, entry, ex, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask SafePublishAsync(
        IOutboxDashboardEventPublisher? publisher,
        OutboxDashboardEvent evt,
        CancellationToken ct)
    {
        if (publisher is null) return;
        try
        {
            await publisher.PublishAsync(evt, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // intentional broad catch — dashboard publish errors must not break core dispatch
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "IOutboxDashboardEventPublisher.PublishAsync threw; event dropped.");
        }
    }

    private async Task DeadLetterMissingDispatcherAsync(
        IOutboxStore store,
        IOutboxDashboardEventPublisher? publisher,
        OutboxEntry entry,
        CancellationToken ct)
    {
        var error = $"No dispatcher for type '{entry.TypeName}'.";
        _logger.LogWarning(
            "No dispatcher registered for outbox type '{TypeName}'. Dead-lettering message {Id}.",
            entry.TypeName, entry.Id);
        await store.DeadLetterAsync(entry.Id, error, ct).ConfigureAwait(false);

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
        Exception ex,
        CancellationToken ct)
    {
        int newRetryCount = entry.RetryCount + 1;

        if (newRetryCount >= _options.MaxAttempts)
        {
            _logger.LogError(ex,
                "Message {Id} ({TypeName}) exhausted {MaxAttempts} attempts. Dead-lettering.",
                entry.Id, entry.TypeName, _options.MaxAttempts);
            await store.DeadLetterAsync(entry.Id, ex.Message, ct).ConfigureAwait(false);

            await SafePublishAsync(
                publisher,
                new MessageDeadLetteredEvent(entry.Id, ex.Message, newRetryCount),
                ct).ConfigureAwait(false);
            return;
        }

        // exponent = newRetryCount - 1 → first retry waits base, second waits 2×base, third waits 4×base
        var delay = TimeSpan.FromMilliseconds(
            _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, newRetryCount - 1));
        var nextRetry = DateTimeOffset.UtcNow.Add(delay);

        _logger.LogWarning(ex,
            "Message {Id} ({TypeName}) failed (attempt {Attempt}/{Max}). Retry at {NextRetry}.",
            entry.Id, entry.TypeName, newRetryCount, _options.MaxAttempts, nextRetry);

        await store.MarkFailedAsync(entry.Id, newRetryCount, nextRetry, ct).ConfigureAwait(false);

        await SafePublishAsync(
            publisher,
            new MessageFailedEvent(entry.Id, ex.Message, newRetryCount, nextRetry),
            ct).ConfigureAwait(false);
    }
}
