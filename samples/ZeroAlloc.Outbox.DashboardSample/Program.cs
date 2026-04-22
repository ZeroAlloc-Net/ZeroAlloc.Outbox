using System.Text;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Dashboard;
using ZeroAlloc.Outbox.InMemory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOutbox();
builder.Services.AddOutboxInMemory();
builder.Services.AddOutboxDashboardEvents();

// Remove the background worker so seeded state stays static during regression screenshots.
// (The worker would otherwise dead-letter the fixture messages because no IOutboxTypeDispatcher is registered.)
var workerDescriptor = builder.Services.First(d =>
    d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OutboxWorkerService));
builder.Services.Remove(workerDescriptor);

var app = builder.Build();

// Seed fixture data so each dashboard tab renders with content.
await SeedAsync(app.Services);

app.MapGet("/", () => Results.Redirect("/outbox/"));
app.MapOutboxDashboard("/outbox");

// Convenience endpoint: publish a synthetic SSE event (for SSE smoke-testing in the browser).
app.MapPost("/sample/publish", async (IOutboxDashboardEventPublisher pub, CancellationToken ct) =>
{
    await pub.PublishAsync(
        new MessageDispatchedEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, 1),
        ct);
    return Results.NoContent();
});

app.Run();

static async Task SeedAsync(IServiceProvider services)
{
    var store = services.GetRequiredService<IOutboxStore>();
    var inMemory = (InMemoryOutboxStore)store;

    async Task<Guid> EnqueueAsync(string typeName, string payloadJson)
    {
        var before = inMemory.AllEntries().Select(e => e.Id).ToHashSet();
        await store.EnqueueAsync(typeName, Encoding.UTF8.GetBytes(payloadJson), null, CancellationToken.None);
        // The single new entry is whichever Id wasn't present before.
        return inMemory.AllEntries().Select(e => e.Id).First(id => !before.Contains(id));
    }

    // 3 pending (fresh)
    for (var i = 0; i < 3; i++)
    {
        _ = await EnqueueAsync("Sample.OrderPlaced", $"{{\"orderId\":{1000 + i},\"total\":{42.50 + i}}}");
    }

    // 2 retry-queue
    for (var i = 0; i < 2; i++)
    {
        var id = await EnqueueAsync("Sample.EmailRequested", $"{{\"emailId\":\"email-{i}\"}}");
        await store.MarkFailedAsync(id, retryCount: 2, nextRetryAt: DateTimeOffset.UtcNow.AddMinutes(5 + i), CancellationToken.None);
    }

    // 2 dead-lettered
    for (var i = 0; i < 2; i++)
    {
        var id = await EnqueueAsync("Sample.PaymentFailed", $"{{\"paymentId\":\"pmt-{i}\"}}");
        await store.DeadLetterAsync(id, "Payment gateway timeout after 3 retries", CancellationToken.None);
    }

    // 5 dispatched
    for (var i = 0; i < 5; i++)
    {
        var id = await EnqueueAsync("Sample.NotificationSent", $"{{\"notifId\":\"notif-{i}\"}}");
        await store.MarkSucceededAsync(id, CancellationToken.None);
    }
}
