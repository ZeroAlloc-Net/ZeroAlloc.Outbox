using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// The outbox table's column lengths are configured with <c>HasMaxLength</c> in
/// <c>AddOutboxMessages</c>, not with <c>[MaxLength]</c> attributes, because the attribute
/// is not trim-safe. These tests pin the lengths so the schema does not drift.
/// </summary>
public sealed class EfCoreOutboxModelTests
{
    private static IEntityType BuildEntityType()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        var options = new DbContextOptionsBuilder<DashboardTestDbContext>().UseSqlite(connection).Options;
        using var db = new DashboardTestDbContext(options);
        return db.Model.FindEntityType(typeof(OutboxMessageEntity))!;
    }

    [Theory]
    [InlineData(nameof(OutboxMessageEntity.TypeName), 256)]
    [InlineData(nameof(OutboxMessageEntity.LockedBy), 128)]
    [InlineData(nameof(OutboxMessageEntity.DeadLetterError), null)]
    [InlineData(nameof(OutboxMessageEntity.Payload), null)]
    public void Model_Keeps_The_Column_Max_Lengths(string property, int? expected)
    {
        BuildEntityType().FindProperty(property)!.GetMaxLength().Should().Be(expected);
    }

    [Fact]
    public void Entity_Has_No_MaxLength_Attributes()
    {
        typeof(OutboxMessageEntity).GetProperties()
            .Where(p => p.GetCustomAttribute<MaxLengthAttribute>() is not null)
            .Should().BeEmpty("lengths are configured in AddOutboxMessages; the attribute is not trim-safe");
    }
}
