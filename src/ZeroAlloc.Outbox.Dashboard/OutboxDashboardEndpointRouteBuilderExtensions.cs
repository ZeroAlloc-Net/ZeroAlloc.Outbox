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

        // Health-check / placeholder for the HTML shell (lands in a later task).
        group.MapGet("/", () => Results.Ok(new { status = "ok", dashboard = "outbox" }));

        MapReadEndpoints(group);
        MapWriteEndpoints(group);

        return group;
    }

    private static void MapReadEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/api/snapshot", GetSnapshotAsync);
        group.MapGet("/api/throughput", GetThroughputAsync);
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
