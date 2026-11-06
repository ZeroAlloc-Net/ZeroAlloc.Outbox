using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Outbox.Dashboard.Blazor;

namespace ZeroAlloc.Outbox.Tests;

public class BlazorComponentTests
{
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

    [Fact]
    public async Task Renders_an_iframe_at_the_base_url()
    {
        var html = await RenderAsync(new Dictionary<string, object?>(StringComparer.Ordinal) { ["BaseUrl"] = "/outbox" });

        Assert.Contains("<iframe", html, StringComparison.Ordinal);
        Assert.Contains("class=\"outbox-dashboard-frame\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"/outbox\"", html, StringComparison.Ordinal);
        Assert.Contains("title=\"Outbox Dashboard\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("pipeline=", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renders_the_default_base_url_when_none_is_given()
    {
        var html = await RenderAsync(new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.Contains("src=\"/outbox\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_named_pipeline_is_added_to_the_url_escaped()
    {
        var html = await RenderAsync(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["BaseUrl"] = "/ops/outbox",
            ["Pipeline"] = "billing & ledger",
        });

        // The renderer HTML-encodes the attribute, so the & of the query separator is not in play
        // here: the value is percent-escaped first.
        Assert.Contains("src=\"/ops/outbox?pipeline=billing%20%26%20ledger\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pipeline_joins_a_base_url_that_already_has_a_query_with_an_ampersand()
    {
        var html = await RenderAsync(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["BaseUrl"] = "/outbox?theme=dark",
            ["Pipeline"] = "billing",
        });

        Assert.Contains("src=\"/outbox?theme=dark&amp;pipeline=billing\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_pipeline_means_the_default_pipeline()
    {
        var html = await RenderAsync(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["BaseUrl"] = "/outbox",
            ["Pipeline"] = "",
        });

        Assert.Contains("src=\"/outbox\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("pipeline=", html, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(Dictionary<string, object?> parameters)
    {
        var services = new ServiceCollection().AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance).BuildServiceProvider();
        await using (services.ConfigureAwait(false))
        {
            var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
            await using (renderer.ConfigureAwait(false))
            {
                return await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    var output = await renderer.RenderComponentAsync<OutboxDashboard>(ParameterView.FromDictionary(parameters)).ConfigureAwait(false);
                    return output.ToHtmlString();
                }).ConfigureAwait(false);
            }
        }
    }
}
