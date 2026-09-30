using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ZeroAlloc.Outbox.Generator;

using ZeroAlloc.TestHelpers;

namespace ZeroAlloc.Outbox.Generator.Tests;

internal static class GeneratorTestHelper
{
    // Generated code is compiled, so the references must match the runtime ZeroAlloc.Outbox was
    // built against: the trusted platform assemblies plus the Outbox and DI assemblies.
    private static readonly MetadataReference[] s_runtimeReferences =
        ((string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Where(p => IsFrameworkAssembly(System.IO.Path.GetFileName(p)))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(
                typeof(ZeroAlloc.Outbox.OutboxMessageAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(
                typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location))
            .ToArray();

    private static bool IsFrameworkAssembly(string fileName)
        => fileName.StartsWith("System.", System.StringComparison.Ordinal)
            || string.Equals(fileName, "netstandard.dll", System.StringComparison.Ordinal)
            || string.Equals(fileName, "Microsoft.CSharp.dll", System.StringComparison.Ordinal);

    public static (Compilation Output, System.Collections.Generic.IReadOnlyList<Diagnostic> Diagnostics) Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            s_runtimeReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new OutboxGenerator();
        CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        return (output, (System.Collections.Generic.IReadOnlyList<Diagnostic>)diagnostics);
    }

    /// <summary>
    /// Runs the generator over <paramref name="source"/> with runtime references, so the output
    /// compilation can be checked for errors in the generated code.
    /// </summary>
    public static GeneratorRunResult RunCompiled(string source, out Compilation output)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            s_runtimeReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(new OutboxGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out output, out _);
        return driver.GetRunResult().Results[0];
    }

    public static void VerifyGenerator(string source)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            s_runtimeReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new OutboxGenerator();
        var driver = CSharpGeneratorDriver.Create(generator)
            .RunGenerators(compilation);

        GeneratorSnapshot.Verify(driver);
    }
}
