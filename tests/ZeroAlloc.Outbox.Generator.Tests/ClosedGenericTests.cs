using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Outbox.Generator.Tests;

/// <summary>
/// A generic [OutboxMessage] type gets code for every closed construction the generator can see:
/// an IOutboxWriter or IOutboxDispatcher of it in the source, or an assembly-level
/// [OutboxMessage(typeof ...)] declaration.
/// </summary>
public sealed class ClosedGenericTests
{
    private const string Envelope = """
        namespace App
        {
            [OutboxMessage]
            public sealed record Envelope<T>(T Payload);

            public sealed record Order(int Id);
        }
        """;

    // Assembly attributes must follow the using directives, so every source gets its usings here.
    private static GeneratorRunResult Run(string source, out Compilation output)
        => GeneratorTestHelper.RunCompiled(
            "using ZeroAlloc.Outbox;\n" + source.Replace("using ZeroAlloc.Outbox;", string.Empty, System.StringComparison.Ordinal),
            out output);

    private static string[] Errors(Compilation output)
        => output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();

    private static string[] Ids(GeneratorRunResult result)
        => result.Diagnostics.Select(d => d.Id).OrderBy(i => i, System.StringComparer.Ordinal).ToArray();

    private static string SourceAt(Location location)
        => location.SourceTree!.GetText().ToString(location.SourceSpan);

    private static string Generated(GeneratorRunResult result, string hintName)
        => result.GeneratedSources.First(s => string.Equals(s.HintName, hintName, System.StringComparison.Ordinal))
            .SourceText.ToString();

    [Fact]
    public void Visible_writer_usage_generates_a_writer_dispatcher_and_registration()
    {
        var result = Run(Envelope + """

            namespace App
            {
                public sealed class Checkout(IOutboxWriter<Envelope<Order>> writer)
                {
                    public IOutboxWriter<Envelope<Order>> Writer { get; } = writer;
                }
            }
            """, out var output);

        result.Exception.Should().BeNull();
        result.Diagnostics.Should().BeEmpty();
        Errors(output).Should().BeEmpty();
        var code = Generated(result, "App.Envelope`1.Outbox.g.cs");
        code.Should().Contain("internal sealed class EnvelopeOfOrderOutboxWriter : global::ZeroAlloc.Outbox.IOutboxWriter<global::App.Envelope<global::App.Order>>");
        code.Should().Contain("internal sealed class EnvelopeOfOrderOutboxTypeDispatcher : global::ZeroAlloc.Outbox.IOutboxTypeDispatcher");
        code.Should().Contain("_store.EnqueueAsync(\"App.Envelope<App.Order>\"");
        code.Should().Contain("public string TypeName => \"App.Envelope<App.Order>\";");
        code.Should().Contain("public static global::ZeroAlloc.Outbox.IOutboxBuilder AddEnvelopeOutbox(");
    }

    [Fact]
    public void Visible_dispatcher_usage_counts_as_a_closed_usage()
    {
        var result = Run(Envelope + """

            namespace App
            {
                public sealed class OrderHandler : IOutboxDispatcher<Envelope<Order>>
                {
                    public System.Threading.Tasks.ValueTask DispatchAsync(Envelope<Order> message, System.Threading.CancellationToken ct)
                        => default;
                }
            }
            """, out var output);

        result.Diagnostics.Should().BeEmpty();
        Errors(output).Should().BeEmpty();
        Generated(result, "App.Envelope`1.Outbox.g.cs").Should().Contain("\"App.Envelope<App.Order>\"");
    }

    [Fact]
    public void Assembly_level_declaration_generates_the_closed_type()
    {
        var result = Run("[assembly: ZeroAlloc.Outbox.OutboxMessage(typeof(App.Envelope<App.Order>))]\n" + Envelope, out var output);

        result.Exception.Should().BeNull();
        result.Diagnostics.Should().BeEmpty();
        Errors(output).Should().BeEmpty();
        Generated(result, "App.Envelope`1.Outbox.g.cs").Should().Contain("public string TypeName => \"App.Envelope<App.Order>\";");
    }

    [Fact]
    public void A_closed_type_seen_more_than_once_is_generated_once()
    {
        var result = Run("""
            [assembly: ZeroAlloc.Outbox.OutboxMessage(typeof(App.Envelope<App.Order>))]
            [assembly: ZeroAlloc.Outbox.OutboxMessage(typeof(App.Envelope<App.Order>))]
            """ + "\n" + Envelope + """

            namespace App
            {
                public sealed class A(IOutboxWriter<Envelope<Order>> w) { public object W => w; }
                public sealed class B(IOutboxWriter<Envelope<Order?>> w) { public object W => w; }
            }
            """, out var output);

        result.Diagnostics.Should().BeEmpty();
        Errors(output).Should().BeEmpty();
        var code = Generated(result, "App.Envelope`1.Outbox.g.cs");
        code.Split("class EnvelopeOfOrderOutboxWriter").Length.Should().Be(2);
    }

