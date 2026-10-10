namespace ZeroAlloc.Outbox.Dashboard;

/// <summary>One entry of <c>api/pipelines</c>. A null name is the default pipeline.</summary>
internal sealed record PipelineInfo(string? Name, bool Events);
