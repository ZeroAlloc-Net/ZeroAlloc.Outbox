using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Outbox.Generator.Tests;

public sealed class HintNameTests
{
    private static GeneratorRunResult RunGenerator(string source, out Compilation output)
        => GeneratorTestHelper.RunCompiled(source, out output);

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
    public void Generic_type_hint_name_carries_its_arity()
    {
        var result = RunGenerator("""
            using ZeroAlloc.Outbox;

            [assembly: OutboxMessage(typeof(App.Foo<int>))]
            [assembly: OutboxMessage(typeof(App.Bar<int, string>))]

            namespace App
            {
                [OutboxMessage]
                public sealed record Foo<T>(T Value);

                [OutboxMessage]
                public sealed record Bar<T1, T2>(T1 First, T2 Second);
            }
            """, out _);

        result.Exception.Should().BeNull();
        HintNames(result).Should().Equal("App.Bar`2.Outbox.g.cs", "App.Foo`1.Outbox.g.cs");
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
    [InlineData("[OutboxMessage] public sealed partial record Order(int Id);\n[System.Serializable] public sealed partial record Order;")]
    [InlineData("[OutboxMessage] public sealed partial record Order(int Id);\n[OutboxMessage] public sealed partial record Order;")]
    [InlineData("[OutboxMessage, OutboxMessage] public sealed partial record Order(int Id);")]
    public void Partial_message_declared_with_attributes_in_several_places_is_generated_once(string declarations)
    {
        var result = RunGenerator("using ZeroAlloc.Outbox;\nnamespace App;\n" + declarations, out var output);

        result.Exception.Should().BeNull();
        HintNames(result).Should().Equal("App.Order.Outbox.g.cs");
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
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
