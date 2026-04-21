using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox.Dashboard;
using ZeroAlloc.Outbox.InMemory;

namespace ZeroAlloc.Outbox.Tests;

public sealed class DashboardRestEndpointsTests
{
    private static async Task<(IHost Host, HttpClient Client)> CreateClientAsync(InMemoryOutboxStore store)
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

        return (host, host.GetTestClient());
    }

    [Fact]
    public async Task Snapshot_Returns200_WithExpectedShape()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T1", new byte[] { 1 }, null, CancellationToken.None);

        var (host, client) = await CreateClientAsync(store);
        using (host)
        using (client)
        {
            var response = await client.GetAsync(new Uri("/outbox/api/snapshot", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var snapshot = await response.Content.ReadFromJsonAsync<OutboxSnapshot>();
            Assert.NotNull(snapshot);
            snapshot!.Pending.Should().HaveCount(1);
            Assert.Equal("T1", snapshot.Pending[0].TypeName);
        }
    }

    [Fact]
    public async Task Throughput_Returns200_WithEmptyArray_WhenNoDispatch()
    {
        var store = new InMemoryOutboxStore();

        var (host, client) = await CreateClientAsync(store);
        using (host)
        using (client)
        {
            var response = await client.GetAsync(new Uri("/outbox/api/throughput?windowMinutes=60", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var points = await response.Content.ReadFromJsonAsync<List<ThroughputPoint>>();
            Assert.NotNull(points);
            Assert.Empty(points!);
        }
    }

    [Fact]
    public async Task Requeue_Returns204_ForDeadLetteredMessage()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;
        await store.MarkFailedAsync(id, 3, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.DeadLetterAsync(id, "boom", CancellationToken.None);

        var (host, client) = await CreateClientAsync(store);
        using (host)
        using (client)
        {
            var response = await client.PostAsync(
                new Uri($"/outbox/api/messages/{id}/requeue", UriKind.Relative),
                content: null);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
    }

    [Fact]
    public async Task Requeue_Returns422_ForPendingMessage()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;

        var (host, client) = await CreateClientAsync(store);
        using (host)
        using (client)
        {
            var response = await client.PostAsync(
                new Uri($"/outbox/api/messages/{id}/requeue", UriKind.Relative),
                content: null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }
    }

    [Fact]
    public async Task Cancel_Returns204_ForPendingMessage()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;

        var (host, client) = await CreateClientAsync(store);
        using (host)
        using (client)
        {
            var response = await client.PostAsync(
                new Uri($"/outbox/api/messages/{id}/cancel", UriKind.Relative),
                content: null);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Empty(store.AllEntries());
        }
    }

    [Fact]
    public async Task Cancel_Returns422_ForDispatchedMessage()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;
        await store.MarkSucceededAsync(id, CancellationToken.None);

        var (host, client) = await CreateClientAsync(store);
        using (host)
        using (client)
        {
            var response = await client.PostAsync(
                new Uri($"/outbox/api/messages/{id}/cancel", UriKind.Relative),
                content: null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }
    }

    [Fact]
    public async Task ForceDispatch_Returns204_ForPendingMessage()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;
        await store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        var (host, client) = await CreateClientAsync(store);
        using (host)
        using (client)
        {
            var response = await client.PostAsync(
                new Uri($"/outbox/api/messages/{id}/force-dispatch", UriKind.Relative),
                content: null);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            var entry = store.AllEntries().First(e => e.Id == id);
            Assert.True(entry.NextRetryAt <= DateTimeOffset.UtcNow.AddSeconds(1));
        }
    }

    [Fact]
    public async Task ForceDispatch_Returns422_ForDispatchedMessage()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;
        await store.MarkSucceededAsync(id, CancellationToken.None);

        var (host, client) = await CreateClientAsync(store);
        using (host)
        using (client)
        {
            var response = await client.PostAsync(
                new Uri($"/outbox/api/messages/{id}/force-dispatch", UriKind.Relative),
                content: null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }
    }

    [Fact]
    public async Task Requeue_PublishesMessageRequeuedEvent()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;
        await store.MarkFailedAsync(id, 3, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.DeadLetterAsync(id, "boom", CancellationToken.None);

        using var host = await new HostBuilder()
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
            .StartAsync();

        var publisher = host.Services.GetRequiredService<IOutboxDashboardEventPublisher>();
        using var sub = publisher.Subscribe();

        using var client = host.GetTestClient();
        var response = await client.PostAsync(
            new Uri($"/outbox/api/messages/{id}/requeue", UriKind.Relative),
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var evt = await sub.Reader.ReadAsync(cts.Token);
        Assert.IsType<MessageRequeuedEvent>(evt);
    }

    [Fact]
    public async Task Cancel_PublishesMessageCancelledEvent()
    {
        var store = new InMemoryOutboxStore();
        await store.EnqueueAsync("T", new byte[] { 1 }, null, CancellationToken.None);
        var id = store.AllEntries()[0].Id;

        using var host = await new HostBuilder()
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
            .StartAsync();

        var publisher = host.Services.GetRequiredService<IOutboxDashboardEventPublisher>();
        using var sub = publisher.Subscribe();

        using var client = host.GetTestClient();
        var response = await client.PostAsync(
            new Uri($"/outbox/api/messages/{id}/cancel", UriKind.Relative),
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var evt = await sub.Reader.ReadAsync(cts.Token);
        Assert.IsType<MessageCancelledEvent>(evt);
    }
}
