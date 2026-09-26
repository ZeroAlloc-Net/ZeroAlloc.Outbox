using Microsoft.Extensions.Options;

namespace ZeroAlloc.Outbox;

/// <summary>
/// Rejects settings that would break the worker or the claim: a batch that claims nothing, a
/// message that is never attempted, a poll loop that never waits, a lease that never lasts or
/// that strands a crashed host's messages for days, or a host identity the stores cannot record.
/// </summary>
internal sealed class OutboxOptionsValidator : IValidateOptions<OutboxOptions>
{
    /// <summary>Matches the width of the <c>LockedBy</c> column in every store.</summary>
    internal const int MaxHostIdLength = 128;

    /// <summary>
    /// The longest lease accepted. A lease this long already keeps a crashed host's batch
    /// unclaimable for a day; anything longer is almost certainly a unit mistake.
    /// </summary>
    internal static readonly TimeSpan MaxLeaseDuration = TimeSpan.FromDays(1);

    public ValidateOptionsResult Validate(string? name, OutboxOptions options)
    {
        List<string>? failures = null;

        if (options.BatchSize <= 0)
            (failures ??= []).Add("OutboxOptions.BatchSize must be greater than zero.");

        if (options.MaxAttempts < 1)
            (failures ??= []).Add("OutboxOptions.MaxAttempts must be at least 1.");

        if (options.PollingInterval <= TimeSpan.Zero)
            (failures ??= []).Add("OutboxOptions.PollingInterval must be greater than zero.");

        if (options.LeaseDuration <= TimeSpan.Zero)
            (failures ??= []).Add("OutboxOptions.LeaseDuration must be greater than zero.");
        else if (options.LeaseDuration > MaxLeaseDuration)
            (failures ??= []).Add("OutboxOptions.LeaseDuration must be at most 1 day.");

        if (string.IsNullOrWhiteSpace(options.HostId))
            (failures ??= []).Add("OutboxOptions.HostId must not be empty or whitespace.");
        else if (options.HostId.Length > MaxHostIdLength)
            (failures ??= []).Add($"OutboxOptions.HostId must be at most {MaxHostIdLength} characters.");

        return failures is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
