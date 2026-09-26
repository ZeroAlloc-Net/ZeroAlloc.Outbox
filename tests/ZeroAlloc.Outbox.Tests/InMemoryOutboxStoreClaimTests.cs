using System.Collections.Concurrent;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// The lease-based claim on <see cref="InMemoryOutboxStore"/>: concurrent claimers never
/// share a message, an expired lease is reclaimable, and a lost lease cannot be renewed.
/// </summary>
public sealed class InMemoryOutboxStoreClaimTests : IDisposable
{
    private static readonly byte[] s_payload = [1];
    private readonly InMemoryOutboxStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task Concurrent_Claimers_Claim_Every_Message_Exactly_Once()
    {
        const int messages = 200;
        const int claimers = 8;
        for (var i = 0; i < messages; i++)
            await _store.EnqueueAsync("T", s_payload, null, default);

        var claimed = new ConcurrentBag<OutboxMessageId>();
        var tasks = new Task[claimers];
        for (var c = 0; c < claimers; c++)
        {
            var lease = new OutboxLease($"h{c}", TimeSpan.FromMinutes(1));
            tasks[c] = Task.Run(async () =>
            {
                while (true)
                {
                    var batch = await _store.ClaimPendingAsync(10, lease, default).ConfigureAwait(false);
                    if (batch.Count == 0) return;
                    foreach (var entry in batch)
                    {
                        claimed.Add(entry.Id);
                        await _store.MarkSucceededAsync(entry.Id, lease, default).ConfigureAwait(false);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        claimed.Should().HaveCount(messages);
        claimed.Distinct().Should().HaveCount(messages, "no message may be claimed twice");
    }

    [Fact]
    public async Task A_Claimed_Message_Is_Not_Claimable_By_Another_Host_While_Leased()
    {
        await _store.EnqueueAsync("T", s_payload, null, default);

        (await _store.ClaimPendingAsync(10, new OutboxLease("a", TimeSpan.FromMinutes(1)), default))
            .Should().ContainSingle();
        (await _store.ClaimPendingAsync(10, new OutboxLease("b", TimeSpan.FromMinutes(1)), default))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task An_Expired_Lease_Is_Claimable_By_Another_Host()
    {
        await _store.EnqueueAsync("T", s_payload, null, default);
        var first = await _store.ClaimPendingAsync(
            10, new OutboxLease("a", TimeSpan.FromMilliseconds(50)), default);

        await Task.Delay(100);
        var second = await _store.ClaimPendingAsync(
            10, new OutboxLease("b", TimeSpan.FromMinutes(1)), default);

        second.Should().ContainSingle().Which.Id.Should().Be(first[0].Id);
    }

    [Fact]
    public async Task A_Lost_Lease_Cannot_Be_Renewed_But_The_New_Holder_Can_Renew()
    {
        await _store.EnqueueAsync("T", s_payload, null, default);
        var leaseA = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var leaseB = new OutboxLease("b", TimeSpan.FromMinutes(1));
        var id = (await _store.ClaimPendingAsync(10, leaseA, default))[0].Id;

        await Task.Delay(100);
        (await _store.ClaimPendingAsync(10, leaseB, default)).Should().ContainSingle();

        (await _store.RenewLeaseAsync(id, leaseA, default)).Should().BeFalse();
        (await _store.RenewLeaseAsync(id, leaseB, default)).Should().BeTrue();
    }

    [Fact]
    public async Task An_Expired_Lease_Cannot_Be_Renewed_Even_By_Its_Own_Host()
    {
        await _store.EnqueueAsync("T", s_payload, null, default);
        var lease = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var id = (await _store.ClaimPendingAsync(10, lease, default))[0].Id;

        await Task.Delay(100);

        (await _store.RenewLeaseAsync(id, lease, default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_Failed_Message_Is_Claimable_Again_At_Its_Retry_Time()
    {
        await _store.EnqueueAsync("T", s_payload, null, default);
        var lease = new OutboxLease("a", TimeSpan.FromMinutes(1));
        var id = (await _store.ClaimPendingAsync(10, lease, default))[0].Id;

        await _store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, lease, default);

        var again = await _store.ClaimPendingAsync(10, new OutboxLease("b", TimeSpan.FromMinutes(1)), default);
        again.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task A_Dispatched_Or_Dead_Lettered_Message_Is_Never_Claimable()
    {
        await _store.EnqueueAsync("T", s_payload, null, default);
        await _store.EnqueueAsync("T", s_payload, null, default);
        var lease = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var batch = await _store.ClaimPendingAsync(10, lease, default);

        await _store.MarkSucceededAsync(batch[0].Id, lease, default);
        await _store.DeadLetterAsync(batch[1].Id, "boom", lease, default);
        await Task.Delay(100);

        (await _store.ClaimPendingAsync(10, new OutboxLease("b", TimeSpan.FromMinutes(1)), default))
            .Should().BeEmpty();
        _store.AllEntries().Should().OnlyContain(e => e.LockedBy == null && e.LockedUntil == null);
    }

    [Fact]
    public async Task A_Late_Mark_After_A_Lost_Lease_Does_Not_Overwrite_The_New_Holders_Outcome()
    {
        await _store.EnqueueAsync("T", s_payload, null, default);
        var leaseA = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var leaseB = new OutboxLease("b", TimeSpan.FromMinutes(1));
        var id = (await _store.ClaimPendingAsync(10, leaseA, default))[0].Id;

        await Task.Delay(100);
        (await _store.ClaimPendingAsync(10, leaseB, default)).Should().ContainSingle();
        (await _store.MarkSucceededAsync(id, leaseB, default)).Should().BeTrue();

        (await _store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, leaseA, default)).Should().BeFalse();

        var entry = _store.AllEntries().Should().ContainSingle().Subject;
        entry.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);
        entry.RetryCount.Should().Be(0, "the late mark must not touch the row at all");
    }

    [Fact]
    public async Task A_Mark_Reports_Whether_It_Moved_The_Message()
    {
        await _store.EnqueueAsync("T", s_payload, null, default);
        var lease = new OutboxLease("a", TimeSpan.FromMinutes(1));
        var id = (await _store.ClaimPendingAsync(10, lease, default))[0].Id;

        (await _store.MarkSucceededAsync(id, lease, default)).Should().BeTrue();
        (await _store.MarkSucceededAsync(id, lease, default)).Should().BeFalse();
        (await _store.DeadLetterAsync(id, "late", lease, default)).Should().BeFalse();
        (await _store.MarkSucceededAsync(OutboxMessageId.New(), lease, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Released_Leases_Are_Claimable_At_Once_But_Only_This_Hosts_Pending_Ones()
    {
        for (var i = 0; i < 3; i++)
            await _store.EnqueueAsync($"T{i}", s_payload, null, default);
        var leaseA = new OutboxLease("a", TimeSpan.FromMinutes(10));
        var byA = await _store.ClaimPendingAsync(2, leaseA, default);
        var byC = await _store.ClaimPendingAsync(1, new OutboxLease("c", TimeSpan.FromMinutes(10)), default);
        await _store.MarkSucceededAsync(byA[0].Id, leaseA, default);

        (await _store.ReleaseLeasesAsync([byA[0].Id, byA[1].Id, byC[0].Id], leaseA, default))
            .Should().Be(1, "only this host's pending rows are released");

        var byB = await _store.ClaimPendingAsync(10, new OutboxLease("b", TimeSpan.FromMinutes(1)), default);
        byB.Should().ContainSingle().Which.Id.Should().Be(byA[1].Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_Lease_Without_A_Host_Is_Rejected(string? hostId)
    {
        await _store.EnqueueAsync("T", s_payload, null, default);
        var lease = new OutboxLease(hostId!, TimeSpan.FromMinutes(1));
        var id = OutboxMessageId.New();

        var calls = new Func<Task>[]
        {
            async () => await _store.ClaimPendingAsync(10, lease, default).ConfigureAwait(false),
            async () => await _store.RenewLeaseAsync(id, lease, default).ConfigureAwait(false),
            async () => await _store.ReleaseLeasesAsync([id], lease, default).ConfigureAwait(false),
            async () => await _store.MarkSucceededAsync(id, lease, default).ConfigureAwait(false),
            async () => await _store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, lease, default).ConfigureAwait(false),
            async () => await _store.DeadLetterAsync(id, "boom", lease, default).ConfigureAwait(false),
        };

        foreach (var call in calls)
            await call.Should().ThrowAsync<ArgumentException>().WithParameterName("lease");
    }

    [Fact]
    public async Task A_Host_Whose_Lease_Was_Taken_Over_Cannot_Mark_And_Leaves_The_New_Lease_Alone()
    {
        await _store.EnqueueAsync("T", s_payload, null, default);
        var leaseA = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var id = (await _store.ClaimPendingAsync(10, leaseA, default))[0].Id;

        await Task.Delay(100);
        (await _store.ClaimPendingAsync(10, new OutboxLease("b", TimeSpan.FromMinutes(1)), default))
            .Should().ContainSingle();
        var entry = _store.AllEntries().Should().ContainSingle().Subject;
        var lockedUntilB = entry.LockedUntil;

        (await _store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, leaseA, default)).Should().BeFalse();
        (await _store.MarkSucceededAsync(id, leaseA, default)).Should().BeFalse();
        (await _store.DeadLetterAsync(id, "late", leaseA, default)).Should().BeFalse();

        entry.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Pending);
        entry.RetryCount.Should().Be(0);
        entry.LockedBy.Should().Be("b", "host b's lease must survive host a's late marks");
        entry.LockedUntil.Should().Be(lockedUntilB);
    }

    [Fact]
    public async Task A_Host_Whose_Lease_Expired_Unclaimed_Can_Still_Mark()
    {
        await _store.EnqueueAsync("T", s_payload, null, default);
        var lease = new OutboxLease("a", TimeSpan.FromMilliseconds(50));
        var id = (await _store.ClaimPendingAsync(10, lease, default))[0].Id;

        await Task.Delay(100);

        (await _store.MarkSucceededAsync(id, lease, default)).Should().BeTrue();
        var entry = _store.AllEntries().Should().ContainSingle().Subject;
        entry.Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Succeeded);
        entry.LockedBy.Should().BeNull();
        entry.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task An_Unclaimed_Message_Cannot_Be_Marked()
    {
        await _store.EnqueueAsync("T", s_payload, null, default);
        var id = _store.AllEntries()[0].Id;
        var lease = new OutboxLease("a", TimeSpan.FromMinutes(1));

        (await _store.MarkSucceededAsync(id, lease, default)).Should().BeFalse();
        _store.AllEntries()[0].Status.Should().Be(InMemoryOutboxStore.InMemoryEntryStatus.Pending);
    }

    [Fact]
    public async Task Claim_Returns_The_Oldest_Messages_First()
    {
        for (var i = 0; i < 5; i++)
            await _store.EnqueueAsync($"T{i}", s_payload, null, default);

        var batch = await _store.ClaimPendingAsync(3, new OutboxLease("a", TimeSpan.FromMinutes(1)), default);

        batch.Should().HaveCount(3);
        batch.Should().BeInAscendingOrder(e => e.CreatedAt);
    }
}
