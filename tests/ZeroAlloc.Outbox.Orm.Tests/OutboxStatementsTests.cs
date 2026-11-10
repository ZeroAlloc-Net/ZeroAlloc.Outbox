using AwesomeAssertions;
using Xunit;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// <see cref="OutboxStatements.Create"/> picks the generated repository for each dialect.
/// </summary>
public sealed class OutboxStatementsTests
{
    [Theory]
    [InlineData(OutboxOrmDialect.Sqlite, typeof(SqliteOutboxRepository))]
    [InlineData(OutboxOrmDialect.Postgres, typeof(PostgresOutboxRepository))]
    [InlineData(OutboxOrmDialect.SqlServer, typeof(SqlServerOutboxRepository))]
    public async Task Create_PicksTheRepositoryForTheDialect(OutboxOrmDialect dialect, Type expected)
    {
        await using var fx = new SqliteFixture();

        var statements = OutboxStatements.Create(dialect, await fx.ConnectAsync(), "OutboxMessages", quoteColumns: false);

        statements.Should().BeOfType(expected);
    }

    [Fact]
    public void Create_CoversEveryDialect()
    {
        // The theory above lists the dialects by hand; this fails when a new one is added without a row.
        Enum.GetValues<OutboxOrmDialect>().Should().BeEquivalentTo(
            [OutboxOrmDialect.Sqlite, OutboxOrmDialect.Postgres, OutboxOrmDialect.SqlServer]);
    }

    [Fact]
    public async Task Create_UnknownDialect_Throws()
    {
        await using var fx = new SqliteFixture();
        var connection = await fx.ConnectAsync();

        var act = () => OutboxStatements.Create((OutboxOrmDialect)99, connection, "OutboxMessages", quoteColumns: false);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("dialect");
    }
}
