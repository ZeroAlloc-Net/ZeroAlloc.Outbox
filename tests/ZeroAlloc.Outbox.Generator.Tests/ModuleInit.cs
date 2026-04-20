using System.Runtime.CompilerServices;
using VerifyTests;

namespace ZeroAlloc.Outbox.Generator.Tests;

public static class ModuleInit
{
    [ModuleInitializer]
    public static void Init() => VerifySourceGenerators.Initialize();
}
