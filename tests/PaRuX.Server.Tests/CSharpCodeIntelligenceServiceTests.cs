using PaRuX.Intelligence;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CSharpCodeIntelligenceServiceTests
{
    [Fact]
    public void Semantic_graph_resolves_real_method_call_instead_of_text_match()
    {
        var graph = CSharpCodeIntelligenceService.AnalyzeSources(new[]
        {
            ("Service.cs", "namespace Demo; public class Service { public int Add(int a,int b)=>a+b; }") ,
            ("Caller.cs", "namespace Demo; public class Caller { public int Run(){ var s=new Service(); return s.Add(1,2); } }")
        });

        CodeIntelligenceNode run = graph.Nodes.Single(node => node.Kind == CodeIntelligenceNodeKind.Method && node.Name == "Run");
        CodeIntelligenceNode add = graph.Nodes.Single(node => node.Kind == CodeIntelligenceNodeKind.Method && node.Name == "Add");

        Assert.Contains(graph.Edges, edge => edge.Kind == CodeIntelligenceEdgeKind.Calls
            && edge.FromId == run.Id && edge.ToId == add.Id);
    }

    [Fact]
    public void Unknown_external_call_does_not_create_fabricated_internal_edge()
    {
        var graph = CSharpCodeIntelligenceService.AnalyzeSources(new[]
        {
            ("Only.cs", "namespace Demo; public class Only { public void Run(){ System.Console.WriteLine(\"x\"); } public void WriteLine(){} }")
        });
        CodeIntelligenceNode run = graph.Nodes.Single(node => node.Kind == CodeIntelligenceNodeKind.Method && node.Name == "Run");
        CodeIntelligenceNode localWriteLine = graph.Nodes.Single(node => node.Kind == CodeIntelligenceNodeKind.Method && node.Name == "WriteLine");

        Assert.DoesNotContain(graph.Edges, edge => edge.Kind == CodeIntelligenceEdgeKind.Calls
            && edge.FromId == run.Id && edge.ToId == localWriteLine.Id);
    }

    [Fact]
    public void Conventional_test_type_links_to_production_type()
    {
        var graph = CSharpCodeIntelligenceService.AnalyzeSources(new[]
        {
            ("RuntimeManager.cs", "public class RuntimeManager { public void Start(){} }"),
            ("RuntimeManagerTests.cs", "public class RuntimeManagerTests { public void Works(){} }")
        });
        CodeIntelligenceNode test = graph.Nodes.Single(node => node.Kind == CodeIntelligenceNodeKind.Type && node.Name == "RuntimeManagerTests");
        CodeIntelligenceNode product = graph.Nodes.Single(node => node.Kind == CodeIntelligenceNodeKind.Type && node.Name == "RuntimeManager");

        Assert.Contains(graph.Edges, edge => edge.Kind == CodeIntelligenceEdgeKind.Tests
            && edge.FromId == test.Id && edge.ToId == product.Id);
    }

    [Fact]
    public void Node_locations_are_grounded_in_source_lines()
    {
        var graph = CSharpCodeIntelligenceService.AnalyzeSources(new[]
        {
            ("Foo.cs", "public class Foo\n{\n    public void Bar()\n    {\n    }\n}")
        });
        CodeIntelligenceNode method = graph.Nodes.Single(node => node.Kind == CodeIntelligenceNodeKind.Method && node.Name == "Bar");

        Assert.Equal("Foo.cs", method.RelativePath);
        Assert.Equal(3, method.StartLine);
        Assert.True(method.EndLine >= method.StartLine);
    }
}
