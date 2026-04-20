using Microsoft.CodeAnalysis;
using System;

namespace ZeroAlloc.Outbox.Generator.Tests;

public sealed class DiagnosticTests
{
    [Fact]
    public void ZO0001_OutboxOnInterface_EmitsWarning()
    {
        var (_, diagnostics) = GeneratorTestHelper.Run("""
            using ZeroAlloc.Outbox;
            [OutboxMessage]
            public interface IMyMessage { }
            """);

        bool found = false;
        foreach (var d in diagnostics)
        {
            if (string.Equals(d.Id, "ZO0001", StringComparison.Ordinal)
                && d.Severity == DiagnosticSeverity.Warning)
            {
                found = true;
                break;
            }
        }
        found.Should().BeTrue("ZO0001 warning should be emitted for interface");
    }

    [Fact]
    public void ZO0002_OutboxOnStaticClass_EmitsWarning()
    {
        var (_, diagnostics) = GeneratorTestHelper.Run("""
            using ZeroAlloc.Outbox;
            [OutboxMessage]
            public static class StaticMessage { }
            """);

        bool found = false;
        foreach (var d in diagnostics)
        {
            if (string.Equals(d.Id, "ZO0002", StringComparison.Ordinal)
                && d.Severity == DiagnosticSeverity.Warning)
            {
                found = true;
                break;
            }
        }
        found.Should().BeTrue("ZO0002 warning should be emitted for static class");
    }

    [Fact]
    public void ZO0003_OutboxOnNestedType_EmitsWarning()
    {
        var (_, diagnostics) = GeneratorTestHelper.Run("""
            using ZeroAlloc.Outbox;
            public class Outer
            {
                [OutboxMessage]
                public sealed record Inner(int Id);
            }
            """);

        bool found = false;
        foreach (var d in diagnostics)
        {
            if (string.Equals(d.Id, "ZO0003", StringComparison.Ordinal)
                && d.Severity == DiagnosticSeverity.Warning)
            {
                found = true;
                break;
            }
        }
        found.Should().BeTrue("ZO0003 warning should be emitted for nested type");
    }

    [Fact]
    public void ValidRecord_EmitsNoDiagnostics()
    {
        var (_, diagnostics) = GeneratorTestHelper.Run("""
            using ZeroAlloc.Outbox;
            namespace MyApp;
            [OutboxMessage]
            public sealed record OrderPlaced(int Id);
            """);

        bool hasZoDiagnostic = false;
        foreach (var d in diagnostics)
        {
            if (d.Id.StartsWith("ZO", StringComparison.Ordinal))
            {
                hasZoDiagnostic = true;
                break;
            }
        }
        hasZoDiagnostic.Should().BeFalse("no ZO* diagnostics for valid record");
    }
}
