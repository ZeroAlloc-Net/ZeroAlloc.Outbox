using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZeroAlloc.Outbox.Generator;

[Generator]
public sealed class OutboxGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) =>
                    node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (ctx, ct) => TryParse(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(models, static (ctx, model) =>
        {
            bool hasErrors = false;
            foreach (var d in model.Diagnostics)
            {
                ctx.ReportDiagnostic(d);
                if (d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    hasErrors = true;
            }

            if (!hasErrors && !model.IsInterface && !model.IsStatic)
                OutboxCodeWriter.Write(ctx, model);
        });
    }

    private static OutboxModel? TryParse(
        GeneratorSyntaxContext ctx,
        System.Threading.CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol symbol)
            return null;

        if (!HasOutboxMessageAttribute(symbol))
            return null;

        var ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : symbol.ContainingNamespace.ToDisplayString();

        var fqn = symbol.ContainingNamespace.IsGlobalNamespace
            ? symbol.Name
            : symbol.ContainingNamespace.ToDisplayString() + "." + symbol.Name;

        var diagnostics = new System.Collections.Generic.List<Diagnostic>();

        Location? loc = null;
        foreach (var l in symbol.Locations) { loc = l; break; }

        if (symbol.TypeKind == TypeKind.Interface)
            diagnostics.Add(Diagnostic.Create(OutboxDiagnostics.OutboxOnInterface, loc, symbol.Name));

        if (symbol.IsStatic)
            diagnostics.Add(Diagnostic.Create(OutboxDiagnostics.OutboxOnStaticClass, loc, symbol.Name));

        if (symbol.ContainingType is not null)
            diagnostics.Add(Diagnostic.Create(OutboxDiagnostics.OutboxOnNestedType, loc, symbol.Name));

        return new OutboxModel(
            ns,
            symbol.Name,
            fqn,
            symbol.TypeKind == TypeKind.Interface,
            symbol.IsStatic || symbol.ContainingType is not null,
            System.Collections.Immutable.ImmutableArray.CreateRange(diagnostics));
    }

    private static bool HasOutboxMessageAttribute(INamedTypeSymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (string.Equals(
                    attr.AttributeClass?.ToDisplayString(),
                    "ZeroAlloc.Outbox.OutboxMessageAttribute",
                    System.StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
