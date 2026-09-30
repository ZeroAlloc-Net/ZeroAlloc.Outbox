using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// An application's own migrations, numbered from 1 like the outbox schema, for proving that
/// the two sources share one database without colliding. The DDL is plain enough to run on
/// SQLite, PostgreSQL and SQL Server alike.
/// </summary>
internal sealed class AppMigrationSource : IMigrationSource
{
    public const string SourceName = "Tests.App";

    public string Name => SourceName;

    public IReadOnlyList<Migration> GetMigrations() =>
    [
        new Migration(1, "create_orders", "CREATE TABLE Orders (Id INT NOT NULL PRIMARY KEY)"),
        new Migration(2, "create_customers", "CREATE TABLE Customers (Id INT NOT NULL PRIMARY KEY)"),
    ];
}
