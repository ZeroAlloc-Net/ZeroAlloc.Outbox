using System;
using System.Collections.Generic;
using System.Data.Async;
using System.Data.Async.Adapters;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Mediator;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.AotSmoke.Adapters;
using ZeroAlloc.Outbox.Dashboard;
using ZeroAlloc.Outbox.InMemory;
using ZeroAlloc.Outbox.Mediator;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Outbox.Resilience;
using ZeroAlloc.Outbox.Telemetry;

// The adapter packages under PublishAot=true, with no trim or AOT suppression anywhere.
//
// 1. The ORM store over SQLite, with its schema applied by the ORM's MigrationRunner. Two messages
//    are written through the generated writers inside a DbTransaction the application owns. The
//    worker claims them from the ORM store and dispatches one through the Mediator bridge and one
//    through a proxy the Resilience generator emitted, which retries a first failure. Both go
//    through the Telemetry bridge's generated instrumentation, seen on outbox.dispatched_total.
// 2. The dashboard on Kestrel over the in-memory store: its HTML page and its JSON endpoints,
//    which are bound by the request delegate generator and serialized by source-generated
//    contexts.

try
{
    await OrmMediatorResilienceTelemetryAsync().ConfigureAwait(false);
    await DashboardAsync().ConfigureAwait(false);
}
catch (SmokeFailure failure)
{
    Console.Error.WriteLine($"AOT smoke (adapters): FAIL - {failure.Message}");
    return 1;
}

Console.WriteLine("AOT smoke (adapters): PASS");
return 0;

static async Task OrmMediatorResilienceTelemetryAsync()
{
    var connectionString = $"Data Source=outbox-aot-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    // A shared-cache in-memory database lives while any connection to it is open.
    var keepAlive = new SqliteConnection(connectionString);
    await keepAlive.OpenAsync().ConfigureAwait(false);
    await using var _ = keepAlive.ConfigureAwait(false);

    var migrationConnection = keepAlive.AsAsync();
    await new MigrationRunner(migrationConnection, OutboxOrmMigrations.Sqlite, new SqliteMigrationDialect())
        .RunAsync(CancellationToken.None).ConfigureAwait(false);

    var received = Channel.CreateUnbounded<string>();

    var dispatchedTotal = 0;
    using var meterListener = new MeterListener();
    meterListener.InstrumentPublished = (instrument, listener) =>
    {
        if (string.Equals(instrument.Name, "outbox.dispatched_total", StringComparison.Ordinal))
            listener.EnableMeasurementEvents(instrument);
    };
    meterListener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref dispatchedTotal, (int)value));
    meterListener.Start();

    using var host = BuildOrmHost(connectionString, received.Writer);
    await WriteInApplicationTransactionAsync(host.Services).ConfigureAwait(false);

    await host.StartAsync().ConfigureAwait(false);
    var delivered = new HashSet<string>(StringComparer.Ordinal);
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (delivered.Count < 2)
            delivered.Add(await received.Reader.ReadAsync(timeout.Token).ConfigureAwait(false));
    }
    catch (OperationCanceledException)
    {
        throw new SmokeFailure(
            $"the worker did not dispatch both ORM messages within 10 seconds; got {string.Join(", ", delivered)}");
    }
    finally
    {
        await host.StopAsync().ConfigureAwait(false);
    }

    if (!delivered.SetEquals(["shipped:ord-1", "paid:ord-2"]))
        throw new SmokeFailure($"expected shipped:ord-1 and paid:ord-2, got {string.Join(", ", delivered)}");

    meterListener.RecordObservableInstruments();
    if (Volatile.Read(ref dispatchedTotal) < 2)
        throw new SmokeFailure($"expected the telemetry proxy to count both dispatches, counted {dispatchedTotal}");
}

static IHost BuildOrmHost(string connectionString, ChannelWriter<string> received)
{
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Services.AddLogging();
    builder.Services.AddSerializerDispatcher();
    builder.Services.AddScoped<IAsyncDbConnection>(_ => new SqliteConnection(connectionString).AsAsync());
    builder.Services.AddSingleton(received);
    builder.Services.AddTransient<INotificationHandler<OrderShipped>, OrderShippedHandler>();
    builder.Services.AddTransient<IResilientOrderPaidDispatcher, FlakyOrderPaidDispatcher>();
    builder.Services.AddResilientOrderPaidDispatcherResiliencePolicies();
    builder.Services
        .AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
        .WithOrm()
        .AddOrderShippedOutbox()
        .AddOrderPaidOutbox()
        .WithMediator<OrderShipped>()
        .WithResilience<OrderPaid, IResilientOrderPaidDispatcher, IResilientOrderPaidDispatcherResilienceProxy>()
        .WithTelemetry();

    return builder.Build();
}

/// <summary>
/// Writes one message of each type through the generated writers, inside a transaction the
/// application owns on the scope's connection; the outbox rows commit with it.
/// </summary>
static async Task WriteInApplicationTransactionAsync(IServiceProvider services)
{
    var scope = services.CreateAsyncScope();
    await using (scope.ConfigureAwait(false))
    {
        var connection = (DbConnection)(AdapterDbConnection)scope.ServiceProvider.GetRequiredService<IAsyncDbConnection>();
        await connection.OpenAsync().ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await scope.ServiceProvider.GetRequiredService<IOutboxWriter<OrderShipped>>()
                .WriteAsync(new OrderShipped("ord-1"), transaction, CancellationToken.None).ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<IOutboxWriter<OrderPaid>>()
                .WriteAsync(new OrderPaid("ord-2"), transaction, CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }
    }
}

static async Task DashboardAsync()
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSerializerDispatcher();
    builder.Services.AddOutbox().WithInMemoryStore().WithDashboardEvents();

    var app = builder.Build();
    await using var _ = app.ConfigureAwait(false);
    app.MapOutboxDashboard();
    await app.StartAsync().ConfigureAwait(false);
    try
    {
        var store = app.Services.GetRequiredService<InMemoryOutboxStore>();
        await store.EnqueueAsync("Smoke.Dashboard", new byte[] { 1 }, transaction: null, CancellationToken.None)
            .ConfigureAwait(false);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };

        var page = await GetAsync(http, "/outbox/", "text/html").ConfigureAwait(false);
        if (!page.Contains("<html", StringComparison.OrdinalIgnoreCase))
            throw new SmokeFailure("the dashboard page is not HTML");

        var snapshot = await GetAsync(http, "/outbox/api/snapshot", "application/json").ConfigureAwait(false);
        if (!snapshot.Contains("Smoke.Dashboard", StringComparison.Ordinal))
            throw new SmokeFailure($"the snapshot does not list the queued message: {snapshot}");

        var pipelines = await GetAsync(http, "/outbox/api/pipelines", "application/json").ConfigureAwait(false);
        if (!pipelines.StartsWith('['))
            throw new SmokeFailure($"api/pipelines did not return a JSON array: {pipelines}");
    }
    finally
    {
        await app.StopAsync().ConfigureAwait(false);
    }
}

static async Task<string> GetAsync(HttpClient http, string path, string mediaType)
{
    using var response = await http.GetAsync(path).ConfigureAwait(false);
    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    if (!response.IsSuccessStatusCode)
        throw new SmokeFailure($"GET {path} answered {(int)response.StatusCode}: {body}");
    if (!string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.Ordinal))
        throw new SmokeFailure($"GET {path} answered {response.Content.Headers.ContentType}, expected {mediaType}");
    return body;
}
