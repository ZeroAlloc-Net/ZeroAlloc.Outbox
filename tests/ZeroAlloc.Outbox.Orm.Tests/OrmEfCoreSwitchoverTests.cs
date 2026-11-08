using System.Collections.Concurrent;
using System.Data.Async.Adapters;
using System.Globalization;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.SqlClient;
using Xunit;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// Switching a pipeline from the EF Core store to the ORM store without moving its data, with
/// a second ORM pipeline on the same connection and database.
/// </summary>
/// <remarks>
/// Two tables share one SQL Server database. The EF Core store's table is <c>OutboxMessages</c>,
/// which the ORM pipeline addresses as <c>[OutboxMessages]</c>. The second pipeline's table is
/// <c>workflow_outbox</c>, made by <see cref="OutboxOrmMigrations.For"/>. Not run on PostgreSQL:
/// there EF Core quotes its mixed-case column names, which the ORM store's unquoted statements
/// do not match.
/// </remarks>
public sealed class OrmEfCoreSwitchoverTests(SqlServerFixture server) : IClassFixture<SqlServerFixture>
{
    private const string TypeName = "OrmSwitchover.Run";

    private sealed class EfDbContext(DbContextOptions<EfDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddOutboxMessages();
    }

    [Fact]
    public async Task Switching_the_first_pipeline_to_the_Orm_store_delivers_its_rows_and_leaves_the_second_table_alone()
    {
        var connectionString = await server.CreateDatabaseAsync();
        try
        {
            var ids = await SeedEfTableAsync(connectionString);
            await SeedSecondTableAsync(connectionString);
            var secondBefore = await SnapshotAsync(connectionString, "workflow_outbox");
            secondBefore.Should().HaveCount(3);

            // Both pipelines on the ORM store, one connection, two tables. Their workers run at the
            // same time on it, so the connection allows multiple active result sets.
            var shared = new SqlConnection(connectionString + ";MultipleActiveResultSets=true").AsAsync();
            await using var _ = shared;
            await shared.OpenAsync();
            var delivered = new ConcurrentQueue<byte>();
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton(shared);
            services.AddOutbox("first", Configure)
                .WithOrm(OutboxOrmDialect.SqlServer, o => o.TableName = "[OutboxMessages]");
            services.AddOutbox("second", Configure)
                .WithOrm(OutboxOrmDialect.SqlServer, o => o.TableName = "workflow_outbox");
            services.AddSingleton<IOutboxTypeDispatcher>(new RecordingDispatcher(p => delivered.Enqueue(p.Span[0])));

            await using var sp = services.BuildServiceProvider();
            var workers = sp.GetServices<IHostedService>().OfType<OutboxWorkerService>().ToList();
            workers.Should().HaveCount(2);
            foreach (var worker in workers)
                await worker.StartAsync(CancellationToken.None);
            try
            {
                await WaitForAsync(() => delivered.Count >= 2, TimeSpan.FromSeconds(20));
                await Task.Delay(300); // time for a duplicate delivery to show up
                delivered.Order().Should().Equal(new byte[] { 1, 2 }, "each pre-existing row is delivered once");

                // The dead letter goes back to pending through the first pipeline's requeue.
                await using var scope = sp.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredKeyedService<IOutboxRequeue>("first")
                    .RequeueAsync(ids.DeadLetter, CancellationToken.None);
                await WaitForAsync(() => delivered.Count >= 3, TimeSpan.FromSeconds(20));
                await Task.Delay(300);
            }
            finally
            {
                foreach (var worker in workers)
                    await worker.StopAsync(CancellationToken.None);
            }

            delivered.Order().Should().Equal(new byte[] { 1, 2, 3 }, "the requeued dead letter is delivered once");
            (await SnapshotAsync(connectionString, "workflow_outbox")).Should()
                .Equal(secondBefore, "the second pipeline's table is unchanged, requeue and workers included");
            (await ScalarAsync(connectionString, "SELECT COUNT(*) FROM [OutboxMessages] WHERE [Status] = 1"))
                .Should().Be(3, "all three rows of the quoted table ended succeeded");
        }
        finally
        {
            server.ClearPools();
        }
    }

