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

    /// <summary>ZO0004: a generic [OutboxMessage] type has no closed construction the generator can see.</summary>
    public static readonly DiagnosticDescriptor OpenGenericWithoutClosedUsage = new(
        id: "ZO0004",
        title: "Generic [OutboxMessage] type has no closed usage",
        messageFormat: "'{0}' is generic, and no closed construction of it is used or declared in this assembly, so no outbox code is generated for it. Use IOutboxWriter<{1}<...>> with concrete type arguments, or declare one with [assembly: OutboxMessage(typeof({1}<...>))].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Outbox code is generated for concrete types only: the stored type name and the serializer are fixed per closed construction, such as Envelope<Order>, and creating them at run time would need reflection, which is not NativeAOT-safe. The generator emits code for every IOutboxWriter or IOutboxDispatcher of a closed construction in the source, and for every [assembly: OutboxMessage(typeof(...))] declaration.");

    /// <summary>ZO0005: the stored type name of a closed generic message is longer than the TypeName column.</summary>
    public static readonly DiagnosticDescriptor StoredTypeNameTooLong = new(
        id: "ZO0005",
        title: "Stored type name is longer than 256 characters",
        messageFormat: "The stored type name of '{0}' is {1} characters long, and outbox stores keep at most 256",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Each outbox row stores its message's fully qualified type name, such as App.Envelope<App.Order>, in a TypeName column of at most 256 characters. A longer name cannot be stored. Shorten the namespace or type names involved.");

    /// <summary>ZO0006: a closed generic outbox message cannot be generated as declared.</summary>
    public static readonly DiagnosticDescriptor InvalidClosedGeneric = new(
        id: "ZO0006",
        title: "[OutboxMessage] declaration of a closed generic type is invalid",
        messageFormat: "{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "[OutboxMessage] marks a top-level type declaration. [assembly: OutboxMessage(typeof(Envelope<Order>))] declares a closed construction of a generic type that is marked [OutboxMessage] in the same assembly. The generated code names the closed type, so every type in it must be accessible from the assembly.");

    /// <summary>ZO0007: a closed generic message would get the same generated names as another message.</summary>
    public static readonly DiagnosticDescriptor GeneratedNameCollision = new(
        id: "ZO0007",
        title: "Outbox message would get the same generated names as another message",
        messageFormat: "'{0}' would get the generated name {1}, which '{2}' already gets in namespace '{3}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A closed generic message's generated names are built from the simple names of the type and its type arguments, such as EnvelopeOfOrderOutboxWriter for Envelope<Order>, and a generic message type is registered by Add{Name}Outbox. Type arguments with the same simple name from different namespaces, or a generic and a non-generic message of the same name in one namespace, give the same names. Rename one of the types.");
}
