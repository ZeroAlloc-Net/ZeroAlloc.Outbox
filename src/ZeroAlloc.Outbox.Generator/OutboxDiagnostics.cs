using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Outbox.Generator;

internal static class OutboxDiagnostics
{
    private const string Category = "ZeroAlloc.Outbox";

    /// <summary>ZO0001: [OutboxMessage] applied to an interface.</summary>
    public static readonly DiagnosticDescriptor OutboxOnInterface = new(
        id: "ZO0001",
        title: "[OutboxMessage] on interface",
        messageFormat: "'{0}' is an interface. [OutboxMessage] must be applied to a class, record, or struct.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>ZO0002: [OutboxMessage] applied to a static class.</summary>
    public static readonly DiagnosticDescriptor OutboxOnStaticClass = new(
        id: "ZO0002",
        title: "[OutboxMessage] on static class",
        messageFormat: "'{0}' is static. [OutboxMessage] cannot be applied to a static class.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>ZO0003: [OutboxMessage] applied to a nested type.</summary>
    public static readonly DiagnosticDescriptor OutboxOnNestedType = new(
        id: "ZO0003",
        title: "[OutboxMessage] on nested type",
        messageFormat: "'{0}' is a nested type. [OutboxMessage] must be applied to a top-level type to ensure a stable type discriminator.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
