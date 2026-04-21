using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox.Dashboard;

namespace ZeroAlloc.Outbox.Tests;

public class MapOutboxDashboardTests
{
    [Fact]
    public async Task RootRoute_Returns200_WithOkBody()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services => services.AddRouting())
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapOutboxDashboard("/outbox");
                        });
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();
        var response = await client.GetAsync(new Uri("/outbox/", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ok", body, StringComparison.OrdinalIgnoreCase);
    }
}
