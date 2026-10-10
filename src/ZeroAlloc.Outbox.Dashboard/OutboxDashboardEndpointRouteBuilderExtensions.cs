using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
// ZeroAlloc.Results namespace enters the transitive closure when ZeroAlloc.Resilience is referenced
// (Outbox#20). Because this file lives in namespace ZeroAlloc.Outbox.Dashboard, C# parent-chain
// name lookup finds the namespace ZeroAlloc.Results before the type Microsoft.AspNetCore.Http.Results.
// All Results.* call sites are qualified with the global:: alias below to force the correct type.
using HttpResults = global::Microsoft.AspNetCore.Http.Results;

namespace ZeroAlloc.Outbox.Dashboard;

/// <summary>Adds the ZeroAlloc.Outbox dashboard endpoints to a <see cref="IEndpointRouteBuilder"/>.</summary>
public static class OutboxDashboardEndpointRouteBuilderExtensions
{
    /// <summary>Maps the outbox dashboard (HTML + REST + SSE) under the given base path.</summary>
    /// <remarks>
    /// Every API endpoint takes an optional <c>pipeline</c> query parameter naming a pipeline
    /// registered with <c>AddOutbox(name, configure)</c>; without it, the endpoint serves the
    /// default pipeline, as before. <c>api/pipelines</c> lists the pipelines that have an
    /// <see cref="IOutboxDashboardStore"/>, and the page shows a pipeline selector when there is
    /// more than one. A pipeline without a dashboard store answers 404.
    /// </remarks>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="basePath">Base path for all dashboard endpoints (e.g. "/outbox").</param>
    /// <returns>A <see cref="IEndpointConventionBuilder"/> for further configuration (auth, rate limits).</returns>
    public static IEndpointConventionBuilder MapOutboxDashboard(
        this IEndpointRouteBuilder endpoints,
        string basePath)
        => MapDashboard(endpoints, basePath, hostResolver: null);

    /// <summary>Maps the outbox dashboard under <c>/outbox</c>. See <see cref="MapOutboxDashboard(IEndpointRouteBuilder, string)"/>.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>A <see cref="IEndpointConventionBuilder"/> for further configuration (auth, rate limits).</returns>
    public static IEndpointConventionBuilder MapOutboxDashboard(this IEndpointRouteBuilder endpoints)
        => MapOutboxDashboard(endpoints, "/outbox");

    /// <summary>
    /// Maps the outbox dashboard like <see cref="MapOutboxDashboard(IEndpointRouteBuilder, string)"/>,
    /// with the settings in <see cref="OutboxDashboardOptions"/>, such as the JSON type info of the
    /// host's own <see cref="OutboxDashboardEvent"/> subclasses.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="configure">Sets the dashboard options.</param>
    /// <returns>A <see cref="IEndpointConventionBuilder"/> for further configuration (auth, rate limits).</returns>
    public static IEndpointConventionBuilder MapOutboxDashboard(
        this IEndpointRouteBuilder endpoints,
        Action<OutboxDashboardOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new OutboxDashboardOptions();
        configure(options);
        return MapDashboard(endpoints, options.BasePath, options.EventTypeInfoResolver);
    }

    private static RouteGroupBuilder MapDashboard(
        IEndpointRouteBuilder endpoints,
        string basePath,
        IJsonTypeInfoResolver? hostResolver)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(basePath);

        var group = endpoints.MapGroup(basePath);

        var normalisedBase = basePath.EndsWith("/", StringComparison.Ordinal) ? basePath : basePath + "/";

        // HTML shell + static assets (embedded resources).
        group.MapGet("/", (HttpContext ctx) => ServeHtmlAsync(ctx, normalisedBase));
        group.MapGet("/outbox.css", (HttpContext ctx) => ServeResourceAsync(ctx, "outbox.css", "text/css; charset=utf-8"));
        group.MapGet("/outbox.js", (HttpContext ctx) => ServeResourceAsync(ctx, "outbox.js", "application/javascript; charset=utf-8"));

        MapReadEndpoints(group, new DashboardEventTypeInfo(hostResolver));
        MapWriteEndpoints(group);

