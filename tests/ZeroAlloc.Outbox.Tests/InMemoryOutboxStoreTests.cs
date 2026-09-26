using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public sealed class InMemoryOutboxStoreTests : IDisposable
{
    private static readonly OutboxLease s_lease = new("test-host", TimeSpan.FromMinutes(1));
    private readonly InMemoryOutboxStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task Enqueue_ThenClaim_ReturnsPendingEntry()
    {
        await _store.EnqueueAsync("MyApp.OrderPlaced", new byte[] { 1, 2, 3 }, null, default);

        var entries = await _store.ClaimPendingAsync(10, s_lease, default);

        entries.Should().HaveCount(1);
        entries[0].TypeName.Should().Be("MyApp.OrderPlaced");
        entries[0].RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task MarkSucceeded_RemovesFromPendingClaim()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var entries = await _store.ClaimPendingAsync(10, s_lease, default);

        await _store.MarkSucceededAsync(entries[0].Id, s_lease, default);

        var remaining = await _store.ClaimPendingAsync(10, s_lease, default);
        remaining.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkFailed_DefersByNextRetryAt()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var entries = await _store.ClaimPendingAsync(10, s_lease, default);
        var id = entries[0].Id;

        var nextRetry = DateTimeOffset.UtcNow.AddHours(1);
        await _store.MarkFailedAsync(id, 1, nextRetry, s_lease, default);

        // Should not appear in ClaimPending (NextRetryAt is in the future)
        var pending = await _store.ClaimPendingAsync(10, s_lease, default);
        pending.Should().BeEmpty();

        // RetryCount should have been incremented
        var internalEntry = _store.AllEntries().First(e => e.Id == id);
        internalEntry.RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task DeadLetter_RemovesFromPendingClaim()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var entries = await _store.ClaimPendingAsync(10, s_lease, default);

        await _store.DeadLetterAsync(entries[0].Id, "permanent failure", s_lease, default);

        var remaining = await _store.ClaimPendingAsync(10, s_lease, default);
        remaining.Should().BeEmpty();
    }

    [Fact]
    public async Task ClaimPending_RespectsMaxBatchSize()
    {
        for (int i = 0; i < 5; i++)
            await _store.EnqueueAsync("T", new byte[] { 1 }, null, default);

        var entries = await _store.ClaimPendingAsync(3, s_lease, default);

        entries.Should().HaveCount(3);
    }

    [Fact]
    public async Task Enqueue_PayloadRoundTrips()
    {
        byte[] data = { 10, 20, 30 };
        await _store.EnqueueAsync("T", data, null, default);

        var entries = await _store.ClaimPendingAsync(10, s_lease, default);

        entries[0].RawPayload.Should().BeEquivalentTo(data);
    }
}
