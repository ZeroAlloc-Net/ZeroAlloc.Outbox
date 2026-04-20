using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ZeroAlloc.Outbox.Generator.Tests;

internal static class GeneratorTestHelper
{
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    private static IEnumerable<MetadataReference> GetReferences()
        => AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .Append(MetadataReference.CreateFromFile(typeof(ZeroAlloc.Outbox.OutboxMessageAttribute).Assembly.Location));

    public static (Compilation Output, IReadOnlyList<Diagnostic> Diagnostics) Run(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { syntaxTree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new OutboxGenerator();
        CSharpGeneratorDriver
            .Create(generator)
            .WithUpdatedParseOptions(ParseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var result = CSharpGeneratorDriver.Create(generator)
            .WithUpdatedParseOptions(ParseOptions)
            .RunGenerators(compilation)
            .GetRunResult();

        var allDiags = result.Diagnostics
            .Concat(diagnostics)
            .ToList();

        return (output, allDiags);
    }

    public static Task VerifyGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { syntaxTree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new OutboxGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .WithUpdatedParseOptions(ParseOptions)
            .RunGenerators(compilation);

        return VerifyXunit.Verifier.Verify(driver).UseDirectory("Snapshots");
    }
}
