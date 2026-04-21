using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox.Dashboard;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public sealed class SseEventsEndpointTests
{
    [Fact]
    public async Task Sse_Emits_EventFrame_WhenEventPublished()
    {
        var store = new InMemoryOutboxStore();
        using var host = await CreateHostAsync(store);
        using var client = host.GetTestClient();

        var publisher = host.Services.GetRequiredService<IOutboxDashboardEventPublisher>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/outbox/api/events", UriKind.Relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        // Give the subscriber time to register before publishing.
        await Task.Delay(100, cts.Token);

        await publisher.PublishAsync(
            new MessageDispatchedEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, 1),
            CancellationToken.None);

        var response = await responseTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        var sawEventName = false;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            if (line.StartsWith("event: MessageDispatched", StringComparison.Ordinal))
            {
                sawEventName = true;
                break;
            }
        }
        Assert.True(sawEventName, "Expected SSE frame 'event: MessageDispatched' was not observed.");
    }

    private static async Task<IHost> CreateHostAsync(InMemoryOutboxStore store)
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
                        app.UseEndpoints(e => e.MapOutboxDashboard("/outbox"));
                    });
            })
            .StartAsync().ConfigureAwait(false);
        return host;
    }
}
