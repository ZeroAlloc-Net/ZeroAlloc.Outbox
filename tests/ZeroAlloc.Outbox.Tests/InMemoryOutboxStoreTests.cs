using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public sealed class InMemoryOutboxStoreTests
{
    private readonly InMemoryOutboxStore _store = new();

    [Fact]
    public async Task Enqueue_ThenFetch_ReturnsPendingEntry()
    {
        await _store.EnqueueAsync("MyApp.OrderPlaced", new byte[] { 1, 2, 3 }, null, default);

        var entries = await _store.FetchPendingAsync(10, default);

        entries.Should().HaveCount(1);
        entries[0].TypeName.Should().Be("MyApp.OrderPlaced");
        entries[0].RetryCount.Should().Be(0);
    }

    [Fact]
    public async Task MarkSucceeded_RemovesFromPendingFetch()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var entries = await _store.FetchPendingAsync(10, default);

        await _store.MarkSucceededAsync(entries[0].Id, default);

        var remaining = await _store.FetchPendingAsync(10, default);
        remaining.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkFailed_DefersByNextRetryAt()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var entries = await _store.FetchPendingAsync(10, default);
        var id = entries[0].Id;

        var nextRetry = DateTimeOffset.UtcNow.AddHours(1);
        await _store.MarkFailedAsync(id, 1, nextRetry, default);

        // Should not appear in FetchPending (NextRetryAt is in the future)
        var pending = await _store.FetchPendingAsync(10, default);
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task DeadLetter_RemovesFromPendingFetch()
    {
        await _store.EnqueueAsync("T", new byte[] { 1 }, null, default);
        var entries = await _store.FetchPendingAsync(10, default);

        await _store.DeadLetterAsync(entries[0].Id, "permanent failure", default);

        var remaining = await _store.FetchPendingAsync(10, default);
        remaining.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchPending_RespectsMaxBatchSize()
    {
        for (int i = 0; i < 5; i++)
            await _store.EnqueueAsync("T", new byte[] { 1 }, null, default);

        var entries = await _store.FetchPendingAsync(3, default);

        entries.Should().HaveCount(3);
    }

    [Fact]
    public async Task Enqueue_PayloadRoundTrips()
    {
        byte[] data = { 10, 20, 30 };
        await _store.EnqueueAsync("T", data, null, default);

        var entries = await _store.FetchPendingAsync(10, default);

        entries[0].RawPayload.Should().BeEquivalentTo(data);
    }
}
