using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// Two <see cref="OutboxWorkerService"/> hosts, each with a service provider of its own and a
/// distinct <see cref="OutboxOptions.HostId"/>, polling one PostgreSQL outbox through the EF Core
/// store. Every message is dispatched exactly once, since no dispatch outlives the lease.
/// </summary>
public sealed class TwoWorkerEndToEndTests(PostgresServerFixture server) : IClassFixture<PostgresServerFixture>
{
    private const string TypeName = "TwoWorkers.Ping";

    [Fact]
    public async Task Two_Workers_Sharing_A_Database_Dispatch_Every_Message_Exactly_Once()
    {
        const int messages = 300;
        var connectionString = await server.CreateDatabaseAsync();
        var sent = await EnqueueAsync(connectionString, messages);

        var dispatches = new ConcurrentDictionary<Guid, int>();
        var holders = new ConcurrentBag<(string HostId, string? LockedBy)>();
        using var workerA = BuildWorker(connectionString, "worker-a", dispatches, holders);
        using var workerB = BuildWorker(connectionString, "worker-b", dispatches, holders);
        HostIdOf(workerA).Should().Be("worker-a");
        HostIdOf(workerB).Should().Be("worker-b");

        await Task.WhenAll(workerA.StartAsync(), workerB.StartAsync());
        try
        {
            await WaitUntilAllDispatchedAsync(connectionString, messages, TimeSpan.FromSeconds(60));
        }
        finally
        {
            // Stop both together, so one failing to stop cannot leave the other running.
            await Task.WhenAll(workerA.StopAsync(), workerB.StopAsync());
            NpgsqlConnection.ClearAllPools();
        }

        dispatches.Keys.Should().BeEquivalentTo(sent, "every message is dispatched");
        dispatches.Values.Should().OnlyContain(n => n == 1, "no message may be dispatched twice");
        holders.Should().OnlyContain(
            h => h.LockedBy == h.HostId, "a worker dispatches only messages leased under its own HostId");
        holders.Select(h => h.HostId).Distinct(StringComparer.Ordinal).Should().BeEquivalentTo(
            ["worker-a", "worker-b"], "both workers share the load");
    }

    private static string HostIdOf(IHost worker)
        => worker.Services.GetRequiredService<IOptions<OutboxOptions>>().Value.HostId;

    private static async Task<List<Guid>> EnqueueAsync(string connectionString, int messages)
    {
        var sent = new List<Guid>(messages);
        var db = NewContext(connectionString);
        await using (db.ConfigureAwait(false))
        {
            await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
            var store = new EfCoreOutboxStore<ServerClaimDbContext>(db);
            for (var i = 0; i < messages; i++)
            {
                // The payload is a key of its own, so the dispatcher can count each message.
                var key = Guid.NewGuid();
                sent.Add(key);
                await store.EnqueueAsync(TypeName, key.ToByteArray(), transaction: null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        return sent;
    }

    private static async Task WaitUntilAllDispatchedAsync(string connectionString, int messages, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (true)
        {
            var db = NewContext(connectionString);
            await using (db.ConfigureAwait(false))
            {
                var dispatched = await db.OutboxMessages
                    .CountAsync(e => e.Status == OutboxMessageStatus.Succeeded, deadline.Token)
                    .ConfigureAwait(false);
                if (dispatched == messages) return;
            }

            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
    }

    private static ServerClaimDbContext NewContext(string connectionString)
        => new(new DbContextOptionsBuilder<ServerClaimDbContext>().UseNpgsql(connectionString).Options);

    private static IHost BuildWorker(
        string connectionString,
        string hostId,
        ConcurrentDictionary<Guid, int> dispatches,
        ConcurrentBag<(string HostId, string? LockedBy)> holders)
        => new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddDbContext<ServerClaimDbContext>(o => o.UseNpgsql(connectionString));
                services.AddOutbox(o =>
                {
                    o.HostId = hostId;
                    o.BatchSize = 10;
                    o.PollingInterval = TimeSpan.FromMilliseconds(50);
                    o.LeaseDuration = TimeSpan.FromSeconds(30);
                }).WithEfCore<ServerClaimDbContext>();

                // Scoped, so it shares the worker's per-batch DbContext, and keyed by the HostId
                // this host's worker actually resolved.
                services.AddScoped<IOutboxTypeDispatcher>(sp => new CountingDispatcher(
                    sp.GetRequiredService<IOptions<OutboxOptions>>().Value.HostId,
                    sp.GetRequiredService<ServerClaimDbContext>(),
                    dispatches,
                    holders));
            })
            .Build();

    /// <summary>
    /// Counts every dispatch by message, from any thread, and records which host the message is
    /// leased to in the database while it is being dispatched.
    /// </summary>
    private sealed class CountingDispatcher(
        string hostId,
        ServerClaimDbContext db,
        ConcurrentDictionary<Guid, int> dispatches,
        ConcurrentBag<(string HostId, string? LockedBy)> holders) : IOutboxTypeDispatcher
    {
        public string TypeName => TwoWorkerEndToEndTests.TypeName;

        public async ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            dispatches.AddOrUpdate(new Guid(payload.Span), 1, static (_, n) => n + 1);

            var key = payload.ToArray();
            var lockedBy = await db.OutboxMessages
                .AsNoTracking()
                .Where(e => e.Payload == key)
                .Select(e => e.LockedBy)
                .SingleAsync(ct)
                .ConfigureAwait(false);
            holders.Add((hostId, lockedBy));
        }
    }
}
