using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZeroAlloc.Outbox.EfCore;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// How <c>WithOrm</c> behaves next to another store registration. A pipeline has one store, so a
/// different second store throws instead of silently replacing the first, and since the ORM store
/// uses the container's one connection, only one pipeline can use it. See ZeroAlloc.Outbox#206.
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
            .WithMessage("*WithOrm()*EfCoreOutboxStore<RegistrationDbContext>*AddOutbox(name, configure)*");
    }

    [Fact]
    public void WithEfCore_After_WithOrm_Throws()
    {
        var builder = new ServiceCollection().AddOutbox().WithOrm();

        var act = () => builder.WithEfCore<RegistrationDbContext>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithEfCore<RegistrationDbContext>()*OrmOutboxStore*AddOutbox(name, configure)*");
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
            .WithMessage("*WithOrm()*Postgres*SqlServer*AddOutbox(name, configure)*");
    }

    [Fact]
    public async Task WithOrm_On_A_Named_Pipeline_Registers_A_Keyed_Store()
    {
        await using var fx = new SqliteFixture();
        var connection = await fx.ConnectAsync();
        await using var _ = connection;
        var services = new ServiceCollection();
        services.AddSingleton(connection);

        services.AddOutbox("workflow", _ => { }).WithOrm(OutboxOrmDialect.Sqlite);

        services.Should().NotContain(d => !d.IsKeyedService && d.ServiceType == typeof(IOutboxStore));
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        scope.ServiceProvider.GetService<IOutboxStore>().Should().BeNull();
        scope.ServiceProvider.GetRequiredKeyedService<IOutboxStore>("workflow")
            .Should().BeSameAs(scope.ServiceProvider.GetRequiredKeyedService<OrmOutboxStore>("workflow"));
    }

    [Fact]
    public void WithOrm_On_A_Named_Pipeline_Next_To_A_Default_EfCore_Pipeline_Does_Not_Conflict()
    {
        var services = new ServiceCollection();
        services.AddOutbox().WithEfCore<RegistrationDbContext>();

        var act = () => services.AddOutbox("workflow", _ => { }).WithOrm(OutboxOrmDialect.Postgres);

        act.Should().NotThrow();
    }

    [Fact]
    public void WithOrm_Twice_On_A_Named_Pipeline_With_The_Same_Dialect_Keeps_One_Store()
    {
        var services = new ServiceCollection();

        services.AddOutbox("workflow", _ => { }).WithOrm(OutboxOrmDialect.Postgres);
        services.AddOutbox("workflow", _ => { }).WithOrm(OutboxOrmDialect.Postgres);

        services.Count(d => d.ServiceType == typeof(IOutboxStore)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(OrmOutboxStore)).Should().Be(1);
    }

    [Fact]
    public void WithOrm_Twice_On_A_Named_Pipeline_With_Different_Dialects_Throws()
    {
        var services = new ServiceCollection();
        services.AddOutbox("workflow", _ => { }).WithOrm(OutboxOrmDialect.Postgres);

        var act = () => services.AddOutbox("workflow", _ => { }).WithOrm(OutboxOrmDialect.SqlServer);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithOrm()*pipeline 'workflow'*Postgres*SqlServer*");
    }

    [Fact]
    public void WithOrm_On_A_Named_Pipeline_After_WithEfCore_On_The_Same_Pipeline_Throws()
    {
        var services = new ServiceCollection();
        services.AddOutbox("workflow", _ => { }).WithEfCore<RegistrationDbContext>();

        var act = () => services.AddOutbox("workflow", _ => { }).WithOrm();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithOrm()*pipeline 'workflow'*EfCoreOutboxStore<RegistrationDbContext>*");
    }

    [Fact]
    public void WithOrm_On_Two_Pipelines_Throws_Because_They_Would_Share_The_Connection()
    {
        var services = new ServiceCollection();
        services.AddOutbox().WithOrm(OutboxOrmDialect.Postgres);

        var act = () => services.AddOutbox("workflow", _ => { }).WithOrm(OutboxOrmDialect.Postgres);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WithOrm()*OrmOutboxStore*'workflow'*default outbox pipeline already uses it*IAsyncDbConnection*");
    }

    private sealed class RegistrationDbContext(DbContextOptions<RegistrationDbContext> options)
        : DbContext(options);
}
