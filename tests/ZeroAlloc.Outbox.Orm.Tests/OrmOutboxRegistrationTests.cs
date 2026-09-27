using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// How <c>WithOrm</c> behaves next to another store registration. One container hosts one outbox
/// pipeline, so a different second store throws instead of silently replacing the first.
/// See ZeroAlloc.Outbox#206.
/// </summary>
public sealed class OrmOutboxRegistrationTests
{
    [Fact]
    public async Task WithOrm_Registers_The_Orm_Store()
    {
        await using var fx = new SqliteFixture();
        var connection = await fx.ConnectAsync();
        await using var _ = connection;
        var services = new ServiceCollection();
        services.AddSingleton(connection);

        services.AddOutbox().WithOrm();

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IOutboxStore>().Should().BeOfType<OrmOutboxStore>();
    }

    [Fact]
    public void WithOrm_After_WithEfCore_Throws()
    {
        var builder = new ServiceCollection().AddOutbox().WithEfCore<RegistrationDbContext>();

        var act = () => builder.WithOrm(OutboxOrmDialect.SqlServer);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithOrm()*EfCoreOutboxStore<RegistrationDbContext>*issues/206*");
    }

    [Fact]
    public void WithEfCore_After_WithOrm_Throws()
    {
        var builder = new ServiceCollection().AddOutbox().WithOrm();

        var act = () => builder.WithEfCore<RegistrationDbContext>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithEfCore<RegistrationDbContext>()*OrmOutboxStore*issues/206*");
    }

    [Fact]
    public void WithOrm_Twice_With_The_Same_Dialect_Keeps_One_Store()
    {
        var services = new ServiceCollection();

        services.AddOutbox().WithOrm(OutboxOrmDialect.Postgres).WithOrm(OutboxOrmDialect.Postgres);

        services.Count(d => d.ServiceType == typeof(IOutboxStore)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(OrmOutboxStore)).Should().Be(1);
    }

    [Fact]
    public void WithOrm_Twice_With_Different_Dialects_Throws()
    {
        var builder = new ServiceCollection().AddOutbox().WithOrm(OutboxOrmDialect.Postgres);

        var act = () => builder.WithOrm(OutboxOrmDialect.SqlServer);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithOrm()*Postgres*SqlServer*issues/206*");
    }

    private sealed class RegistrationDbContext(DbContextOptions<RegistrationDbContext> options)
        : DbContext(options);
}
