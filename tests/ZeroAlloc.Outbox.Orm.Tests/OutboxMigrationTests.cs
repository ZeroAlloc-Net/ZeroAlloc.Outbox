using AwesomeAssertions;
using Xunit;
using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// Migration 2, <c>add_outbox_lease</c>, upgrading a SQLite database already sitting at schema
/// version 1 with rows in it. The same upgrade on PostgreSQL and SQL Server is covered by
/// <see cref="OrmServerClaimTests"/>.
/// </summary>
public sealed class OutboxMigrationTests
{
    private static readonly byte[] s_payload = [1, 2, 3];

    [Fact]
    public async Task Migration_2_Adds_Lease_Columns_On_Sqlite_Without_Losing_Existing_Rows()
    {
        await using var fx = new SqliteFixture();

        // Bring the database up to schema version 1 only.
        var first = await fx.ConnectAsync();
        await using (first.ConfigureAwait(false))
        {
            var source = new FirstMigrationOnlySource(OutboxOrmMigrations.Sqlite);
            await new MigrationRunner(first, source, new SqliteMigrationDialect()).RunAsync();
        }

        // Insert a row under the v1 schema, before the lease columns exist.
        var v1 = await fx.ConnectAsync();
        await using (v1.ConfigureAwait(false))
        {
            await new OrmOutboxStore(v1).EnqueueAsync("Pre.Existing", s_payload, transaction: null, CancellationToken.None);
        }

        // Upgrade to the full schema, which applies migration 2 on top.
        var upgrade = await fx.ConnectAsync();
        await using (upgrade.ConfigureAwait(false))
        {
            await new MigrationRunner(upgrade, OutboxOrmMigrations.Sqlite, new SqliteMigrationDialect())
                .RunAsync();
        }

        await AssertLeaseColumnsAddedAndUnsetAsync(fx);

        // The pre-existing row is claimable under the new schema, and the claim leases it.
        var v2 = await fx.ConnectAsync();
        await using (v2.ConfigureAwait(false))
        {
            var lease = new OutboxLease("upgraded-host", TimeSpan.FromMinutes(1));
            var claimed = await new OrmOutboxStore(v2).ClaimPendingAsync(10, lease, CancellationToken.None);
            claimed.Should().ContainSingle().Which.TypeName.Should().Be("Pre.Existing");
        }
    }

    /// <summary>
    /// The lease columns exist after the upgrade, and the row written before it has them unset.
    /// </summary>
    private static async Task AssertLeaseColumnsAddedAndUnsetAsync(SqliteFixture fx)
    {
        var raw = await fx.OpenRawAsync().ConfigureAwait(false);
        await using (raw.ConfigureAwait(false))
        {
            var pragma = raw.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(OutboxMessages)";
            var reader = await pragma.ExecuteReaderAsync().ConfigureAwait(false);
            var columns = new List<string>();
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    columns.Add(reader.GetString(1));
                }
            }

            columns.Should().Contain("LockedBy").And.Contain("LockedUntil");

            var select = raw.CreateCommand();
            select.CommandText = "SELECT LockedBy, LockedUntil FROM OutboxMessages WHERE TypeName = 'Pre.Existing'";
            var row = await select.ExecuteReaderAsync().ConfigureAwait(false);
            await using (row.ConfigureAwait(false))
            {
                (await row.ReadAsync().ConfigureAwait(false)).Should().BeTrue();
                row.IsDBNull(0).Should().BeTrue("a pre-existing row is unleased");
                row.IsDBNull(1).Should().BeTrue("a pre-existing row is unleased");
            }
        }
    }
}