    [Fact]
    public void Every_closed_type_of_a_definition_is_registered_by_one_method()
    {
        var result = Run("""
            using ZeroAlloc.Outbox;

            [assembly: OutboxMessage(typeof(App.Pair<int, App.Order>))]
            [assembly: OutboxMessage(typeof(App.Pair<App.Order, string>))]

            namespace App
            {
                [OutboxMessage]
                public sealed record Pair<TFirst, TSecond>(TFirst First, TSecond Second);

                public sealed record Order(int Id);
            }
            """, out var output);

        result.Diagnostics.Should().BeEmpty();
        Errors(output).Should().BeEmpty();
        var code = Generated(result, "App.Pair`2.Outbox.g.cs");
        code.Should().Contain("\"App.Pair<System.Int32, App.Order>\"");
        code.Should().Contain("\"App.Pair<App.Order, System.String>\"");
        code.Should().Contain("class PairOfInt32AndOrderOutboxWriter");
        code.Should().Contain("class PairOfOrderAndStringOutboxWriter");
        code.Split("AddPairOutbox(").Length.Should().Be(2);
    }

    [Theory]
    [InlineData("int?", "App.Envelope<System.Nullable<System.Int32>>", "EnvelopeOfNullableOfInt32")]
    [InlineData("(int A, string B)", "App.Envelope<System.ValueTuple<System.Int32, System.String>>", "EnvelopeOfValueTupleOfInt32AndString")]
    [InlineData("int[]", "App.Envelope<System.Int32[]>", "EnvelopeOfInt32Array")]
    [InlineData("int[,]", "App.Envelope<System.Int32[,]>", "EnvelopeOfInt32Array2D")]
    [InlineData("dynamic", "App.Envelope<System.Object>", "EnvelopeOfObject")]
    [InlineData("Outer.Inner", "App.Envelope<App.Outer.Inner>", "EnvelopeOfInner")]
    [InlineData("Generic<Order>.Inner", "App.Envelope<App.Generic<App.Order>.Inner>", "EnvelopeOfInnerOfOrder")]
    [InlineData("Envelope<Order>", "App.Envelope<App.Envelope<App.Order>>", "EnvelopeOfEnvelopeOfOrder")]
    [InlineData("global::Root", "App.Envelope<Root>", "EnvelopeOfRoot")]
    // The namespace is written as a non-generic message's stored name writes it, keyword escape included.
    [InlineData("@event.@class", "App.Envelope<@event.class>", "EnvelopeOfclass")]
    public void Stored_name_is_the_fully_qualified_CSharp_name_of_the_runtime_type(
        string argument, string storedName, string identifier)
    {
        var result = Run(Envelope + $$"""

            public sealed record Root;

            namespace @event { public sealed record @class; }

            namespace App
            {
                public static class Outer { public sealed record Inner; }
                public static class Generic<T> { public sealed record Inner; }

                public sealed class Uses(IOutboxWriter<Envelope<{{argument}}>> w) { public object W => w; }
            }
            """, out var output);

        result.Diagnostics.Should().BeEmpty();
        Errors(output).Should().BeEmpty();
        var code = Generated(result, "App.Envelope`1.Outbox.g.cs");
        code.Should().Contain($"public string TypeName => \"{storedName}\";");
        code.Should().Contain($"internal sealed class {identifier}OutboxWriter ");
    }

