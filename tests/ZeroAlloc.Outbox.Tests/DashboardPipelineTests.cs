using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox.Dashboard;
using ZeroAlloc.Outbox.Dashboard.Blazor;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>
/// The dashboard's pipeline selector, ZeroAlloc.Outbox#256: <c>api/pipelines</c> lists the
/// pipelines with a dashboard store, and every API endpoint serves the pipeline its
/// <c>pipeline</c> query parameter names, or the default one without it.
/// </summary>
public sealed class DashboardPipelineTests
{
    [Fact]
    public async Task Pipelines_Lists_The_Default_And_Each_Named_Pipeline_With_A_Dashboard_Store()
    {
        using var host = await CreateHostAsync();
        using var client = host.GetTestClient();

        var pipelines = await client.GetFromJsonAsync<List<PipelineDto>>(new Uri("/outbox/api/pipelines", UriKind.Relative));

        pipelines.Should().BeEquivalentTo(new[]
        {
            new PipelineDto(null, true),
            new PipelineDto("workflow", true),
            new PipelineDto("quiet", false),
        }, o => o.WithStrictOrdering(), "the ORM-like pipeline without a dashboard store is left out");
    }

    [Fact]
    public async Task Snapshot_Serves_The_Selected_Pipeline_And_The_Default_Without_One()
    {
        using var host = await CreateHostAsync();
        using var client = host.GetTestClient();
        await EnqueueAsync(host.Services.GetRequiredService<InMemoryOutboxStore>(), "Default.Msg");
        await EnqueueAsync(host.Services.GetRequiredKeyedService<InMemoryOutboxStore>("workflow"), "Workflow.Msg");

        var byDefault = await client.GetFromJsonAsync<OutboxSnapshot>(new Uri("/outbox/api/snapshot", UriKind.Relative));
        var byName = await client.GetFromJsonAsync<OutboxSnapshot>(
            new Uri("/outbox/api/snapshot?pipeline=workflow", UriKind.Relative));

        byDefault!.Pending.Should().ContainSingle().Which.TypeName.Should().Be("Default.Msg");
        byName!.Pending.Should().ContainSingle().Which.TypeName.Should().Be("Workflow.Msg");
    }

    [Theory]
    [InlineData("/outbox/api/snapshot?pipeline=nope")]
    [InlineData("/outbox/api/throughput?pipeline=nope")]
    [InlineData("/outbox/api/snapshot?pipeline=bare")]
    public async Task A_Pipeline_Without_A_Dashboard_Store_Answers_404(string url)
    {
        using var host = await CreateHostAsync();
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri(url, UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancel_Acts_On_The_Selected_Pipelines_Store()
    {
        using var host = await CreateHostAsync();
        using var client = host.GetTestClient();
        var workflow = host.Services.GetRequiredKeyedService<InMemoryOutboxStore>("workflow");
        await EnqueueAsync(workflow, "Workflow.Msg");
        var id = workflow.AllEntries()[0].Id;

        var onDefault = await client.PostAsync(
            new Uri($"/outbox/api/messages/{id}/cancel", UriKind.Relative), content: null);
        var onWorkflow = await client.PostAsync(
            new Uri($"/outbox/api/messages/{id}/cancel?pipeline=workflow", UriKind.Relative), content: null);

        onDefault.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "the default store has no such message");
        onWorkflow.StatusCode.Should().Be(HttpStatusCode.NoContent);
        workflow.AllEntries().Should().BeEmpty("cancelling removes the message from the workflow store");
    }

    [Fact]
    public async Task Events_Streams_The_Selected_Pipelines_Publisher()
    {
        using var host = await CreateHostAsync();
        using var client = host.GetTestClient();
        var publisher = host.Services.GetRequiredKeyedService<IOutboxDashboardEventPublisher>("workflow");
        host.Services.GetRequiredService<IOutboxDashboardEventPublisher>().Should().NotBeSameAs(publisher);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/outbox/api/events?pipeline=workflow", UriKind.Relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        // Publish until the frame is read, since the handler subscribes some time after the request starts.
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await publisher.PublishAsync(new MessageCancelledEvent(OutboxMessageId.New()), cts.Token).ConfigureAwait(false);
                    await Task.Delay(50, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
            }
        }, cts.Token);

        using var response = await responseTask;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token), Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) is not null
               && !line.StartsWith("event: MessageCancelled", StringComparison.Ordinal))
        {
        }

        line.Should().NotBeNull("the frame published to the pipeline's own publisher is streamed");
    }

    [Fact]
    public async Task Events_For_A_Pipeline_Without_A_Publisher_Answers_404()
    {
        using var host = await CreateHostAsync();
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/outbox/api/events?pipeline=quiet", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_Page_Carries_The_Pipeline_Selector()
    {
        using var host = await CreateHostAsync();
        using var client = host.GetTestClient();

        var html = await client.GetStringAsync(new Uri("/outbox/", UriKind.Relative));
        var js = await client.GetStringAsync(new Uri("/outbox/outbox.js", UriKind.Relative));

        html.Should().Contain("id=\"pipeline-select\"");
        js.Should().Contain("api/pipelines");
    }

    [Fact]
    public void The_Blazor_Component_Takes_An_Optional_Pipeline()
    {
        var property = typeof(OutboxDashboard).GetProperty(nameof(OutboxDashboard.Pipeline));

        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(string));
        property.GetCustomAttributes(typeof(ParameterAttribute), inherit: false).Should().ContainSingle();
        property.GetCustomAttributes(typeof(EditorRequiredAttribute), inherit: false).Should().BeEmpty();
    }

    private static async Task EnqueueAsync(IOutboxStore store, string typeName)
        => await store.EnqueueAsync(typeName, new byte[] { 1 }, null, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// A default in-memory pipeline and a named one, both with dashboard events; a named one with
    /// a dashboard store but no events; and a named one whose store has no dashboard store.
    /// </summary>
    private static async Task<IHost> CreateHostAsync()
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(builder =>
            {
                builder.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddLogging();
                        services.AddOutbox().WithInMemoryStore().WithDashboardEvents();
                        services.AddOutbox("workflow", _ => { }).WithInMemoryStore().WithDashboardEvents();
                        services.AddOutbox("quiet", _ => { }).WithInMemoryStore();
                        services.AddOutbox("bare", _ => { });
                        services.AddKeyedSingleton<IOutboxStore>("bare", new InMemoryOutboxStore());

                        // These tests drive the dashboard, not the outbox workers, which would
                        // otherwise claim and dead-letter the rows the tests enqueue.
                        var outbox = typeof(OutboxWorkerService).Assembly;
                        for (var i = services.Count - 1; i >= 0; i--)
                        {
                            var d = services[i];
                            if (d.ServiceType == typeof(IHostedService) && !d.IsKeyedService
                                && (d.ImplementationType == typeof(OutboxWorkerService)
                                    || d.ImplementationFactory?.Method.DeclaringType?.Assembly == outbox))
                            {
                                services.RemoveAt(i);
                            }
                        }
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(e => e.MapOutboxDashboard("/outbox"));
                    });
            })
            .StartAsync().ConfigureAwait(false);
        return host;
    }

    private sealed record PipelineDto(string? Name, bool Events);
}
