using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// Wraps another <see cref="IMigrationSource"/> and exposes only its first
/// migration, so a test can bring a database up to schema version 1 and then
/// separately apply the full source to exercise the upgrade path.
/// </summary>
/// <remarks>
/// It keeps the inner source's name: the history table scopes versions by source name, so the
/// full source finds migration 1 applied only when both record it under the same name.
/// </remarks>
internal sealed class FirstMigrationOnlySource(IMigrationSource inner) : IMigrationSource
{
    public string Name => inner.Name;

    public IReadOnlyList<Migration> GetMigrations() => [inner.GetMigrations()[0]];
}
