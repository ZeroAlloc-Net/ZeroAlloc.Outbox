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

        var dispatchers = new Dictionary<string, IOutboxTypeDispatcher>(StringComparer.Ordinal);
        foreach (var d in scope.ServiceProvider.GetRequiredService<IEnumerable<IOutboxTypeDispatcher>>())
            dispatchers[d.TypeName] = d;

        var entries = await store.FetchPendingAsync(_options.BatchSize, ct).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessEntryAsync(store, dispatchers, entry, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessEntryAsync(
        IOutboxStore store,
        Dictionary<string, IOutboxTypeDispatcher> dispatchers,
        OutboxEntry entry,
        CancellationToken ct)
    {
        if (!dispatchers.TryGetValue(entry.TypeName, out var dispatcher))
        {
            _logger.LogWarning(
                "No dispatcher registered for outbox type '{TypeName}'. Dead-lettering message {Id}.",
                entry.TypeName, entry.Id);
            await store.DeadLetterAsync(entry.Id, $"No dispatcher for type '{entry.TypeName}'.", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await dispatcher.DispatchAsync(entry.Payload, ct).ConfigureAwait(false);
            await store.MarkSucceededAsync(entry.Id, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            int newRetryCount = entry.RetryCount + 1;

            if (newRetryCount >= _options.MaxAttempts)
            {
                _logger.LogError(ex,
                    "Message {Id} ({TypeName}) exhausted {MaxAttempts} attempts. Dead-lettering.",
                    entry.Id, entry.TypeName, _options.MaxAttempts);
                await store.DeadLetterAsync(entry.Id, ex.Message, ct).ConfigureAwait(false);
            }
            else
            {
                // exponent = newRetryCount - 1 → first retry waits base, second waits 2×base, third waits 4×base
                var delay = TimeSpan.FromMilliseconds(
                    _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, newRetryCount - 1));
                var nextRetry = DateTimeOffset.UtcNow.Add(delay);

                _logger.LogWarning(ex,
                    "Message {Id} ({TypeName}) failed (attempt {Attempt}/{Max}). Retry at {NextRetry}.",
                    entry.Id, entry.TypeName, newRetryCount, _options.MaxAttempts, nextRetry);

                await store.MarkFailedAsync(entry.Id, newRetryCount, nextRetry, ct).ConfigureAwait(false);
            }
        }
    }
}
