using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZeroAlloc.Outbox.Generator;

[Generator]
public sealed class OutboxGenerator : IIncrementalGenerator
{
    private const string AttributeName = "ZeroAlloc.Outbox.OutboxMessageAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeName,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, ct) => TryParse(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(models, static (ctx, model) =>
        {
            foreach (var d in model.Diagnostics)
                ctx.ReportDiagnostic(d.ToDiagnostic());

            // A generic definition gets code per closed construction, below.
            if (model.IsEmitted && !model.IsGeneric)
                OutboxCodeWriter.Write(ctx, model);
        });

        // Closed constructions of generic messages: IOutboxWriter<Envelope<Order>> and
        // IOutboxDispatcher<Envelope<Order>> in the source, and [assembly: OutboxMessage(typeof(...))].
        var usages = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsOutboxInterfaceName(node),
                transform: static (ctx, ct) => ClosedMessageParser.ParseUsage(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        var declarations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeName,
                predicate: static (node, _) => node is CompilationUnitSyntax,
                transform: static (ctx, ct) => ClosedMessageParser.ParseDeclarations(ctx, ct))
            .SelectMany(static (messages, _) => messages.AsImmutableArray());

        var closed = models.Collect()
            .Combine(usages.Collect())
            .Combine(declarations.Collect());

        context.RegisterSourceOutput(closed, static (ctx, input) =>
        {
            var ((definitions, found), declared) = input;
            ClosedGenericEmitter.Emit(ctx, definitions, found.AddRange(declared));
        });
    }

    private static bool IsOutboxInterfaceName(SyntaxNode node)
        => node is GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } name
            && name.Identifier.ValueText is "IOutboxWriter" or "IOutboxDispatcher";

    private static OutboxModel? TryParse(
        GeneratorAttributeSyntaxContext ctx,
        System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetSymbol is not INamedTypeSymbol symbol)
            return null;

        // A partial type is one message however many of its declarations carry the attribute: only
        // the declaration with the first [OutboxMessage] in source order produces its model, so
        // its file is added once.
        if (FindOutboxMessageAttribute(symbol) is not { } attribute
            || attribute.ApplicationSyntaxReference is not { } reference
            || reference.SyntaxTree != ctx.TargetNode.SyntaxTree
            || !ctx.TargetNode.Span.Contains(reference.Span))
            return null;

        var ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : symbol.ContainingNamespace.ToDisplayString();

        var fqn = symbol.ContainingNamespace.IsGlobalNamespace
            ? symbol.Name
            : symbol.ContainingNamespace.ToDisplayString() + "." + symbol.Name;

        var diagnostics = new List<DiagnosticInfo>();

        Location? loc = null;
        foreach (var l in symbol.Locations) { loc = l; break; }
        var location = LocationInfo.From(loc);

        if (symbol.TypeKind == TypeKind.Interface)
            diagnostics.Add(new DiagnosticInfo(OutboxDiagnostics.OutboxOnInterface, location, symbol.Name));

        if (symbol.IsStatic)
            diagnostics.Add(new DiagnosticInfo(OutboxDiagnostics.OutboxOnStaticClass, location, symbol.Name));

        if (symbol.ContainingType is not null)
            diagnostics.Add(new DiagnosticInfo(OutboxDiagnostics.OutboxOnNestedType, location, symbol.Name));

        AddMisplacedTypeofForms(symbol, loc, diagnostics, ct);

        return new OutboxModel(
            ns,
            symbol.Name,
            fqn,
            HintNames.ForHost(symbol),
            symbol.ToDisplayString(),
            symbol.TypeParameters.Length > 0,
            symbol.TypeKind == TypeKind.Interface,
            symbol.IsStatic || symbol.ContainingType is not null,
            location,
            new EquatableArray<DiagnosticInfo>(diagnostics));
    }

    /// <summary>
    /// ZO0006 for every <c>[OutboxMessage(typeof(...))]</c> on a type declaration: that form declares a
    /// closed generic type and is valid only on the assembly.
    /// </summary>
    private static void AddMisplacedTypeofForms(
        INamedTypeSymbol symbol, Location? fallback, List<DiagnosticInfo> diagnostics, System.Threading.CancellationToken ct)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.ConstructorArguments.Length == 0
                || !string.Equals(attribute.AttributeClass?.ToDisplayString(), AttributeName, System.StringComparison.Ordinal))
                continue;
            diagnostics.Add(new DiagnosticInfo(
                OutboxDiagnostics.InvalidClosedGeneric,
                LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax(ct).GetLocation() ?? fallback),
                $"[OutboxMessage(typeof(...))] is valid only on the assembly; mark '{symbol.Name}' with [OutboxMessage] without arguments"));
        }
    }

    /// <summary>
    /// The first [OutboxMessage] on <paramref name="symbol"/> in source order, or null when it has
    /// none. The attributes of a partial type come from all of its declarations.
    /// </summary>
    internal static AttributeData? FindOutboxMessageAttribute(INamedTypeSymbol symbol)
    {
        AttributeData? first = null;
        foreach (var attr in symbol.GetAttributes())
        {
            if (!string.Equals(
                    attr.AttributeClass?.ToDisplayString(),
                    AttributeName,
                    System.StringComparison.Ordinal))
                continue;
            if (first is null || IsBefore(attr.ApplicationSyntaxReference, first.ApplicationSyntaxReference))
                first = attr;
        }
        return first;
    }

    private static bool IsBefore(SyntaxReference? candidate, SyntaxReference? current)
    {
        if (candidate is null) return false;
        if (current is null) return true;
        var byFile = string.CompareOrdinal(candidate.SyntaxTree.FilePath, current.SyntaxTree.FilePath);
        return byFile != 0 ? byFile < 0 : candidate.Span.Start < current.Span.Start;
    }
}
