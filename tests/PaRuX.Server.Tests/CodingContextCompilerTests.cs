using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingContextCompilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-context-" + Guid.NewGuid().ToString("N"));

    public CodingContextCompilerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CompileAsync_IsBounded_AndIncludesRepositoryEvidence()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Demo\nLocal repository instructions.");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "RoutingService.cs"),
            "public sealed class RoutingService { public void SelectModel() { } }");
        File.WriteAllText(Path.Combine(_root, "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        string result = await CodingContextCompiler.CompileAsync(
            _root, "Update RoutingService SelectModel behavior", CancellationToken.None, 4000);

        Assert.True(result.Length <= 4000);
        Assert.Contains("Compiled repository context", result);
        Assert.Contains("Context selection:", result);
        Assert.Contains("Repository map", result);
        Assert.Contains("README.md", result);
        Assert.Contains("RoutingService.cs", result);
        Assert.Contains("SelectModel", result);
    }

    [Fact]
    public async Task CompileAsync_Uses_Roslyn_Call_Graph_For_Task_Relevant_Symbols()
    {
        File.WriteAllText(Path.Combine(_root, "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_root, "Service.cs"),
            "public sealed class Service { public int Add(int a, int b) => a + b; }");
        File.WriteAllText(Path.Combine(_root, "Caller.cs"),
            "public sealed class Caller { public int Run() { var s = new Service(); return s.Add(1, 2); } }");
        File.WriteAllText(Path.Combine(_root, "CallerTests.cs"),
            "public sealed class CallerTests { public void RunWorks() { } }");

        string result = await CodingContextCompiler.CompileAsync(
            _root, "Change Caller.Run and verify Service.Add integration", CancellationToken.None, 8000);

        Assert.Contains("C# semantic graph", result);
        Assert.Contains("Caller.cs", result);
        Assert.Contains("Service.cs", result);
        Assert.Contains("--Calls-->", result);
    }

    [Fact]
    public async Task CompileAsync_Uses_Polyglot_Import_Graph_For_TypeScript_Repositories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"scripts\":{\"test\":\"vitest run\"}}");
        File.WriteAllText(Path.Combine(_root, "src", "policy.ts"),
            "export function choosePolicy(input: string) { return input === 'safe'; }");
        File.WriteAllText(Path.Combine(_root, "src", "router.ts"),
            "import { choosePolicy } from './policy';\nexport function selectRoute(input: string) { return choosePolicy(input); }");

        string result = await CodingContextCompiler.CompileAsync(
            _root, "Change selectRoute and choosePolicy behavior", CancellationToken.None, 9000);

        Assert.Contains("Polyglot dependency graph", result);
        Assert.Contains("src/router.ts", result);
        Assert.Contains("src/policy.ts", result);
        Assert.Contains("--imports ./policy-->", result);
    }

    [Fact]
    public async Task CompileAsync_Carries_Bounded_Prior_Agent_Evidence()
    {
        File.WriteAllText(Path.Combine(_root, "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_root, "Feature.cs"), "public sealed class Feature { }");
        string marker = "unique-prior-observation-42";

        string result = await CodingContextCompiler.CompileAsync(
            _root,
            "Update Feature",
            CancellationToken.None,
            6000,
            new[] { "read_file Feature.cs\n" + marker });

        Assert.Contains("Prior agent observations", result);
        Assert.Contains(marker, result);
        Assert.True(result.Length <= 6000);
    }

    [Fact]
    public void ExtractTaskTerms_PrefersIdentifiers_AndDropsStopWords()
    {
        List<string> terms = CodingContextCompiler.ExtractTaskTerms(
            "Please update RuntimeOptimizationService BuildTopologyCandidates without changing unrelated files");

        Assert.Contains("RuntimeOptimizationService", terms);
        Assert.Contains("BuildTopologyCandidates", terms);
        Assert.DoesNotContain(terms, value => string.Equals(value, "Please", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(terms, value => string.Equals(value, "files", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompileAsync_DoesNotTraverseBuildNoise()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "SecretGenerated.cs"), "needleGeneratedOnly");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Real.cs"), "public sealed class Real { public string Value => \"needleReal\"; }");

        string result = await CodingContextCompiler.CompileAsync(
            _root, "find needleReal", CancellationToken.None, 4000);

        Assert.Contains("src/Real.cs", result);
        Assert.DoesNotContain("SecretGenerated.cs", result);
    }

    [Fact]
    public async Task CompileAsync_Allows_A_Larger_But_Still_Hard_Bounded_Packet_For_Long_Context_Models()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        for (int i = 0; i < 60; i++)
            File.WriteAllText(Path.Combine(_root, "src", $"Feature{i}.ts"),
                $"export function Feature{i}() {{ return {i}; }}\n" + new string('x', 1200));

        string result = await CodingContextCompiler.CompileAsync(
            _root, "Inspect Feature42 and related features", CancellationToken.None, 100_000);

        Assert.True(result.Length <= CodingContextCompiler.MaximumMaxChars);
        Assert.Contains("Feature42", result);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
