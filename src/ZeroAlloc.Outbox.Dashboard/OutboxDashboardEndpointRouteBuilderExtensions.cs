using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

        // Stub: a health-check endpoint so the scaffold can be tested.
        // Real endpoints land in Tasks 8-11.
        group.MapGet("/", () => Results.Ok(new { status = "ok", dashboard = "outbox" }));

        return group;
    }
}
