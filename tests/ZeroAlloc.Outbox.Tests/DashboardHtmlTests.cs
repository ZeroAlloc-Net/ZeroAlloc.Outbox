using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox.Dashboard;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public sealed class DashboardHtmlTests
{
    [Fact]
    public async Task Root_Returns_HtmlPage_WithBasePathSubstituted()
    {
        using var store = new InMemoryOutboxStore();
        using var host = await CreateHostAsync(store);
        var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/outbox/", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<title>Outbox Dashboard</title>", body, StringComparison.Ordinal);
        Assert.Contains("<base href=\"/outbox/\">", body, StringComparison.Ordinal);
        Assert.DoesNotContain("{{BASE_PATH}}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_Uses_NonDefaultBasePath_InBaseHref()
    {
        using var store = new InMemoryOutboxStore();
        using var host = await CreateHostAsync(store, "/admin/ops");
        var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/admin/ops/", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<base href=\"/admin/ops/\">", body, StringComparison.Ordinal);
        Assert.DoesNotContain("{{BASE_PATH}}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Css_Returns_TextCss()
    {
        using var store = new InMemoryOutboxStore();
        using var host = await CreateHostAsync(store);
        var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/outbox/outbox.css", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Js_Returns_ApplicationJavascript()
    {
        using var store = new InMemoryOutboxStore();
        using var host = await CreateHostAsync(store);
        var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/outbox/outbox.js", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/javascript", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<IHost> CreateHostAsync(InMemoryOutboxStore store, string basePath = "/outbox")
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(builder =>
            {
                builder.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSingleton<IOutboxStore>(store);
                        services.AddSingleton<IOutboxDashboardStore>(store);
                        services.AddOutboxDashboardEvents();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(e => e.MapOutboxDashboard(basePath));
                    });
            })
            .StartAsync().ConfigureAwait(false);
        return host;
    }
}
