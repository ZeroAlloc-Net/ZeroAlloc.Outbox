using AwesomeAssertions;
using Xunit;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// The statements are built at runtime from the configured table name, which is spliced in verbatim.
/// </summary>
public sealed class OutboxSqlTests
{
    public static TheoryData<OutboxOrmDialect, string> Cases()
    {
        var data = new TheoryData<OutboxOrmDialect, string>();
        foreach (var dialect in new[] { OutboxOrmDialect.Sqlite, OutboxOrmDialect.Postgres, OutboxOrmDialect.SqlServer })
        {
            foreach (var table in new[] { "OutboxMessages", "\"OutboxMessages\"", "public.\"Outbox Messages\"" })
                data.Add(dialect, table);
        }

        return data;
    }

    private static string[] All(OutboxSql sql) =>
    [
        sql.Insert, sql.MarkProcessed, sql.MarkForRetry, sql.DeadLetter,
        sql.ReleaseLease, sql.RenewLease, sql.Claim, sql.Requeue,
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Statements_name_the_configured_table_in_every_dialect(OutboxOrmDialect dialect, string table)
    {
        foreach (var statement in All(new OutboxSql(dialect, table)))
        {
            statement.Should().Contain(table);

            // Nothing else may still name the default table when another one is configured.
            var rest = statement.Replace(table, string.Empty, StringComparison.Ordinal);
            if (!string.Equals(table, "OutboxMessages", StringComparison.Ordinal))
                rest.Should().NotContain("OutboxMessages");
        }
    }

    [Fact]
    public void Default_table_text_is_the_unquoted_name()
    {
        var sql = new OutboxSql(OutboxOrmDialect.Sqlite, "OutboxMessages");
        sql.Insert.Should().Contain("INSERT INTO OutboxMessages");
        sql.Requeue.Should().Contain("UPDATE OutboxMessages").And.Contain("@deadLetter");
    }
}
