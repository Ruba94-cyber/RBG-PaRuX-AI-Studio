using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public class CSharpCodeIntelligenceGraphCharacterizationTests
{
    private static CSharpCodeIntelligenceGraph Analyze(string source) =>
        CSharpCodeIntelligenceService.AnalyzeSources(
            new[] { ("A.cs", source) });

    private static CSharpCodeIntelligenceGraph AnalyzeMany(
        params (string Path, string Text)[] sources) =>
        CSharpCodeIntelligenceService.AnalyzeSources(sources);

    [Fact]
    public void Empty_source_list_returns_empty_graph_with_utc_timestamp()
    {
        DateTime before = DateTime.UtcNow;
        CSharpCodeIntelligenceGraph graph =
            CSharpCodeIntelligenceService.AnalyzeSources(Array.Empty<(string, string)>());
        DateTime after = DateTime.UtcNow;

        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
        Assert.Equal(0, graph.ErrorDiagnosticCount);
        Assert.Equal(0, graph.WarningDiagnosticCount);
        Assert.True(graph.IndexedUtc >= before && graph.IndexedUtc <= after);
    }

    [Fact]
    public void Single_file_yields_file_node_with_filename_and_relative_path()
    {
        CSharpCodeIntelligenceGraph graph = Analyze("class Widget { }");

        CodeIntelligenceNode file = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.File);
        Assert.Equal("A.cs", file.Name);
        Assert.Equal("A.cs", file.RelativePath);
        Assert.StartsWith("file:", file.Id);
        Assert.Equal("A.cs", file.FullyQualifiedName);
    }

    [Fact]
    public void Type_method_field_and_property_nodes_are_created_and_linked_to_file()
    {
        string source = """
            public class Widget
            {
                public int Count { get; set; }
                public int Total = 0;
                public int Add(int value) { return Total + value; }
            }
            """;

        CSharpCodeIntelligenceGraph graph = Analyze(source);
        CodeIntelligenceNode file = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.File);
        CodeIntelligenceNode type = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Type && node.Name == "Widget");
        CodeIntelligenceNode property = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Property && node.Name == "Count");
        CodeIntelligenceNode field = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Field && node.Name == "Total");
        CodeIntelligenceNode method = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Method && node.Name == "Add");

        Assert.Equal("A.cs", type.RelativePath);
        Assert.Equal("A.cs", property.RelativePath);
        Assert.Equal("A.cs", field.RelativePath);
        Assert.Equal("A.cs", method.RelativePath);

        Assert.Contains(graph.Edges, edge => edge.FromId == file.Id && edge.ToId == type.Id && edge.Kind == CodeIntelligenceEdgeKind.Contains);
        Assert.Contains(graph.Edges, edge => edge.FromId == file.Id && edge.ToId == property.Id && edge.Kind == CodeIntelligenceEdgeKind.Contains);
        Assert.Contains(graph.Edges, edge => edge.FromId == file.Id && edge.ToId == field.Id && edge.Kind == CodeIntelligenceEdgeKind.Contains);
        Assert.Contains(graph.Edges, edge => edge.FromId == file.Id && edge.ToId == method.Id && edge.Kind == CodeIntelligenceEdgeKind.Contains);
    }

    [Fact]
    public void Namespace_declaration_yields_namespace_node_contained_in_file()
    {
        string source = """
            namespace PaRuX.Intelligence
            {
                public class Widget { }
            }
            """;

        CSharpCodeIntelligenceGraph graph = Analyze(source);
        CodeIntelligenceNode file = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.File);
        CodeIntelligenceNode ns = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Namespace);
        CodeIntelligenceNode type = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Type && node.Name == "Widget");

        Assert.Equal("Intelligence", ns.Name);
        Assert.Equal("PaRuX.Intelligence", ns.FullyQualifiedName);
        Assert.Equal("A.cs", ns.RelativePath);
        Assert.Contains(graph.Edges, edge => edge.FromId == file.Id && edge.ToId == ns.Id && edge.Kind == CodeIntelligenceEdgeKind.Contains);
    }

    [Fact]
    public void Resolvable_internal_call_produces_calls_edge_between_method_and_file()
    {
        string source = """
            public class Widget
            {
                public int Add(int a, int b) { return Combine(a, b); }
                public int Combine(int a, int b) { return a + b; }
            }
            """;

        CSharpCodeIntelligenceGraph graph = Analyze(source);
        CodeIntelligenceNode add = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Method && node.Name == "Add");
        CodeIntelligenceNode combine = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Method && node.Name == "Combine");

        Assert.Contains(
            graph.Edges,
            edge => edge.FromId == add.Id && edge.ToId == combine.Id && edge.Kind == CodeIntelligenceEdgeKind.Calls);
    }

    [Fact]
    public void External_using_directive_creates_no_import_edge_when_target_not_in_source()
    {
        string source = """
            using System.Collections.Generic;
            public class Widget { public List<int> Values { get; set; } }
            """;

        CSharpCodeIntelligenceGraph graph = Analyze(source);

        Assert.DoesNotContain(graph.Edges, edge => edge.Kind == CodeIntelligenceEdgeKind.Imports);
    }

    [Fact]
    public void Using_directive_to_source_namespace_creates_import_edge()
    {
        CSharpCodeIntelligenceGraph graph = AnalyzeMany(
            ("Alpha.cs", "namespace PaRuX.Intelligence {\npublic class Alpha { }\n}"),
            ("Beta.cs", "using PaRuX.Intelligence;\nnamespace Other {\npublic class Beta { public Alpha Alpha { get; set; } }\n}"));

        CodeIntelligenceNode betaFile = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.File && node.RelativePath == "Beta.cs");
        CodeIntelligenceNode alphaNs = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Namespace && node.Name == "Intelligence" && node.FullyQualifiedName == "PaRuX.Intelligence");

        Assert.Contains(
            graph.Edges,
            edge => edge.FromId == betaFile.Id && edge.ToId == alphaNs.Id && edge.Kind == CodeIntelligenceEdgeKind.Imports);
    }

    [Fact]
    public void Syntax_error_in_source_produces_error_diagnostic_count()
    {
        CSharpCodeIntelligenceGraph graph = Analyze("class Broken { } }");

        Assert.True(graph.ErrorDiagnosticCount > 0, $"expected diagnostics, got {graph.ErrorDiagnosticCount} errors / {graph.WarningDiagnosticCount} warnings");
    }

    [Fact]
    public void Test_class_gets_tests_edge_to_production_class_with_confidence()
    {
        string source = """
            public class Widget { }
            public class WidgetTests
            {
                public void Smoke() { var w = new Widget(); }
            }
            """;

        CSharpCodeIntelligenceGraph graph = Analyze(source);
        CodeIntelligenceNode widget = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Type && node.Name == "Widget");
        CodeIntelligenceNode widgetTests = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Type && node.Name == "WidgetTests");

        CodeIntelligenceEdge testEdge = Assert.Single(
            graph.Edges,
            edge => edge.FromId == widgetTests.Id && edge.ToId == widget.Id && edge.Kind == CodeIntelligenceEdgeKind.Tests);
        Assert.Equal(0.85, testEdge.Confidence, 3);
    }

    [Fact]
    public void Edges_are_deduplicated_across_trees()
    {
        string source = """
            public class Widget
            {
                public int Add(int a, int b) { return Combine(a, b) + Combine(b, a); }
                public int Combine(int a, int b) { return a + b; }
            }
            """;

        CSharpCodeIntelligenceGraph graph = Analyze(source);
        CodeIntelligenceNode add = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Method && node.Name == "Add");
        CodeIntelligenceNode combine = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Method && node.Name == "Combine");

        var calls = graph.Edges
            .Where(edge => edge.FromId == add.Id && edge.ToId == combine.Id && edge.Kind == CodeIntelligenceEdgeKind.Calls)
            .ToList();
        Assert.Single(calls);
    }

    [Fact]
    public void Enumerate_source_files_returns_empty_for_missing_or_blank_root()
    {
        Assert.Empty(CSharpCodeIntelligenceService.EnumerateSourceFiles(""));
        Assert.Empty(CSharpCodeIntelligenceService.EnumerateSourceFiles("   "));
        Assert.Empty(CSharpCodeIntelligenceService.EnumerateSourceFiles(
            Path.Combine(Path.GetTempPath(), "parux-ci-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Enumerate_source_files_prunes_noise_directories_and_returns_sorted_paths()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-ci-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            Directory.CreateDirectory(Path.Combine(root, "obj"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "Zeta.cs"), "");
            File.WriteAllText(Path.Combine(root, "alpha.cs"), "");
            File.WriteAllText(Path.Combine(root, "bin", "junk.cs"), "");
            File.WriteAllText(Path.Combine(root, "obj", "junk.cs"), "");
            File.WriteAllText(Path.Combine(root, "src", "Deep.cs"), "");

            IReadOnlyList<string> files = CSharpCodeIntelligenceService.EnumerateSourceFiles(root);

            Assert.Equal(3, files.Count);
            Assert.All(files, file => Assert.DoesNotContain("bin", new[] { Path.GetFileName(Path.GetDirectoryName(file))! }));
            Assert.All(files, file => Assert.DoesNotContain("obj", new[] { Path.GetFileName(Path.GetDirectoryName(file))! }));
            Assert.Contains(files, file => Path.GetFileName(file) == "Deep.cs");

            var relative = files.Select(f => Path.GetRelativePath(root, f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal(relative, files.Select(f => Path.GetRelativePath(root, f)).ToList());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Index_repository_async_throws_for_missing_root()
    {
        CSharpCodeIntelligenceService service = new();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            service.IndexRepositoryAsync(
                Path.Combine(Path.GetTempPath(), "parux-ci-" + Guid.NewGuid().ToString("N")),
                CancellationToken.None));
    }

    [Fact]
    public void Reference_to_source_field_produces_references_edge()
    {
        string source = """
            public class Widget
            {
                public int Total = 0;
                public int ReadTotal() { return Total; }
            }
            """;

        CSharpCodeIntelligenceGraph graph = Analyze(source);
        CodeIntelligenceNode field = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Field && node.Name == "Total");
        CodeIntelligenceNode method = Assert.Single(graph.Nodes, node => node.Kind == CodeIntelligenceNodeKind.Method && node.Name == "ReadTotal");

        Assert.Contains(
            graph.Edges,
            edge => edge.FromId == method.Id && edge.ToId == field.Id && edge.Kind == CodeIntelligenceEdgeKind.References);
    }
}
