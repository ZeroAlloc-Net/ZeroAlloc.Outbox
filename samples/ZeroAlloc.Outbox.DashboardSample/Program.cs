using System.Text;
using System.Text.Json;
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

// Seed fixture data so each dashboard tab renders with meaningful content.
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

    async Task<Guid> EnqueueAsync(string typeName, object payload)
    {
        var before = inMemory.AllEntries().Select(e => e.Id).ToHashSet();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await store.EnqueueAsync(typeName, bytes, null, CancellationToken.None);
        return inMemory.AllEntries().Select(e => e.Id).First(id => !before.Contains(id));
    }

    // ── Pending — fresh, awaiting first dispatch ───────────────────────────────
    // 12 messages across 5 realistic event types, demonstrating a warm queue.
    await EnqueueAsync("Commerce.OrderPlaced",            new { orderId = "ord_8a1f",      customerId = "cust_4421", total = 129.95m,  currency = "EUR" });
    await EnqueueAsync("Commerce.OrderPlaced",            new { orderId = "ord_8a20",      customerId = "cust_9013", total = 42.00m,   currency = "EUR" });
    await EnqueueAsync("Commerce.OrderPlaced",            new { orderId = "ord_8a21",      customerId = "cust_1122", total = 1_849.00m, currency = "USD" });
    await EnqueueAsync("Fulfilment.StockReserved",        new { orderId = "ord_8a1f",      warehouse = "ams-1", lines = 3 });
    await EnqueueAsync("Fulfilment.StockReserved",        new { orderId = "ord_8a21",      warehouse = "fra-2", lines = 7 });
    await EnqueueAsync("Billing.InvoiceIssued",           new { invoiceId = "inv_2026_0042", orderId = "ord_8a1f", amount = 129.95m });
    await EnqueueAsync("Billing.InvoiceIssued",           new { invoiceId = "inv_2026_0043", orderId = "ord_8a20", amount = 42.00m });
    await EnqueueAsync("Identity.UserRegistered",         new { userId = "usr_01K8P4", email = "alice@example.com",   source = "web" });
    await EnqueueAsync("Identity.UserRegistered",         new { userId = "usr_01K8P5", email = "bob@example.com",     source = "mobile" });
    await EnqueueAsync("Notifications.EmailQueued",       new { template = "welcome-v2", to = "alice@example.com",    locale = "en-US" });
    await EnqueueAsync("Notifications.EmailQueued",       new { template = "order-receipt", to = "alice@example.com", locale = "en-US" });
    await EnqueueAsync("Notifications.PushQueued",        new { deviceId = "dev_Ro3p", title = "Your order shipped",  priority = "normal" });

    // ── Retry queue — failed at least once, waiting for next attempt ──────────
    // 5 messages with varying retry counts showing the back-off pattern.
    async Task QueueRetryAsync(string typeName, object payload, int retryCount, int nextInMinutes)
    {
        var id = await EnqueueAsync(typeName, payload);
        await store.MarkFailedAsync(id, retryCount, DateTimeOffset.UtcNow.AddMinutes(nextInMinutes), CancellationToken.None);
    }

    await QueueRetryAsync("Billing.PaymentAuthorized", new { paymentId = "pmt_Q1m", orderId = "ord_8a22", amount = 89.00m },   retryCount: 1, nextInMinutes: 2);
    await QueueRetryAsync("Billing.PaymentAuthorized", new { paymentId = "pmt_Q1n", orderId = "ord_8a23", amount = 215.40m },  retryCount: 2, nextInMinutes: 5);
    await QueueRetryAsync("Notifications.SmsQueued",   new { to = "+31612345678",   body = "Your verification code is 842193" }, retryCount: 3, nextInMinutes: 12);
    await QueueRetryAsync("Integrations.WebhookSent",  new { url = "https://partner.example.com/hooks/orders", @event = "order.placed" }, retryCount: 2, nextInMinutes: 7);
    await QueueRetryAsync("Fulfilment.ShipmentDispatched", new { shipmentId = "shp_778", carrier = "PostNL", tracking = "3STBDN0142993" }, retryCount: 1, nextInMinutes: 1);

    // ── Dead-lettered — retries exhausted, requires human attention ────────────
    async Task DeadLetterAsync(string typeName, object payload, string error)
    {
        var id = await EnqueueAsync(typeName, payload);
        await store.DeadLetterAsync(id, error, CancellationToken.None);
    }

    await DeadLetterAsync("Billing.PaymentAuthorized",  new { paymentId = "pmt_P0x", orderId = "ord_7f99", amount = 1_459.00m }, "Gateway returned 502 after 5 retries");
    await DeadLetterAsync("Integrations.WebhookSent",   new { url = "https://legacy.partner.example.com/ingest", @event = "invoice.issued" }, "TLS handshake failed: certificate expired");
    await DeadLetterAsync("Notifications.EmailQueued",  new { template = "password-reset", to = "ghost@deleted.example.com" }, "SMTP 550: recipient mailbox does not exist");
    await DeadLetterAsync("Fulfilment.StockReserved",   new { orderId = "ord_7f41", warehouse = "mad-3", lines = 2 }, "Warehouse API unreachable; replaced by manual allocation");

    // ── Dispatched — successfully delivered within the history window ─────────
    // 25 succeeded messages across the same types, producing the throughput chart.
    string[] dispatchedTypes =
    [
        "Commerce.OrderPlaced",
        "Fulfilment.StockReserved",
        "Fulfilment.ShipmentDispatched",
        "Billing.InvoiceIssued",
        "Billing.PaymentAuthorized",
        "Billing.RefundInitiated",
        "Identity.UserRegistered",
        "Identity.PasswordChanged",
        "Notifications.EmailQueued",
        "Notifications.SmsQueued",
        "Integrations.WebhookSent",
        "Inventory.StockReplenished",
    ];

    for (var i = 0; i < 25; i++)
    {
        var type = dispatchedTypes[i % dispatchedTypes.Length];
        var id = await EnqueueAsync(type, new { sequenceId = i + 1, note = "historical fixture" });
        await store.MarkSucceededAsync(id, CancellationToken.None);
    }
}
