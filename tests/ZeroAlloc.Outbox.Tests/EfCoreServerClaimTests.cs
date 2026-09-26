using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// <see cref="OutboxServerClaimTests"/> for <see cref="EfCoreOutboxStore{TContext}"/>, each store
/// on a <see cref="DbContext"/> of its own, with the schema created by <c>EnsureCreated</c> from
/// the mapping an application gets from <see cref="OutboxDbContextExtensions.AddOutboxMessages"/>.
/// </summary>
/// <remarks>
/// The EF Core claim is three statements: read candidate ids, lease them with a conditional
/// <c>ExecuteUpdate</c> that repeats the unleased predicate, then read back what this call
/// leased. Its exclusivity rests on the engine re-checking that predicate under the row lock,
/// which only a real server can show.
/// </remarks>
public abstract class EfCoreServerClaimTests : OutboxServerClaimTests
{
    private static readonly byte[] s_payload = [1, 2, 3];

    private readonly List<ServerClaimDbContext> _contexts = [];
    private string _connectionString = string.Empty;

    /// <summary>Creates an empty database on the class's container.</summary>
    protected abstract Task<string> CreateDatabaseAsync();

    /// <summary>Points the options at this server.</summary>
    protected abstract void UseServer(DbContextOptionsBuilder<ServerClaimDbContext> builder, string connectionString);

    /// <summary>Drops pooled connections, so tests do not pile up server sessions.</summary>
    protected abstract void ClearPools();

    public override async Task InitializeAsync()
    {
        _connectionString = await CreateDatabaseAsync().ConfigureAwait(false);
        await Context().Database.EnsureCreatedAsync().ConfigureAwait(false);
    }

    public override async Task DisposeAsync()
    {
        for (var i = 0; i < _contexts.Count; i++)
            await _contexts[i].DisposeAsync().ConfigureAwait(false);
        ClearPools();
    }

    protected override Task<IOutboxStore> StoreAsync()
        => Task.FromResult<IOutboxStore>(new EfCoreOutboxStore<ServerClaimDbContext>(Context()));

    protected override async Task<long> CountWithStatusAsync(int status)
    {
        var persisted = (OutboxMessageStatus)status;
        return await Context().OutboxMessages.LongCountAsync(e => e.Status == persisted).ConfigureAwait(false);
    }

    protected override async Task<StoredRow> ReadRowAsync(OutboxMessageId id)
    {
        var row = await Context().OutboxMessages.AsNoTracking().SingleAsync(e => e.Id == id).ConfigureAwait(false);
        return new StoredRow((int)row.Status, row.RetryCount, row.LockedBy, row.LockedUntil);
    }

    [Fact]
    public async Task A_Claim_Whose_Candidates_Are_Taken_Selects_The_Next_Due_Rows()
    {
        // Another host leases exactly this claim's candidates between its candidate SELECT and
        // its conditional UPDATE, so the first attempt leases nothing. The retry must select
        // again and come back with the next due rows, not with an empty or short batch.
        const int batchSize = 5;
        var writer = await StoreAsync();
        for (var i = 0; i < batchSize * 2; i++)
            await writer.EnqueueAsync("T", s_payload, transaction: null, CancellationToken.None);
        var oldestFirst = await Context().OutboxMessages.AsNoTracking()
            .OrderBy(e => e.CreatedAt).Select(e => e.Id).ToListAsync();
        var taken = oldestFirst.Take(batchSize).ToList();
        DateTimeOffset? otherUntil = DateTimeOffset.UtcNow.AddMinutes(1);

        var thief = new TakeCandidatesInterceptor(ct => Context().OutboxMessages
            .Where(e => taken.Contains(e.Id))
            .ExecuteUpdateAsync(
                u => u.SetProperty(e => e.LockedBy, "other")
                      .SetProperty(e => e.LockedUntil, otherUntil),
                ct));
        var store = new EfCoreOutboxStore<ServerClaimDbContext>(Context(thief));

        var batch = await store.ClaimPendingAsync(batchSize, new OutboxLease("a", TimeSpan.FromMinutes(1)), CancellationToken.None);

        thief.Took.Should().Be(batchSize, "the other host leased every first-attempt candidate");
        // Compared as sets: CreatedAt can tie, so the order among equal timestamps is not fixed.
        batch.Select(e => e.Id).Should().BeEquivalentTo(oldestFirst.Skip(batchSize), "the retry claims the next due rows");
        var holders = await Context().OutboxMessages.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.LockedBy);
        batch.Should().OnlyContain(e => holders[e.Id] == "a", "no returned row may carry the other host's lease");
        taken.Should().OnlyContain(id => holders[id] == "other", "the claim must not take over a live lease");
    }

    private ServerClaimDbContext Context(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<ServerClaimDbContext>();
        UseServer(builder, _connectionString);
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        var db = new ServerClaimDbContext(builder.Options);
        _contexts.Add(db);
        return db;
    }

    /// <summary>
    /// Runs <c>take</c> right before the first non-query command, which is the claim's conditional
    /// lease update, standing in for another host that leases the candidates in between.
    /// </summary>
    private sealed class TakeCandidatesInterceptor(Func<CancellationToken, Task<int>> take) : DbCommandInterceptor
    {
        private int _fired;

        /// <summary>How many rows the other host leased, once it has.</summary>
        public int Took { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
                Took = await take(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }
}
