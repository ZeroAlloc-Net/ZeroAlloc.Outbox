using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using ZeroAlloc.Outbox.Dashboard;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

/// <summary>Pins the raw JSON each dashboard endpoint writes, so serialization changes cannot drift it.</summary>
public sealed partial class DashboardJsonShapeTests
{
    private static async Task<IHost> CreateHostAsync(
        InMemoryOutboxStore store,
        Action<IEndpointRouteBuilder>? map = null,
        ILoggerProvider? logger = null)
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
                        if (logger is not null)
                            services.AddLogging(b => b.AddProvider(logger));
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(e => (map ?? (x => x.MapOutboxDashboard("/outbox")))(e));
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
        message.GetProperty("dispatchedAt").ValueKind.Should().Be(JsonValueKind.Null);
        message.GetProperty("deadLetterError").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Throughput_Writes_Bucket_Dispatched_And_Failed()
    {
        using var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var lease = new OutboxLease("test-host", TimeSpan.FromMinutes(1));
        var id = (await store.ClaimPendingAsync(1, lease, CancellationToken.None))[0].Id;
        (await store.MarkSucceededAsync(id, lease, CancellationToken.None)).Should().BeTrue();
        using var host = await CreateHostAsync(store);
        using var client = host.GetTestClient();

        var json = await GetJsonAsync(client, "/outbox/api/throughput", HttpStatusCode.OK);

        json.GetArrayLength().Should().Be(1);
        json[0].GetProperty("bucket").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
        json[0].GetProperty("dispatched").GetInt32().Should().Be(1);
        json[0].GetProperty("failed").GetInt32().Should().Be(0);
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

    /// <summary>A user-defined event.</summary>
    internal sealed record CustomDashboardEvent(OutboxMessageId Id, string Note) : OutboxDashboardEvent(Id);

    /// <summary>The host's source-generated metadata for its custom event, with default options.</summary>
    [JsonSerializable(typeof(CustomDashboardEvent))]
    internal sealed partial class CustomEventJsonContext : JsonSerializerContext
    {
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Collector(this);

        public void Dispose()
        {
        }

        private sealed class Collector(CollectingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries) owner.Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    /// <summary>Publishes the given events in a loop and returns the SSE lines until the stop condition holds.</summary>
    private static async Task<List<string>> StreamUntilAsync(
        IHost host,
        Func<OutboxDashboardEvent[]> events,
        Func<List<string>, bool> stop)
    {
        using var client = host.GetTestClient();
        var publisher = host.Services.GetRequiredService<IOutboxDashboardEventPublisher>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/outbox/api/events", UriKind.Relative));
        var responseTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    foreach (var e in events())
                        await publisher.PublishAsync(e, cts.Token).ConfigureAwait(false);
                    await Task.Delay(50, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
            }
        }, cts.Token);

        var response = await responseTask.ConfigureAwait(false);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), Encoding.UTF8);
        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)) != null)
        {
            lines.Add(line);
            if (stop(lines)) break;
        }
        cts.Cancel();
        return lines;
    }

    [Fact]
    public async Task Sse_Streams_A_Registered_Custom_Event_With_Its_Json()
    {
        using var store = new InMemoryOutboxStore();
        using var host = await CreateHostAsync(
            store,
            e => e.MapOutboxDashboard(o => o.EventTypeInfoResolver = CustomEventJsonContext.Default));

        var lines = await StreamUntilAsync(
            host,
            () => new OutboxDashboardEvent[] { new CustomDashboardEvent(OutboxMessageId.New(), "hello") },
            l => l.Exists(x => x.StartsWith("data: ", StringComparison.Ordinal)));

        lines.Should().Contain("event: CustomDashboard");
        var json = JsonDocument.Parse(lines.Last(x => x.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..]).RootElement;
        json.GetProperty("Note").GetString().Should().Be("hello");
        json.GetProperty("Id").GetString().Should().HaveLength(26);
    }

    [Fact]
    public async Task Sse_Skips_An_Unregistered_Custom_Event_Warns_Once_And_Keeps_Streaming()
    {
        using var store = new InMemoryOutboxStore();
        var logs = new CollectingLoggerProvider();
        using var host = await CreateHostAsync(store, logger: logs);

        var lines = await StreamUntilAsync(
            host,
            () => new OutboxDashboardEvent[]
            {
                new CustomDashboardEvent(OutboxMessageId.New(), "hello"),
                new MessageCancelledEvent(OutboxMessageId.New()),
            },
            l => l.Count(x => x.StartsWith("event: MessageCancelled", StringComparison.Ordinal)) >= 3);

        lines.Should().NotContain(x => x.Contains("CustomDashboard", StringComparison.Ordinal));
        string[] warnings;
        lock (logs.Entries)
            warnings = logs.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToArray();
        warnings.Should().ContainSingle("one warning per type, not one per event")
            .Which.Should().Contain(nameof(CustomDashboardEvent)).And.Contain("EventTypeInfoResolver");
    }

    [Fact]
    public async Task MapOutboxDashboard_Overloads_Bind_Without_Ambiguity()
    {
        using var store = new InMemoryOutboxStore();
        using var host = await new HostBuilder()
            .ConfigureWebHost(builder => builder.UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<IOutboxStore>(store);
                    services.AddSingleton<IOutboxDashboardStore>(store);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                    {
                        e.MapOutboxDashboard();
                        e.MapOutboxDashboard("/x");
                        e.MapOutboxDashboard(o => o.BasePath = "/y");
                    });
                }))
            .StartAsync();
        using var client = host.GetTestClient();

        foreach (var path in new[] { "/outbox", "/x", "/y" })
        {
            var response = await client.GetAsync(new Uri(path + "/api/pipelines", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public void MapOutboxDashboard_Options_Overload_Rejects_Null()
    {
        Action act = () => ((IEndpointRouteBuilder)null!).MapOutboxDashboard((Action<OutboxDashboardOptions>)null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
