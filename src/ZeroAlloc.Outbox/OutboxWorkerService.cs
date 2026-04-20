using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Hosted service that polls the outbox store and dispatches pending messages.
/// Dead-letters messages after <see cref="OutboxOptions.MaxAttempts"/> failures.
/// </summary>
public sealed class OutboxWorkerService : BackgroundService
{
    private readonly IOutboxStore _store;
    private readonly Dictionary<string, IOutboxTypeDispatcher> _dispatchers;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxWorkerService> _logger;

    public OutboxWorkerService(
        IOutboxStore store,
        IEnumerable<IOutboxTypeDispatcher> dispatchers,
        IOptions<OutboxOptions> options,
        ILogger<OutboxWorkerService> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;

        var dict = new Dictionary<string, IOutboxTypeDispatcher>(StringComparer.Ordinal);
        foreach (var d in dispatchers)
            dict[d.TypeName] = d;
        _dispatchers = dict;
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
        var entries = await _store.FetchPendingAsync(_options.BatchSize, ct).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessEntryAsync(entry, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessEntryAsync(OutboxEntry entry, CancellationToken ct)
    {
        if (!_dispatchers.TryGetValue(entry.TypeName, out var dispatcher))
        {
            _logger.LogWarning(
                "No dispatcher registered for outbox type '{TypeName}'. Dead-lettering message {Id}.",
                entry.TypeName, entry.Id);
            await _store.DeadLetterAsync(entry.Id, $"No dispatcher for type '{entry.TypeName}'.", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await dispatcher.DispatchAsync(entry.Payload, ct).ConfigureAwait(false);
            await _store.MarkSucceededAsync(entry.Id, ct).ConfigureAwait(false);
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
                await _store.DeadLetterAsync(entry.Id, ex.Message, ct).ConfigureAwait(false);
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

                await _store.MarkFailedAsync(entry.Id, newRetryCount, nextRetry, ct).ConfigureAwait(false);
            }
        }
    }
}
