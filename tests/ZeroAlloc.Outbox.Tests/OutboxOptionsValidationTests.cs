using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// The lease settings on <see cref="OutboxOptions"/> are validated when the options are resolved.
/// </summary>
public sealed class OutboxOptionsValidationTests
{
    [Fact]
    public void Defaults_Are_Valid()
    {
        var options = Resolve(_ => { });

        options.LeaseDuration.Should().Be(TimeSpan.FromMinutes(5));
        options.HostId.Should().NotBeNullOrEmpty();
        options.HostId.Length.Should().BeLessThanOrEqualTo(128);
    }

    [Fact]
    public void A_Zero_LeaseDuration_Is_Rejected()
    {
        var act = () => Resolve(o => o.LeaseDuration = TimeSpan.Zero);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("OutboxOptions.LeaseDuration must be greater than zero.");
    }

    [Fact]
    public void A_Negative_LeaseDuration_Is_Rejected()
    {
        var act = () => Resolve(o => o.LeaseDuration = TimeSpan.FromSeconds(-1));

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("OutboxOptions.LeaseDuration must be greater than zero.");
    }

    [Fact]
    public void A_LeaseDuration_Of_Exactly_One_Day_Is_Accepted()
    {
        var options = Resolve(o => o.LeaseDuration = TimeSpan.FromDays(1));

        options.LeaseDuration.Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    public void A_LeaseDuration_Longer_Than_One_Day_Is_Rejected()
    {
        var act = () => Resolve(o => o.LeaseDuration = TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("OutboxOptions.LeaseDuration must be at most 1 day.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_BatchSize_Below_One_Is_Rejected(int batchSize)
    {
        var act = () => Resolve(o => o.BatchSize = batchSize);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("OutboxOptions.BatchSize must be greater than zero.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_MaxAttempts_Below_One_Is_Rejected(int maxAttempts)
    {
        var act = () => Resolve(o => o.MaxAttempts = maxAttempts);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("OutboxOptions.MaxAttempts must be at least 1.");
    }

    [Fact]
    public void A_MaxAttempts_Of_One_Is_Accepted()
    {
        var options = Resolve(o => o.MaxAttempts = 1);

        options.MaxAttempts.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_PollingInterval_Of_Zero_Or_Less_Is_Rejected(int milliseconds)
    {
        var act = () => Resolve(o => o.PollingInterval = TimeSpan.FromMilliseconds(milliseconds));

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("OutboxOptions.PollingInterval must be greater than zero.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("	")]
    public void An_Empty_Or_Whitespace_HostId_Is_Rejected(string hostId)
    {
        var act = () => Resolve(o => o.HostId = hostId);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("OutboxOptions.HostId must not be empty or whitespace.");
    }

    [Fact]
    public void An_Over_Long_HostId_Is_Rejected()
    {
        var act = () => Resolve(o => o.HostId = new string('x', 129));

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("OutboxOptions.HostId must be at most 128 characters.");
    }

    [Fact]
    public void A_HostId_Of_Exactly_128_Characters_Is_Accepted()
    {
        var options = Resolve(o => o.HostId = new string('x', 128));

        options.HostId.Should().HaveLength(128);
    }

    private static OutboxOptions Resolve(Action<OutboxOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddOutbox(configure);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<OutboxOptions>>().Value;
    }
}
