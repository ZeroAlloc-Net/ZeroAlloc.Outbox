using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// Wraps another <see cref="IMigrationSource"/> and exposes only its first
/// migration, so a test can bring a database up to schema version 1 and then
/// separately apply the full source to exercise the upgrade path.
/// </summary>
internal sealed class FirstMigrationOnlySource(IMigrationSource inner) : IMigrationSource
{
    public IReadOnlyList<Migration> GetMigrations() => [inner.GetMigrations()[0]];
}