    [Fact]
    public void Open_generic_without_a_closed_usage_reports_ZO0004_and_generates_nothing()
    {
        var result = Run(Envelope, out var output);

        result.Exception.Should().BeNull();
        Ids(result).Should().Equal("ZO0004");
        result.Diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Warning);
        result.Diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .Should().Contain("[assembly: OutboxMessage(typeof(Envelope<");
        result.GeneratedSources.Should().BeEmpty();
        Errors(output).Should().BeEmpty();
    }

    [Fact]
    public void A_usage_that_is_still_open_is_not_a_closed_usage()
    {
        var result = Run(Envelope + """

            namespace App
            {
                public sealed class Forwarder<T>(IOutboxWriter<Envelope<T>> w) where T : notnull { public object W => w; }
            }
            """, out var output);

        Ids(result).Should().Equal("ZO0004");
        result.GeneratedSources.Should().BeEmpty();
        Errors(output).Should().BeEmpty();
    }

    [Fact]
    public void Stored_name_over_256_characters_reports_ZO0005()
    {
        var longName = new string('L', 250);
        var result = Run(Envelope + $$"""

            namespace App
            {
                public sealed record {{longName}};
                public sealed class Uses(IOutboxWriter<Envelope<{{longName}}>> w) { public object W => w; }
            }
            """, out _);

        Ids(result).Should().Equal("ZO0005");
        result.Diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Error);
        SourceAt(result.Diagnostics[0].Location).Should().StartWith("IOutboxWriter<Envelope<");
        result.GeneratedSources.Should().BeEmpty();
    }

    [Theory]
    [InlineData("[assembly: OutboxMessage(typeof(App.Envelope<>))]", "open generic")]
    [InlineData("[assembly: OutboxMessage(typeof(App.Order))]", "not generic")]
    [InlineData("[assembly: OutboxMessage(typeof(App.Plain<App.Order>))]", "not marked [OutboxMessage]")]
    [InlineData("[assembly: OutboxMessage(typeof(System.Collections.Generic.List<App.Order>))]", "not marked [OutboxMessage]")]
    [InlineData("[assembly: OutboxMessage]", "names no type")]
    public void Invalid_assembly_level_declaration_reports_ZO0006(string declaration, string reason)
    {
        var result = Run(declaration + "\n" + Envelope + """

            namespace App
            {
                public sealed record Plain<T>(T Value);
                public sealed class Holder { private sealed record Hidden; }
            }
            """, out _);

        var zo0006 = result.Diagnostics.Where(d => string.Equals(d.Id, "ZO0006", System.StringComparison.Ordinal)).ToArray();
        zo0006.Should().ContainSingle();
        zo0006[0].Severity.Should().Be(DiagnosticSeverity.Error);
        zo0006[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).Should().Contain(reason);
        SourceAt(zo0006[0].Location).Should().StartWith("OutboxMessage");
    }

    [Fact]
    public void Type_argument_the_generated_code_cannot_reach_reports_ZO0006_at_the_usage()
    {
        var result = Run(Envelope + """

            namespace App
            {
                public sealed class Holder
                {
                    private sealed record Hidden;
                    private readonly IOutboxWriter<Envelope<Hidden>>? _writer = null;
                    public object? W => _writer;
                }
            }
            """, out _);

        // The closed usage exists, so ZO0006 explains it and ZO0004 does not repeat it.
        Ids(result).Should().Equal("ZO0006");
        result.Diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).Should().Contain("not accessible");
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Type_argument_on_a_type_declaration_reports_ZO0006()
    {
        var result = Run("""
            using ZeroAlloc.Outbox;

            namespace App
            {
                [OutboxMessage(typeof(Order))]
                public sealed record Order(int Id);
            }
            """, out var output);

        Ids(result).Should().Equal("ZO0006");
        result.GeneratedSources.Should().BeEmpty();
        Errors(output).Should().BeEmpty();
    }

    [Fact]
    public void Type_argument_on_a_later_partial_declaration_reports_ZO0006()
    {
        var result = Run("""
            namespace App
            {
                [OutboxMessage]
                public sealed partial record Order(int Id);

                [OutboxMessage(typeof(Order))]
                public sealed partial record Order;
            }
            """, out _);

        Ids(result).Should().Equal("ZO0006");
        SourceAt(result.Diagnostics[0].Location).Should().StartWith("OutboxMessage(typeof(Order))");
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Closed_types_that_get_the_same_generated_names_report_ZO0007()
    {
        var result = Run("""
            using ZeroAlloc.Outbox;

            [assembly: OutboxMessage(typeof(App.Envelope<A.Order>))]
            [assembly: OutboxMessage(typeof(App.Envelope<B.Order>))]
            [assembly: OutboxMessage(typeof(App.Envelope<A.Unique>))]

            namespace A { public sealed record Order; public sealed record Unique; }
            namespace B { public sealed record Order; }

            namespace App
            {
                [OutboxMessage]
                public sealed record Envelope<T>(T Payload);
            }
            """, out var output);

        Ids(result).Should().Equal("ZO0007", "ZO0007");
        Errors(output).Should().BeEmpty();
        var code = Generated(result, "App.Envelope`1.Outbox.g.cs");
        code.Should().Contain("EnvelopeOfUniqueOutboxWriter");
        code.Should().NotContain("EnvelopeOfOrderOutboxWriter");
    }

    [Fact]
    public void Generic_and_non_generic_types_of_the_same_name_report_ZO0007_for_the_registration_method()
    {
        var result = Run("""
            using ZeroAlloc.Outbox;

            [assembly: OutboxMessage(typeof(App.Foo<int>))]

            namespace App
            {
                [OutboxMessage]
                public sealed record Foo(int Id);

                [OutboxMessage]
                public sealed record Foo<T>(T Value);
            }
            """, out var output);

        Ids(result).Should().Equal("ZO0007");
        result.Diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            .Should().Contain("AddFooOutbox");
        result.GeneratedSources.Select(s => s.HintName).Should().Equal("App.Foo.Outbox.g.cs");
        Errors(output).Should().BeEmpty();
    }

    [Fact]
    public void Non_generic_message_output_is_unchanged_by_a_generic_message_next_to_it()
    {
        var alone = Run("""
            using ZeroAlloc.Outbox;
            namespace App { [OutboxMessage] public sealed record Order(int Id); }
            """, out _);
        var together = Run("""
            using ZeroAlloc.Outbox;
            [assembly: OutboxMessage(typeof(App.Envelope<App.Order>))]
            namespace App
            {
                [OutboxMessage] public sealed record Order(int Id);
                [OutboxMessage] public sealed record Envelope<T>(T Payload);
            }
            """, out var output);

        Errors(output).Should().BeEmpty();
        Generated(together, "App.Order.Outbox.g.cs").Should().Be(Generated(alone, "App.Order.Outbox.g.cs"));
    }
}
