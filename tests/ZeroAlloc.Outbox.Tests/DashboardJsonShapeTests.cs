using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox.Dashboard;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>Pins the raw JSON each dashboard endpoint writes, so serialization changes cannot drift it.</summary>
public sealed class DashboardJsonShapeTests
{
    private static async Task<IHost> CreateHostAsync(InMemoryOutboxStore store)
    {
        return await new HostBuilder()
            .ConfigureWebHost(builder =>
            {
                builder.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSingleton<IOutboxStore>(store);
                        services.AddSingleton<IOutboxDashboardStore>(store);
                        services.AddSingleton<IOutboxDashboardEventPublisher, ChannelOutboxDashboardEventPublisher>();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(e => e.MapOutboxDashboard("/outbox"));
                    });
            })
            .StartAsync().ConfigureAwait(false);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, HttpStatusCode expected)
    {
        var response = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.Clone();
    }

    [Fact]
    public async Task Pipelines_Writes_Name_And_Events_In_CamelCase()
    {
        using var store = new InMemoryOutboxStore();
        using var host = await CreateHostAsync(store);
        using var client = host.GetTestClient();

        var json = await GetJsonAsync(client, "/outbox/api/pipelines", HttpStatusCode.OK);

        json.GetArrayLength().Should().Be(1);
        json[0].GetProperty("name").ValueKind.Should().Be(JsonValueKind.Null);
        json[0].GetProperty("events").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Snapshot_Writes_Each_Group_And_Message_Fields_In_CamelCase()
    {
        using var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T1", new byte[] { 1 }, null, CancellationToken.None);
        using var host = await CreateHostAsync(store);
        using var client = host.GetTestClient();

        var json = await GetJsonAsync(client, "/outbox/api/snapshot", HttpStatusCode.OK);

        foreach (var group in new[] { "pending", "retryQueue", "deadLettered", "dispatched" })
            json.GetProperty(group).ValueKind.Should().Be(JsonValueKind.Array);
        var message = json.GetProperty("pending")[0];
        message.GetProperty("typeName").GetString().Should().Be("T1");
        message.GetProperty("id").ValueKind.Should().Be(JsonValueKind.String);
        message.GetProperty("id").GetString().Should().HaveLength(26, "a typed id serializes as its 26 character string");
        foreach (var field in new[] { "createdAt", "retryCount", "nextRetryAt", "payloadPreview" })
            message.TryGetProperty(field, out _).Should().BeTrue(field);
    }

    [Fact]
    public async Task Throughput_Writes_An_Array()
    {
        using var store = new InMemoryOutboxStore();
        using var host = await CreateHostAsync(store);
        using var client = host.GetTestClient();

        var json = await GetJsonAsync(client, "/outbox/api/throughput", HttpStatusCode.OK);

        json.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Unknown_Pipeline_Writes_An_Error_Body_With_404()
    {
        using var store = new InMemoryOutboxStore();
        using var host = await CreateHostAsync(store);
        using var client = host.GetTestClient();

        var json = await GetJsonAsync(client, "/outbox/api/snapshot?pipeline=nope", HttpStatusCode.NotFound);

        json.GetProperty("error").GetString().Should().Contain("nope");
    }

    [Fact]
    public async Task Rejected_Requeue_Writes_An_Error_Body_With_422()
    {
        using var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var lease = new OutboxLease("test-host", TimeSpan.FromMinutes(1));
        var id = (await store.ClaimPendingAsync(1, lease, CancellationToken.None))[0].Id;
        using var host = await CreateHostAsync(store);
        using var client = host.GetTestClient();

        var response = await client.PostAsync(new Uri($"/outbox/api/messages/{id}/requeue", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Sse_Data_Frame_Writes_The_Event_Fields_As_Json()
    {
        using var store = new InMemoryOutboxStore();
        using var host = await CreateHostAsync(store);
        using var client = host.GetTestClient();
        var publisher = host.Services.GetRequiredService<IOutboxDashboardEventPublisher>();
        var id = OutboxMessageId.New();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/outbox/api/events", UriKind.Relative));
        var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await publisher.PublishAsync(new MessageDeadLetteredEvent(id, "boom", 3), cts.Token).ConfigureAwait(false);
                    await Task.Delay(50, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
            }
        }, cts.Token);

        var response = await responseTask;
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token), Encoding.UTF8);
        string? line;
        string? data = null;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal)) { data = line["data: ".Length..]; break; }
        }

        data.Should().NotBeNull();
        var json = JsonDocument.Parse(data!).RootElement;
        json.GetProperty("Error").GetString().Should().Be("boom");
        json.GetProperty("TotalAttempts").GetInt32().Should().Be(3);
        json.GetProperty("Id").ValueKind.Should().Be(JsonValueKind.String);
        json.GetProperty("Id").GetString().Should().HaveLength(26);
        json.GetProperty("Id").GetString().Should().Be(id.ToString());
    }
}
