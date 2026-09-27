using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// The dashboard operations of <see cref="EfCoreOutboxStore{TContext}"/> against a worker, or
/// another operator, that changes the same row at the same time, each on a
/// <see cref="DbContext"/> and connection of its own, the way separate hosts would be.
/// </summary>
/// <remarks>
/// <para>
/// The first test of each pair lands the other writer's change at the worst moment: right
/// before the dashboard operation's own write, after anything it may have read. An operation
/// that checks the state and then writes in separate steps overwrites that change. One whose
/// write carries its precondition in the <c>WHERE</c> clause finds the row changed and rejects.
/// </para>
/// <para>
/// The second runs both writers truly concurrently, many times over, and checks that exactly
/// one of them wins each time and that the row shows that winner's outcome.
/// </para>
/// </remarks>
public abstract class EfCoreDashboardRaceTests<TContext> : IAsyncLifetime
    where TContext : DbContext
{
    private const int RaceRounds = 25;
    private static readonly byte[] s_payload = [1, 2, 3];
    private static readonly OutboxLease s_worker = new("worker", TimeSpan.FromMinutes(5));

    private readonly List<TContext> _contexts = [];

    /// <summary>Creates this test's database, with the outbox schema in it.</summary>
    public abstract Task InitializeAsync();

    /// <summary>Releases this test's database and connections.</summary>
    public virtual async Task DisposeAsync()
    {
        for (var i = 0; i < _contexts.Count; i++)
            await _contexts[i].DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>A new context on this test's database, with a connection of its own.</summary>
    protected abstract TContext CreateContext(IInterceptor[] interceptors);

    /// <summary>A context this test disposes when it ends.</summary>
    protected TContext Context(params IInterceptor[] interceptors)
    {
        var db = CreateContext(interceptors);
        _contexts.Add(db);
        return db;
    }

    private EfCoreOutboxStore<TContext> Store(params IInterceptor[] interceptors) => new(Context(interceptors));

    private Task<OutboxMessageEntity?> ReadRowAsync(OutboxMessageId id) =>
        Context().Set<OutboxMessageEntity>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == id);

    private static async Task<OutboxMessageId> EnqueueAndClaimAsync(EfCoreOutboxStore<TContext> worker)
    {
        await worker.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None).ConfigureAwait(false);
        return (await worker.ClaimPendingAsync(1, s_worker, CancellationToken.None).ConfigureAwait(false))
            .Should().ContainSingle().Subject.Id;
    }

    [Fact]
    public async Task Requeue_Does_Not_Undo_A_Requeue_And_Dispatch_That_Land_Before_Its_Write()
    {
        // Another operator requeues the dead-lettered message, and a worker claims and dispatches
        // it, all before this requeue writes. Writing Pending over the dispatched row would
        // dispatch the message a second time.
        var worker = Store();
        var id = await EnqueueAndClaimAsync(worker);
        (await worker.DeadLetterAsync(id, "boom", s_worker, CancellationToken.None)).Should().BeTrue();

        var otherOperator = Store();
        var marked = false;
        var dashboard = Store(new BeforeFirstWriteInterceptor(async ct =>
        {
            await otherOperator.RequeueAsync(id, ct).ConfigureAwait(false);
            (await worker.ClaimPendingAsync(1, s_worker, ct).ConfigureAwait(false)).Should().ContainSingle();
            marked = await worker.MarkSucceededAsync(id, s_worker, ct).ConfigureAwait(false);
        }));

        var requeue = async () => await dashboard.RequeueAsync(id, CancellationToken.None).ConfigureAwait(false);

        (await requeue.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"Message {id} cannot be requeued in state Dispatched.");
        marked.Should().BeTrue();
        var row = await ReadRowAsync(id);
        row.Should().NotBeNull();
        row!.Status.Should().Be(OutboxMessageStatus.Succeeded, "the dispatch landed first and must stand");
        row.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Concurrent_Requeues_Of_One_Message_Have_Exactly_One_Winner()
    {
        for (var round = 0; round < RaceRounds; round++)
        {
            var worker = Store();
            var id = await EnqueueAndClaimAsync(worker);
            (await worker.DeadLetterAsync(id, "boom", s_worker, CancellationToken.None)).Should().BeTrue();
            var a = Store();
            var b = Store();

            var (aWon, bWon) = await RaceAsync(
                async () => await Succeeds(a.RequeueAsync(id, CancellationToken.None)).ConfigureAwait(false),
                async () => await Succeeds(b.RequeueAsync(id, CancellationToken.None)).ConfigureAwait(false));

            (aWon ^ bWon).Should().BeTrue($"exactly one requeue wins, round {round}");
            var row = await ReadRowAsync(id);
            row!.Status.Should().Be(OutboxMessageStatus.Pending);
            row.RetryCount.Should().Be(0);
            row.DeadLetterError.Should().BeNull();
            row.LockedBy.Should().BeNull();
        }
    }

    [Fact]
    public async Task Cancel_Does_Not_Delete_A_Message_Dispatched_Before_Its_Write()
    {
        // The holder dispatches and marks the message before the cancel deletes it. Deleting
        // the dispatched row would erase a delivered message and report it cancelled.
        var worker = Store();
        var id = await EnqueueAndClaimAsync(worker);

        var marked = false;
        var dashboard = Store(new BeforeFirstWriteInterceptor(async ct =>
            marked = await worker.MarkSucceededAsync(id, s_worker, ct).ConfigureAwait(false)));

        var cancel = async () => await dashboard.CancelAsync(id, CancellationToken.None).ConfigureAwait(false);

        (await cancel.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"Message {id} cannot be cancelled in state Dispatched.");
        marked.Should().BeTrue();
        var row = await ReadRowAsync(id);
        row.Should().NotBeNull("a dispatched message is not cancelled");
        row!.Status.Should().Be(OutboxMessageStatus.Succeeded);
    }

    [Fact]
    public async Task Cancel_Racing_The_Holders_Mark_Has_Exactly_One_Winner()
    {
        for (var round = 0; round < RaceRounds; round++)
        {
            var worker = Store();
            var id = await EnqueueAndClaimAsync(worker);
            var dashboard = Store();

            var (cancelled, marked) = await RaceAsync(
                async () => await Succeeds(dashboard.CancelAsync(id, CancellationToken.None)).ConfigureAwait(false),
                async () => await worker.MarkSucceededAsync(id, s_worker, CancellationToken.None).ConfigureAwait(false));

            (cancelled ^ marked).Should().BeTrue($"exactly one of cancel and mark wins, round {round}");
            var row = await ReadRowAsync(id);
            if (cancelled)
                row.Should().BeNull("the cancel won, so the row is gone");
            else
                row!.Status.Should().Be(OutboxMessageStatus.Succeeded, "the mark won, so the row is dispatched");
        }
    }

    [Fact]
    public async Task ForceDispatch_Does_Not_Write_To_A_Message_Dispatched_Before_Its_Write()
    {
        // The holder dispatches and marks the message before force-dispatch writes. Reporting
        // success and moving the due time of a dispatched row would misstate what happened.
        var worker = Store();
        var id = await EnqueueAndClaimAsync(worker);
        var dueBefore = (await ReadRowAsync(id))!.NextRetryAt;

        var marked = false;
        var dashboard = Store(new BeforeFirstWriteInterceptor(async ct =>
            marked = await worker.MarkSucceededAsync(id, s_worker, ct).ConfigureAwait(false)));

        var force = async () => await dashboard.ForceDispatchAsync(id, CancellationToken.None).ConfigureAwait(false);

        (await force.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"Message {id} is not pending (status: Succeeded).");
        marked.Should().BeTrue();
        var row = await ReadRowAsync(id);
        row!.Status.Should().Be(OutboxMessageStatus.Succeeded);
        row.NextRetryAt.Should().Be(dueBefore, "a dispatched row is not written to");
    }

    [Fact]
    public async Task ForceDispatch_Racing_The_Holders_Mark_Never_Takes_The_Lease_Or_Undoes_The_Mark()
    {
        // Force-dispatch does not conflict with the holder: it makes a pending message due and
        // leaves the lease alone, so the holder's mark always wins. Whether force-dispatch lands
        // before the mark, or finds the message dispatched and rejects, the row ends dispatched.
        for (var round = 0; round < RaceRounds; round++)
        {
            var worker = Store();
            var id = await EnqueueAndClaimAsync(worker);
            var dashboard = Store();

            var (_, marked) = await RaceAsync(
                async () => await Succeeds(dashboard.ForceDispatchAsync(id, CancellationToken.None)).ConfigureAwait(false),
                async () => await worker.MarkSucceededAsync(id, s_worker, CancellationToken.None).ConfigureAwait(false));

            marked.Should().BeTrue($"force-dispatch never takes the holder's lease, round {round}");
            var row = await ReadRowAsync(id);
            row!.Status.Should().Be(OutboxMessageStatus.Succeeded);
            row.LockedBy.Should().BeNull();
        }
    }

    /// <summary>Runs both operations at once, released together, and returns what each returned.</summary>
    private static async Task<(bool First, bool Second)> RaceAsync(Func<Task<bool>> first, Func<Task<bool>> second)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Task.Run(async () => { await start.Task.ConfigureAwait(false); return await first().ConfigureAwait(false); });
        var b = Task.Run(async () => { await start.Task.ConfigureAwait(false); return await second().ConfigureAwait(false); });
        start.SetResult();
        return (await a.ConfigureAwait(false), await b.ConfigureAwait(false));
    }

    /// <summary>
    /// Whether a dashboard operation went through. A rejection is how an operation that lost
    /// the race reports it, so that counts as losing; any other exception fails the test.
    /// </summary>
    private static async Task<bool> Succeeds(ValueTask operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs <c>interleave</c> once, right before the first command that writes, which is the
    /// dashboard operation's own <c>UPDATE</c> or <c>DELETE</c>. It stands in for another host
    /// whose change lands after anything the operation read and before it writes.
    /// </summary>
    private sealed class BeforeFirstWriteInterceptor(Func<CancellationToken, Task> interleave) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await InterleaveBeforeWriteAsync(command, cancellationToken).ConfigureAwait(false);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await InterleaveBeforeWriteAsync(command, cancellationToken).ConfigureAwait(false);
            return result;
        }

        private async Task InterleaveBeforeWriteAsync(DbCommand command, CancellationToken ct)
        {
            var text = command.CommandText;
            var writes = text.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || text.Contains("DELETE", StringComparison.OrdinalIgnoreCase);
            if (writes && Interlocked.Exchange(ref _fired, 1) == 0)
                await interleave(ct).ConfigureAwait(false);
        }
    }
}
