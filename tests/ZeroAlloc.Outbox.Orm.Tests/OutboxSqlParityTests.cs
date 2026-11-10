using AwesomeAssertions;
using Xunit;
using ZeroAlloc.Outbox.TestServers;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// Pins the SQL of each ZeroAlloc.ORM repository to what <see cref="OutboxSql"/> builds for the same
/// dialect, table name and <c>quoteColumns</c>, so moving the statements into placeholders changed no
/// text. TEMPORARY: it is deleted in Task 4 together with <see cref="OutboxSql"/>, which is what it
/// compares against.
/// </summary>
/// <remarks>
/// The recorded text is compared after normalising line endings and the leading and trailing
/// whitespace of the whole text; inner whitespace must match exactly.
/// </remarks>
public sealed class OutboxSqlParityTests(PostgresServerFixture server) : IClassFixture<PostgresServerFixture>
{
    private const string Table = "Outbox";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_RepositoryTextEqualsOutboxSql(bool quoteColumns)
    {
        var recorded = await SqliteOutboxRepositoryTests.RunEveryStatementAsync(quoteColumns);

        AssertParity(recorded, new OutboxSql(OutboxOrmDialect.Sqlite, Table, quoteColumns));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Postgres_RepositoryTextEqualsOutboxSql(bool quoteColumns)
    {
        var recorded = await new PostgresOutboxRepositoryTests(server).RunEveryStatementAsync(quoteColumns);

        AssertParity(recorded, new OutboxSql(OutboxOrmDialect.Postgres, Table, quoteColumns));
    }

    /// <summary>
    /// The statements arrive in the order the shared runner executes them: insert, insert in a
    /// transaction, claim, renew, retry, release, processed, dead letter, requeue, read state.
    /// </summary>
    internal static void AssertParity(IReadOnlyList<string> recorded, OutboxSql sql)
    {
        string[] expected =
        [
            sql.Insert, sql.Insert, sql.Claim, sql.RenewLease, sql.MarkForRetry, sql.ReleaseLease,
            sql.MarkProcessed, sql.DeadLetter, sql.Requeue, sql.ReadState,
        ];

        recorded.Should().HaveCount(expected.Length);
        for (var i = 0; i < expected.Length; i++)
            Normalise(recorded[i]).Should().Be(Normalise(expected[i]), $"statement {i} equals the OutboxSql text");
    }

    private static string Normalise(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