        return group;
    }

    private const string ResourcePrefix = "ZeroAlloc.Outbox.Dashboard.wwwroot.";

    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'";

    private static readonly byte[] SseKeepaliveBytes = Encoding.UTF8.GetBytes(": keepalive\n\n");

    private static readonly byte[] SseReadyBytes = Encoding.UTF8.GetBytes(": ready\n\n");

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

    private static void MapReadEndpoints(RouteGroupBuilder group, DashboardEventTypeInfo eventTypeInfo)
    {
        group.MapGet("/api/pipelines", GetPipelines);
        group.MapGet("/api/snapshot", GetSnapshotAsync);
        group.MapGet("/api/throughput", GetThroughputAsync);
        group.MapGet(
            "/api/events",
            (HttpContext ctx, string? pipeline, CancellationToken ct) =>
                StreamEventsAsync(ctx, pipeline, eventTypeInfo, ct));
    }

    /// <summary>
    /// The pipelines the dashboard can show: the default one when it has an unkeyed
    /// <see cref="IOutboxDashboardStore"/>, and each <see cref="NamedOutboxPipeline"/> with a keyed
    /// one. <c>events</c> says whether the pipeline has a dashboard event publisher to stream.
    /// </summary>
    private static IResult GetPipelines(HttpContext ctx)
    {
        var sp = ctx.RequestServices;
        var isService = sp.GetService<IServiceProviderIsService>();
        var isKeyed = sp.GetService<IServiceProviderIsKeyedService>();
        var pipelines = new List<PipelineInfo>();

        if (isService?.IsService(typeof(IOutboxDashboardStore)) == true)
            pipelines.Add(new PipelineInfo(null, isService.IsService(typeof(IOutboxDashboardEventPublisher))));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var named in sp.GetServices<NamedOutboxPipeline>())
        {
            if (seen.Add(named.Name) && isKeyed?.IsKeyedService(typeof(IOutboxDashboardStore), named.Name) == true)
            {
                pipelines.Add(new PipelineInfo(
                    named.Name, isKeyed.IsKeyedService(typeof(IOutboxDashboardEventPublisher), named.Name)));
            }
        }

        return HttpResults.Json(pipelines, DashboardJsonContext.Default.ListPipelineInfo);
    }

    /// <summary>True when <paramref name="pipeline"/> names the default pipeline.</summary>
    private static bool IsDefault(string? pipeline) => string.IsNullOrEmpty(pipeline);

    /// <summary>
    /// The dashboard store of <paramref name="pipeline"/>, or null when that named pipeline has
    /// none. The default pipeline's store is required, as it always was.
    /// </summary>
    private static IOutboxDashboardStore? ResolveStore(HttpContext ctx, string? pipeline)
        => IsDefault(pipeline)
            ? ctx.RequestServices.GetRequiredService<IOutboxDashboardStore>()
            : ctx.RequestServices.GetKeyedService<IOutboxDashboardStore>(pipeline);

    private static IOutboxDashboardEventPublisher? ResolvePublisher(HttpContext ctx, string? pipeline)
        => IsDefault(pipeline)
            ? ctx.RequestServices.GetService<IOutboxDashboardEventPublisher>()
            : ctx.RequestServices.GetKeyedService<IOutboxDashboardEventPublisher>(pipeline);

    private static IResult UnknownPipeline(string? pipeline)
        => HttpResults.Json(
            new ErrorBody($"The outbox pipeline '{pipeline}' has no dashboard store."),
            DashboardJsonContext.Default.ErrorBody,
            statusCode: StatusCodes.Status404NotFound);

    private static async Task StreamEventsAsync(
        HttpContext ctx,
        string? pipeline,
        DashboardEventTypeInfo eventTypeInfo,
        CancellationToken ct)
    {
        var publisher = IsDefault(pipeline)
            ? ctx.RequestServices.GetRequiredService<IOutboxDashboardEventPublisher>()
            : ctx.RequestServices.GetKeyedService<IOutboxDashboardEventPublisher>(pipeline);
        if (publisher is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx response buffering

        using var sub = publisher.Subscribe();
        var reader = sub.Reader;

        // Send an initial comment frame so the client's EventSource.onopen fires immediately
        // rather than waiting up to 15s for the first heartbeat.
        await ctx.Response.Body.WriteAsync(SseReadyBytes, ct).ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);

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

                await WriteEventFrameAsync(ctx, evt, eventTypeInfo, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — nothing to log.
        }
    }

    private static async Task WriteEventFrameAsync(
        HttpContext ctx,
        OutboxDashboardEvent evt,
        DashboardEventTypeInfo eventTypeInfo,
        CancellationToken ct)
    {
        var eventName = evt.GetType().Name;
        const string suffix = "Event";
        if (eventName.EndsWith(suffix, StringComparison.Ordinal))
            eventName = eventName[..^suffix.Length];

        var typeInfo = eventTypeInfo.Resolve(ctx, evt.GetType());
        if (typeInfo is null)
            return;
        var json = JsonSerializer.Serialize(evt, typeInfo);
        var frame = $"event: {eventName}\ndata: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(frame);
        await ctx.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void MapWriteEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/api/messages/{id}/requeue", RequeueAsync);
        group.MapPost("/api/messages/{id}/cancel", CancelAsync);
        group.MapPost("/api/messages/{id}/force-dispatch", ForceDispatchAsync);
    }

    private static async Task<IResult> GetSnapshotAsync(
        HttpContext ctx,
        string? pipeline,
        int? dispatchedLimit,
        CancellationToken ct)
    {
        if (ResolveStore(ctx, pipeline) is not { } store)
            return UnknownPipeline(pipeline);

        var snapshot = await store.GetSnapshotAsync(dispatchedLimit ?? 100, ct).ConfigureAwait(false);
        return HttpResults.Json(snapshot, DashboardJsonContext.Default.OutboxSnapshot);
    }

    private static async Task<IResult> GetThroughputAsync(
        HttpContext ctx,
        string? pipeline,
        int? windowMinutes,
        CancellationToken ct)
    {
        if (ResolveStore(ctx, pipeline) is not { } store)
            return UnknownPipeline(pipeline);

        var window = TimeSpan.FromMinutes(windowMinutes ?? 60);
        var points = new List<ThroughputPoint>();
        await foreach (var p in store.GetThroughputAsync(window, ct).ConfigureAwait(false))
        {
            points.Add(p);
        }
        return HttpResults.Json(points, DashboardJsonContext.Default.ListThroughputPoint);
    }

    private static async Task<IResult> RequeueAsync(
        OutboxMessageId id,
        HttpContext ctx,
        string? pipeline,
        CancellationToken ct)
    {
        if (ResolveStore(ctx, pipeline) is not { } store)
            return UnknownPipeline(pipeline);

        var publisher = ResolvePublisher(ctx, pipeline);
        try
        {
            await store.RequeueAsync(id, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return HttpResults.Json(
                new ErrorBody(ex.Message),
                DashboardJsonContext.Default.ErrorBody,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        await SafePublishAsync(publisher, new MessageRequeuedEvent(id, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        return HttpResults.NoContent();
    }

    private static async Task<IResult> CancelAsync(
        OutboxMessageId id,
        HttpContext ctx,
        string? pipeline,
        CancellationToken ct)
    {
        if (ResolveStore(ctx, pipeline) is not { } store)
            return UnknownPipeline(pipeline);

        var publisher = ResolvePublisher(ctx, pipeline);
        try
        {
            await store.CancelAsync(id, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return HttpResults.Json(
                new ErrorBody(ex.Message),
                DashboardJsonContext.Default.ErrorBody,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        await SafePublishAsync(publisher, new MessageCancelledEvent(id), ct).ConfigureAwait(false);
        return HttpResults.NoContent();
    }

    private static async Task<IResult> ForceDispatchAsync(
        OutboxMessageId id,
        HttpContext ctx,
        string? pipeline,
        CancellationToken ct)
    {
        if (ResolveStore(ctx, pipeline) is not { } store)
            return UnknownPipeline(pipeline);

        try
        {
            await store.ForceDispatchAsync(id, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return HttpResults.Json(
                new ErrorBody(ex.Message),
                DashboardJsonContext.Default.ErrorBody,
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        // No event — the worker publishes MessageDispatchedEvent once it picks the message up.
        return HttpResults.NoContent();
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Publisher errors are telemetry-only, and dashboard publish errors must not break write
            // endpoints; swallow so the API response still succeeds.
            _ = ex;
        }
    }
}
