namespace ZeroAlloc.Outbox;

/// <summary>Configuration for the outbox background worker.</summary>
public sealed class OutboxOptions
{
    /// <summary>Maximum dispatch attempts before a message is dead-lettered. Default: 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Number of messages fetched per polling cycle. Default: 50.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Delay between polling cycles. Default: 5 seconds.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Base delay for exponential retry back-off. Default: 2 seconds.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
}
