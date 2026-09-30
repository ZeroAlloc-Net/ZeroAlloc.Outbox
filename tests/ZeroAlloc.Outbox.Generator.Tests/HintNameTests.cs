using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ZeroAlloc.Outbox.Generator.Tests;

public sealed class HintNameTests
{
    // The generated code is compiled, so the references must match the runtime ZeroAlloc.Outbox
    // was built against: the trusted platform assemblies plus the Outbox and DI assemblies.
    private static readonly MetadataReference[] s_references =
        ((string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Where(p => System.IO.Path.GetFileName(p).StartsWith("System.", System.StringComparison.Ordinal)
                || string.Equals(System.IO.Path.GetFileName(p), "netstandard.dll", System.StringComparison.Ordinal))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(
                typeof(ZeroAlloc.Outbox.OutboxMessageAttribute).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(
                typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location))
            .ToArray();

    private static GeneratorRunResult RunGenerator(string source, out Compilation output)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { CSharpSyntaxTree.ParseText(source) },
            s_references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new OutboxGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out output, out _);
        return driver.GetRunResult().Results[0];
    }

    private static string[] HintNames(GeneratorRunResult result)
        => result.GeneratedSources.Select(s => s.HintName).OrderBy(h => h, System.StringComparer.Ordinal).ToArray();

    [Fact]
    public void Underscore_in_namespace_and_type_name_does_not_collide()
    {
        var result = RunGenerator("""
            using ZeroAlloc.Outbox;

            namespace A_B
            {
                [OutboxMessage]
                public sealed record C(int Id);
            }

            namespace A
            {
                [OutboxMessage]
                public sealed record B_C(int Id);
            }
            """, out var output);

        result.Exception.Should().BeNull();
        HintNames(result).Should().Equal("A.B_C.Outbox.g.cs", "A_B.C.Outbox.g.cs");
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Generic_and_non_generic_types_of_the_same_name_get_distinct_hint_names()
    {
        var result = RunGenerator("""
            using ZeroAlloc.Outbox;

            namespace App
            {
                [OutboxMessage]
                public sealed record Foo(int Id);

                [OutboxMessage]
                public sealed record Foo<T>(T Value);
            }
            """, out _);

        result.Exception.Should().BeNull();
        HintNames(result).Should().Equal("App.Foo.Outbox.g.cs", "App.Foo`1.Outbox.g.cs");
    }

    [Fact]
    public void Type_in_the_global_namespace_has_no_namespace_part()
    {
        var result = RunGenerator("""
            using ZeroAlloc.Outbox;

            [OutboxMessage]
            public sealed record Ping(int Id);
            """, out _);

        HintNames(result).Should().Equal("Ping.Outbox.g.cs");
    }

    [Fact]
    public void Nested_namespace_is_joined_with_dots()
    {
        var result = RunGenerator("""
            using ZeroAlloc.Outbox;

            namespace App.Orders;

            [OutboxMessage]
            public sealed record Placed(int Id);
            """, out _);

        HintNames(result).Should().Equal("App.Orders.Placed.Outbox.g.cs");
    }

    [Theory]
    [InlineData("App.Foo", "App.Foo")]
    [InlineData("App.Outer`1+Foo", "App.Outer`1+Foo")]
    [InlineData("événement", "événement")]
    [InlineData("a<b>", "a-u003Cb-u003E")]
    [InlineData("a b", "a-u0020b")]
    [InlineData("\U0001D400x", "\U0001D400x")]
    public void Sanitize_keeps_identifier_characters_and_escapes_the_rest(string input, string expected)
        => ZeroAlloc.Outbox.Generator.HintNames.Sanitize(input).Should().Be(expected);

    // A lone surrogate does not survive theory-data serialisation, so it has its own test.
    [Fact]
    public void Sanitize_escapes_a_lone_surrogate()
        => ZeroAlloc.Outbox.Generator.HintNames.Sanitize("a" + '\uD800' + "b").Should().Be("a-uD800b");
}
