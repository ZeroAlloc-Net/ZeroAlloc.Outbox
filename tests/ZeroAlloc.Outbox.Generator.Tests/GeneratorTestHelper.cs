using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ZeroAlloc.Outbox.Generator;

namespace ZeroAlloc.Outbox.Generator.Tests;

internal static class GeneratorTestHelper
{
    private static readonly MetadataReference[] s_references =
        Basic.Reference.Assemblies.Net80.References.All
            .Append(MetadataReference.CreateFromFile(
                typeof(ZeroAlloc.Outbox.OutboxMessageAttribute).Assembly.Location))
            .ToArray();

    public static (Compilation Output, System.Collections.Generic.IReadOnlyList<Diagnostic> Diagnostics) Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            s_references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new OutboxGenerator();
        CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        return (output, (System.Collections.Generic.IReadOnlyList<Diagnostic>)diagnostics);
    }

    public static System.Threading.Tasks.Task VerifyGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            s_references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new OutboxGenerator();
        var driver = CSharpGeneratorDriver.Create(generator)
            .RunGenerators(compilation);

        return Verifier.Verify(driver);
    }
}
