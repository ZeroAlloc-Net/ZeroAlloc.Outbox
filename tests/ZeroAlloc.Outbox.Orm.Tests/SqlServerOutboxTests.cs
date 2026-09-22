using System.Data.Async;
using System.Data.Async.Adapters;
using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;
using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// The store against a real SQL Server, which is the only way to prove the
/// OFFSET/FETCH paging spelling and the DDL actually work.
/// </summary>
public sealed class SqlServerOutboxTests : IAsyncLifetime
{
    private static readonly byte[] s_payload = [1, 2, 3];

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        var conn = await ConnectAsync().ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            await new MigrationRunner(conn, OutboxOrmMigrations.SqlServer, new SqlServerMigrationDialect())
                .RunAsync(default).ConfigureAwait(false);
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    private async Task<IAsyncDbConnection> ConnectAsync()
    {
        var raw = new SqlConnection(_container.GetConnectionString());
        var c = raw.AsAsync();
        await c.OpenAsync().ConfigureAwait(false);
        return c;
    }

    private async Task<OrmOutboxStore> StoreAsync()
        => new(await ConnectAsync().ConfigureAwait(false), OutboxOrmDialect.SqlServer);

    [Fact]
    public async Task Enqueue_Then_Fetch_Round_Trips_On_SqlServer()
    {
        var store = await StoreAsync();

        await store.EnqueueAsync("Sql.Cmd", s_payload, transaction: null, CancellationToken.None);
        var pending = await store.FetchPendingAsync(10, CancellationToken.None);

        pending.Should().ContainSingle();
        pending[0].TypeName.Should().Be("Sql.Cmd");
        pending[0].RawPayload.Should().Equal(s_payload);
    }

    [Fact]
    public async Task Paging_Honours_BatchSize_Through_Offset_Fetch()
    {
        // The whole reason this adapter has two query variants: SQL Server has no
        // LIMIT, so the batch bound is expressed as OFFSET 0 ROWS FETCH NEXT.
        var store = await StoreAsync();
        for (var i = 0; i < 7; i++)
            await store.EnqueueAsync($"Batch.{i}", s_payload, null, CancellationToken.None);

        var batch = await store.FetchPendingAsync(3, CancellationToken.None);

        batch.Should().HaveCount(3);
        batch.Should().BeInAscendingOrder(e => e.CreatedAt);
    }

    [Fact]
    public async Task Transitions_Work_On_SqlServer()
    {
        var store = await StoreAsync();
        await store.EnqueueAsync("Flow", s_payload, null, CancellationToken.None);
        var entry = (await store.FetchPendingAsync(1, CancellationToken.None))[0];

        await store.MarkFailedAsync(entry.Id, 1, DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        (await store.FetchPendingAsync(10, CancellationToken.None)).Should().BeEmpty();

        await store.MarkFailedAsync(entry.Id, 2, DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None);
        var again = await store.FetchPendingAsync(10, CancellationToken.None);
        again.Should().ContainSingle();
        again[0].RetryCount.Should().Be(2);

        await store.MarkSucceededAsync(entry.Id, CancellationToken.None);
        (await store.FetchPendingAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Enqueue_In_A_Transaction_Rolls_Back_On_SqlServer()
    {
        // Enlisting on the caller's connection has to work per-provider, not just
        // on SQLite where it was first written.
        var store = await StoreAsync();
        var raw = new SqlConnection(_container.GetConnectionString());
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync();
            var tx = (System.Data.Common.DbTransaction)await raw.BeginTransactionAsync();
            await store.EnqueueAsync("Rolled.Back", s_payload, tx, CancellationToken.None);
            await tx.RollbackAsync();
        }

        (await store.FetchPendingAsync(10, CancellationToken.None))
            .Should().NotContain(e => e.TypeName == "Rolled.Back");
    }
}
