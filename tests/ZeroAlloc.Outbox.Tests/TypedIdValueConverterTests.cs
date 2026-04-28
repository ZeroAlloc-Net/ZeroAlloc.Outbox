using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// Verifies that <see cref="OutboxMessageEntity.Id"/> survives a write/read round-trip
/// through the <c>TypedIdValueConverter&lt;OutboxMessageId, Guid&gt;</c> registered by
/// <see cref="OutboxDbContextExtensions.AddOutboxMessages"/>.
/// </summary>
public sealed class TypedIdValueConverterTests : IAsyncLifetime
{
    private SqliteConnection _conn = default!;
    private DashboardTestDbContext _db = default!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        var opts = new DbContextOptionsBuilder<DashboardTestDbContext>()
            .UseSqlite(_conn)
            .Options;
        _db = new DashboardTestDbContext(opts);
        await _db.Database.EnsureCreatedAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync().ConfigureAwait(false);
        await _conn.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task EntityRoundTrip_PreservesOutboxMessageId()
    {
        var id = OutboxMessageId.New();
        _db.Set<OutboxMessageEntity>().Add(new OutboxMessageEntity
        {
            Id = id,
            TypeName = "Test",
            Payload = new byte[] { 0x01 },
        });
        await _db.SaveChangesAsync(CancellationToken.None);
        _db.ChangeTracker.Clear();

        var roundTripped = await _db.Set<OutboxMessageEntity>()
            .Where(e => e.Id == id)
            .FirstAsync(CancellationToken.None);

        roundTripped.Id.Should().Be(id);
    }
}
