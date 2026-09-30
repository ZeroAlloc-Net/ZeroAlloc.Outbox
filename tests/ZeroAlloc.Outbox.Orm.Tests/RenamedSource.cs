using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// Another source's migrations under a different name, standing in for the outbox schema as
/// it was recorded before its name was fixed.
/// </summary>
internal sealed class RenamedSource(IMigrationSource inner, string name) : IMigrationSource
{
    public string Name => name;

    public IReadOnlyList<Migration> GetMigrations() => inner.GetMigrations();
}
