using System.Reflection;
using Microsoft.AspNetCore.Components;
using ZeroAlloc.Outbox.Dashboard.Blazor;

namespace ZeroAlloc.Outbox.Tests;

public class BlazorComponentTests
{
    [Fact]
    public void AddOutboxDashboardBlazor_ReturnsServiceCollection()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var result = services.AddOutboxDashboardBlazor();
        Assert.Same(services, result);
    }

    [Fact]
    public void OutboxDashboard_HasBaseUrlParameter()
    {
        var property = typeof(OutboxDashboard).GetProperty(
            "BaseUrl",
            BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(property);
        Assert.Equal(typeof(string), property!.PropertyType);
        Assert.NotNull(property.GetCustomAttribute<ParameterAttribute>());
        Assert.NotNull(property.GetCustomAttribute<EditorRequiredAttribute>());
    }
}
