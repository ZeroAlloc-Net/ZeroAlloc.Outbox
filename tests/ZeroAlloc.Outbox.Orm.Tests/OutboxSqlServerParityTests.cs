using Xunit;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// The SQL Server case of <see cref="OutboxSqlParityTests"/>, in a class of its own so only the SQL
/// Server container starts for it. TEMPORARY, deleted in Task 4 with <see cref="OutboxSql"/>.
/// </summary>
public sealed class OutboxSqlServerParityTests(SqlServerFixture server) : IClassFixture<SqlServerFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlServer_RepositoryTextEqualsOutboxSql(bool quoteColumns)
    {
        var recorded = await new SqlServerOutboxRepositoryTests(server).RunEveryStatementAsync(quoteColumns);

        OutboxSqlParityTests.AssertParity(recorded, new OutboxSql(OutboxOrmDialect.SqlServer, "Outbox", quoteColumns));
    }
}