    private static void Configure(OutboxOptions o)
    {
        o.BatchSize = 10;
        o.PollingInterval = TimeSpan.FromMilliseconds(20);
        o.LeaseDuration = TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// The EF Core store creates the quoted table and holds the old rows: one pending (payload 1),
    /// one leased by another host with a lease that has expired (2), one dead letter (3).
    /// </summary>
    private static async Task<(OutboxMessageId Pending, OutboxMessageId ExpiredLease, OutboxMessageId DeadLetter)>
        SeedEfTableAsync(string connectionString)
    {
        var ids = (OutboxMessageId.New(), OutboxMessageId.New(), OutboxMessageId.New());
        var options = new DbContextOptionsBuilder<EfDbContext>().UseSqlServer(connectionString).Options;
        var db = new EfDbContext(options);
        await using (db.ConfigureAwait(false))
        {
            await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
            db.Set<OutboxMessageEntity>().AddRange(
                new OutboxMessageEntity { Id = ids.Item1, TypeName = TypeName, Payload = [1] },
                new OutboxMessageEntity
                {
                    Id = ids.Item2,
                    TypeName = TypeName,
                    Payload = [2],
                    LockedBy = "crashed-host",
                    LockedUntil = DateTimeOffset.UtcNow.AddMinutes(-5),
                },
                new OutboxMessageEntity
                {
                    Id = ids.Item3,
                    TypeName = TypeName,
                    Payload = [3],
                    Status = OutboxMessageStatus.DeadLetter,
                    RetryCount = 5,
                    DeadLetterError = "boom",
                });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return ids;
    }

    /// <summary>
    /// The second pipeline's table, made by the migration source, with rows no worker will
    /// claim: one pending in the far future, one succeeded, one dead letter.
    /// </summary>
    private static async Task SeedSecondTableAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString).AsAsync();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var source = OutboxOrmMigrations.For(OutboxOrmDialect.SqlServer, "workflow_outbox");
            await new MigrationRunner(connection, source, new SqlServerMigrationDialect())
                .RunAsync().ConfigureAwait(false);
        }

        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO workflow_outbox (Id, TypeName, Payload, Status, RetryCount, NextRetryAt, CreatedAt, DeadLetterError)
            VALUES
              ('00000000-0000-0000-0000-000000000001', 'Other.Run', 0x0a, 0, 0, DATEADD(day, 1, SYSDATETIMEOFFSET()), SYSDATETIMEOFFSET(), NULL),
              ('00000000-0000-0000-0000-000000000002', 'Other.Run', 0x0b, 1, 0, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), NULL),
              ('00000000-0000-0000-0000-000000000003', 'Other.Run', 0x0c, 2, 5, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 'kept');
            """).ConfigureAwait(false);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        var raw = new SqlConnection(connectionString);
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync().ConfigureAwait(false);
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        var raw = new SqlConnection(connectionString);
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync().ConfigureAwait(false);
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;
                return Convert.ToInt64(
                    await cmd.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Every column of every row, as text, ordered by id.</summary>
    private static async Task<List<string>> SnapshotAsync(string connectionString, string table)
    {
        var rows = new List<string>();
        var raw = new SqlConnection(connectionString);
        await using (raw.ConfigureAwait(false))
        {
            await raw.OpenAsync().ConfigureAwait(false);
            var cmd = raw.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = $"SELECT * FROM {table} ORDER BY Id";
                var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var cells = new string[reader.FieldCount];
                        for (var i = 0; i < cells.Length; i++)
                            cells[i] = Render(reader.GetValue(i));
                        rows.Add(string.Join("|", cells));
                    }
                }
            }
        }

        return rows;
    }

    private static string Render(object value) => value switch
    {
        DBNull => "<null>",
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private sealed class RecordingDispatcher(Action<ReadOnlyMemory<byte>> action) : IOutboxTypeDispatcher
    {
        public string TypeName => OrmEfCoreSwitchoverTests.TypeName;

        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            action(payload);
            return ValueTask.CompletedTask;
        }
    }
}
