namespace ZeroAlloc.Outbox;

/// <summary>Configuration for the outbox background worker.</summary>
public sealed class OutboxOptions
{
    private static readonly string s_defaultHostId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>Maximum dispatch attempts before a message is dead-lettered. Must be at least 1. Default: 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Number of messages claimed per polling cycle. Must be greater than zero. Default: 50.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Delay between polling cycles. Must be greater than zero. Default: 5 seconds.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Base delay for exponential retry back-off. Default: 2 seconds.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a claim on a message lasts, and how far each renewal extends it. Default: 5 minutes.
    /// </summary>
    /// <remarks>
    /// The worker renews the lease right before dispatching each message, so this must exceed
    /// the slowest single dispatch, not the whole batch. When a dispatch outlives it, another
    /// host may claim and dispatch the same message. Must be greater than zero and at most
    /// one day.
    /// </remarks>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Identifies this process as the holder of the leases it claims.
    /// Default: the machine name plus a random value, fixed for the life of the process.
    /// </summary>
    /// <remarks>
    /// Must be unique per running process, not empty or whitespace, and at most 128 characters.
    /// Two processes sharing one value would treat each other's leases as their own.
    /// </remarks>
    public string HostId { get; set; } = s_defaultHostId;
}
