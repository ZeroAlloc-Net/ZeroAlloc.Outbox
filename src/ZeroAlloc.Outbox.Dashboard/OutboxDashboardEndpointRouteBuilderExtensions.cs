using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace ZeroAlloc.Outbox.Dashboard;

/// <summary>Adds the ZeroAlloc.Outbox dashboard endpoints to a <see cref="IEndpointRouteBuilder"/>.</summary>
public static class OutboxDashboardEndpointRouteBuilderExtensions
{
    /// <summary>Maps the outbox dashboard (HTML + REST + SSE) under the given base path.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="basePath">Base path for all dashboard endpoints (e.g. "/outbox").</param>
    /// <returns>A <see cref="IEndpointConventionBuilder"/> for further configuration (auth, rate limits).</returns>
    public static IEndpointConventionBuilder MapOutboxDashboard(
        this IEndpointRouteBuilder endpoints,
        string basePath = "/outbox")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(basePath);

        var group = endpoints.MapGroup(basePath);

        var normalisedBase = basePath.EndsWith("/", StringComparison.Ordinal) ? basePath : basePath + "/";

        // HTML shell + static assets (embedded resources).
        group.MapGet("/", (HttpContext ctx) => ServeHtmlAsync(ctx, normalisedBase));
        group.MapGet("/outbox.css", (HttpContext ctx) => ServeResourceAsync(ctx, "outbox.css", "text/css; charset=utf-8"));
        group.MapGet("/outbox.js", (HttpContext ctx) => ServeResourceAsync(ctx, "outbox.js", "application/javascript; charset=utf-8"));

        MapReadEndpoints(group);
        MapWriteEndpoints(group);

        return group;
    }

    private const string ResourcePrefix = "ZeroAlloc.Outbox.Dashboard.wwwroot.";

    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'";

    private static readonly byte[] SseKeepaliveBytes = Encoding.UTF8.GetBytes(": keepalive\n\n");

    private static readonly TimeSpan SseHeartbeatInterval = TimeSpan.FromSeconds(15);

    private static async Task ServeHtmlAsync(HttpContext ctx, string basePath)
    {
        var asm = typeof(OutboxDashboardEndpointRouteBuilderExtensions).Assembly;
        var resourceName = ResourcePrefix + "outbox.html";
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded resource '" + resourceName + "' not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var html = await reader.ReadToEndAsync(ctx.RequestAborted).ConfigureAwait(false);
        html = html.Replace("{{BASE_PATH}}", basePath, StringComparison.Ordinal);

        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        await ctx.Response.WriteAsync(html, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task ServeResourceAsync(HttpContext ctx, string fileName, string contentType)
    {
        var asm = typeof(OutboxDashboardEndpointRouteBuilderExtensions).Assembly;
        var resourceName = ResourcePrefix + fileName;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded resource '" + resourceName + "' not found.");

        ctx.Response.ContentType = contentType;
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
    }

    private static void MapReadEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/api/snapshot", GetSnapshotAsync);
        group.MapGet("/api/throughput", GetThroughputAsync);
        group.MapGet("/api/events", StreamEventsAsync);
    }

    private static async Task StreamEventsAsync(
        HttpContext ctx,
        [FromServices] IOutboxDashboardEventPublisher publisher,
        CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";

        using var sub = publisher.Subscribe();
        var reader = sub.Reader;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var readTask = reader.ReadAsync(ct).AsTask();
                var heartbeatTask = Task.Delay(SseHeartbeatInterval, ct);
                var completed = await Task.WhenAny(readTask, heartbeatTask).ConfigureAwait(false);

                if (completed == heartbeatTask)
                {
                    // Heartbeat fired first — emit keepalive comment so idle
                    // intermediaries (nginx, AWS ALB, etc.) don't drop the connection.
                    await ctx.Response.Body.WriteAsync(SseKeepaliveBytes, ct).ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }

                OutboxDashboardEvent evt;
                try
                {
                    evt = await readTask.ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                var eventName = evt.GetType().Name;
                const string suffix = "Event";
                if (eventName.EndsWith(suffix, StringComparison.Ordinal))
                    eventName = eventName[..^suffix.Length];

                var json = JsonSerializer.Serialize(evt, evt.GetType());
                var frame = $"event: {eventName}\ndata: {json}\n\n";
                var bytes = Encoding.UTF8.GetBytes(frame);
                await ctx.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — nothing to log.
        }
    }

    private static void MapWriteEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/api/messages/{id:guid}/requeue", RequeueAsync);
        group.MapPost("/api/messages/{id:guid}/cancel", CancelAsync);
        group.MapPost("/api/messages/{id:guid}/force-dispatch", ForceDispatchAsync);
    }

    private static async Task<IResult> GetSnapshotAsync(
        [FromServices] IOutboxDashboardStore store,
        int? dispatchedLimit,
        CancellationToken ct)
    {
        var snapshot = await store.GetSnapshotAsync(dispatchedLimit ?? 100, ct).ConfigureAwait(false);
        return Results.Ok(snapshot);
    }

    private static async Task<IResult> GetThroughputAsync(
        [FromServices] IOutboxDashboardStore store,
        int? windowMinutes,
        CancellationToken ct)
    {
        var window = TimeSpan.FromMinutes(windowMinutes ?? 60);
        var points = new List<ThroughputPoint>();
        await foreach (var p in store.GetThroughputAsync(window, ct).ConfigureAwait(false))
        {
            points.Add(p);
        }
        return Results.Ok(points);
    }

    private static async Task<IResult> RequeueAsync(
        Guid id,
        [FromServices] IOutboxDashboardStore store,
        [FromServices] IOutboxDashboardEventPublisher? publisher,
        CancellationToken ct)
    {
        try
        {
            await store.RequeueAsync(id, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Results.UnprocessableEntity(new { error = ex.Message });
        }

        await SafePublishAsync(publisher, new MessageRequeuedEvent(id, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> CancelAsync(
        Guid id,
        [FromServices] IOutboxDashboardStore store,
        [FromServices] IOutboxDashboardEventPublisher? publisher,
        CancellationToken ct)
    {
        try
        {
            await store.CancelAsync(id, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Results.UnprocessableEntity(new { error = ex.Message });
        }

        await SafePublishAsync(publisher, new MessageCancelledEvent(id), ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> ForceDispatchAsync(
        Guid id,
        [FromServices] IOutboxDashboardStore store,
        CancellationToken ct)
    {
        try
        {
            await store.ForceDispatchAsync(id, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Results.UnprocessableEntity(new { error = ex.Message });
        }

        // No event — the worker publishes MessageDispatchedEvent once it picks the message up.
        return Results.NoContent();
    }

    private static async ValueTask SafePublishAsync(
        IOutboxDashboardEventPublisher? publisher,
        OutboxDashboardEvent evt,
        CancellationToken ct)
    {
        if (publisher is null) return;
        try
        {
            await publisher.PublishAsync(evt, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // intentional broad catch — dashboard publish errors must not break write endpoints
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // Publisher errors are telemetry-only; swallow so the API response still succeeds.
            _ = ex;
        }
    }
}
