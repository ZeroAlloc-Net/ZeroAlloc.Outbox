using System.Text.RegularExpressions;
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
        sql.ReleaseLease, sql.RenewLease, sql.Claim, sql.Requeue, sql.ReadState,
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
    public void Claim_has_the_shape_of_its_dialect()
    {
        var pg = new OutboxSql(OutboxOrmDialect.Postgres, "OutboxMessages").Claim;
        pg.Should().Contain("MATERIALIZED").And.Contain("SKIP LOCKED");

        var ss = new OutboxSql(OutboxOrmDialect.SqlServer, "OutboxMessages").Claim;
        ss.Should().Contain("READPAST").And.Contain("UPDLOCK").And.Contain("ROWLOCK");

        var lite = new OutboxSql(OutboxOrmDialect.Sqlite, "OutboxMessages").Claim;
        lite.Should().Contain("LIMIT").And.Contain("RETURNING");
    }

    [Fact]
    public void Default_table_text_is_the_unquoted_name()
    {
        var sql = new OutboxSql(OutboxOrmDialect.Sqlite, "OutboxMessages");
        sql.Insert.Should().Contain("INSERT INTO OutboxMessages");
        sql.Requeue.Should().Contain("UPDATE OutboxMessages").And.Contain("@deadLetter");
        sql.ReadState.Should().Contain("FROM OutboxMessages").And.Contain("@id");
    }

    [Fact]
    public void Requeue_resets_the_row_and_is_guarded_by_the_dead_letter_status()
    {
        var sql = new OutboxSql(OutboxOrmDialect.Sqlite, "OutboxMessages").Requeue;
        var flat = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        flat.Should().Contain("Status = @status").And.Contain("RetryCount = 0").And.Contain("NextRetryAt = @now")
            .And.Contain("DeadLetterError = NULL").And.Contain("ProcessedAt = NULL")
            .And.Contain("LockedBy = NULL").And.Contain("LockedUntil = NULL")
            .And.Contain("WHERE Id = @id AND Status = @deadLetter");
    }

    private static readonly string[] Columns =
    [
        "Id", "TypeName", "Payload", "Status", "RetryCount", "NextRetryAt", "CreatedAt",
        "ProcessedAt", "DeadLetterError", "LockedBy", "LockedUntil",
    ];

    private static (string Open, string Close) Quotes(OutboxOrmDialect dialect)
        => dialect == OutboxOrmDialect.SqlServer ? ("[", "]") : ("\"", "\"");

    public static TheoryData<OutboxOrmDialect> Dialects() => new()
    {
        OutboxOrmDialect.Sqlite, OutboxOrmDialect.Postgres, OutboxOrmDialect.SqlServer,
    };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Quoted_columns_quote_every_column_name_in_every_statement(OutboxOrmDialect dialect)
    {
        var (open, close) = Quotes(dialect);
        var bare = string.Join('|', Columns);
        foreach (var statement in All(new OutboxSql(dialect, "Outbox", quoteColumns: true)))
        {
            // A column name not wrapped in the dialect's quotes is a bug. Parameters (@status) and
            // the table name are not columns.
            var unquoted = Regex.Matches(
                statement, $@"(?<![\w@{Regex.Escape(open)}])({bare})(?![\w{Regex.Escape(close)}])",
                RegexOptions.None, TimeSpan.FromSeconds(5));
            unquoted.Count.Should().Be(0,$"every column is quoted in: {statement}");
            statement.Should().Contain($"{open}Id{close}");
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Quoted_columns_text_is_the_unquoted_text_with_quotes_added(OutboxOrmDialect dialect)
    {
        var (open, close) = Quotes(dialect);
        var plain = All(new OutboxSql(dialect, "Outbox"));
        var quoted = All(new OutboxSql(dialect, "Outbox", quoteColumns: true));

        for (var i = 0; i < plain.Length; i++)
        {
            var stripped = quoted[i].Replace(open, string.Empty, StringComparison.Ordinal)
                .Replace(close, string.Empty, StringComparison.Ordinal);
            stripped.Should().Be(plain[i]);
            plain[i].Should().NotContain(open, "the default text has no quotes");
        }
    }

    [Fact]
    public void Quoted_aliases_and_returning_lists_use_the_dialects_style()
    {
        new OutboxSql(OutboxOrmDialect.Postgres, "T", quoteColumns: true).Claim.Should()
            .Contain("WHERE t.\"Id\" = c.\"Id\"")
            .And.Contain("RETURNING t.\"Id\", t.\"TypeName\", t.\"Payload\", t.\"RetryCount\", t.\"CreatedAt\"");
        new OutboxSql(OutboxOrmDialect.SqlServer, "T", quoteColumns: true).Claim.Should()
            .Contain("OUTPUT inserted.[Id], inserted.[TypeName], inserted.[Payload], inserted.[RetryCount], inserted.[CreatedAt]");
        new OutboxSql(OutboxOrmDialect.Sqlite, "T", quoteColumns: true).Claim.Should()
            .Contain("RETURNING \"Id\", \"TypeName\", \"Payload\", \"RetryCount\", \"CreatedAt\"");
        new OutboxSql(OutboxOrmDialect.Postgres, "T", quoteColumns: true).ReadState.Should()
            .Be("SELECT \"Status\", \"RetryCount\" FROM T WHERE \"Id\" = @id");
        new OutboxSql(OutboxOrmDialect.Postgres, "T").ReadState.Should()
            .Be("SELECT Status, RetryCount FROM T WHERE Id = @id");
    }
}
