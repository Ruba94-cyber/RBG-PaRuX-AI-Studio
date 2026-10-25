using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PaRuX.Intelligence;

public enum CodeIntelligenceNodeKind
{
    File = 0,
    Namespace = 1,
    Type = 2,
    Method = 3,
    Property = 4,
    Field = 5
}

public enum CodeIntelligenceEdgeKind
{
    Contains = 0,
    Calls = 1,
    Imports = 2,
    References = 3,
    Tests = 4
}

public sealed record CodeIntelligenceNode(
    string Id,
    CodeIntelligenceNodeKind Kind,
    string Name,
    string FullyQualifiedName,
    string RelativePath,
    int StartLine,
    int EndLine);

public sealed record CodeIntelligenceEdge(
    string FromId,
    string ToId,
    CodeIntelligenceEdgeKind Kind,
    double Confidence = 1.0);

public sealed class CSharpCodeIntelligenceGraph
{
    public List<CodeIntelligenceNode> Nodes { get; set; } = new();
    public List<CodeIntelligenceEdge> Edges { get; set; } = new();
    public int ErrorDiagnosticCount { get; set; }
    public int WarningDiagnosticCount { get; set; }
    public DateTime IndexedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Roslyn-backed C# repository intelligence. Calls and references are accepted only when Roslyn can
/// resolve a semantic symbol inside the indexed compilation. Missing external project references may
/// reduce graph coverage but never create a fabricated edge. The graph is storage-neutral and safe
/// to feed into context ranking, project knowledge, or a future cross-language graph.
/// </summary>
public sealed class CSharpCodeIntelligenceService
{
    private static readonly HashSet<string> NoiseDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", ".parux-agents"
    };

    public async Task<CSharpCodeIntelligenceGraph> IndexRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            throw new DirectoryNotFoundException("C# repository root does not exist.");

        string root = Path.GetFullPath(repositoryRoot);
        List<(string RelativePath, string Text)> sources = new();
        foreach (string file in EnumerateSourceFiles(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            try
            {
                sources.Add((relative, await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return AnalyzeSources(sources);
    }

    public static IReadOnlyList<string> EnumerateSourceFiles(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return Array.Empty<string>();
        string root = Path.GetFullPath(repositoryRoot);
        try
        {
            // AllDirectories + a post-hoc noise filter still walks every bin/obj/.git/node_modules
            // directory before throwing its files away - on a real solution with several projects'
            // build output present (thousands of DLL/resource files) that walk alone measured tens
            // of seconds. Pruning NoiseDirectories from the stack before descending means the walk
            // never enters them at all.
            var results = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(dir); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (string sub in subdirs)
                {
                    if (!NoiseDirectories.Contains(Path.GetFileName(sub))) stack.Push(sub);
                }

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*.cs"); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                results.AddRange(files);
            }
            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public static CSharpCodeIntelligenceGraph AnalyzeSources(
        IReadOnlyList<(string RelativePath, string Text)> sources)
    {
        var graph = new CSharpCodeIntelligenceGraph();
        if (sources.Count == 0) return graph;

        List<SyntaxTree> trees = sources.Select(source =>
            CSharpSyntaxTree.ParseText(
                source.Text,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: source.RelativePath)).ToList<SyntaxTree>();
        CSharpCompilation compilation = CSharpCompilation.Create(
            "PaRuX.CodeIntelligence.Index",
            trees,
            TrustedPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var bySymbol = new Dictionary<string, CodeIntelligenceNode>(StringComparer.Ordinal);
        var fileNodes = new Dictionary<string, CodeIntelligenceNode>(StringComparer.OrdinalIgnoreCase);

        foreach (SyntaxTree tree in trees)
        {
            string path = tree.FilePath.Replace('\\', '/');
            CodeIntelligenceNode fileNode = Node(
                "file:" + path,
                CodeIntelligenceNodeKind.File,
                Path.GetFileName(path),
                path,
                path,
                1,
                Math.Max(1, tree.GetText().Lines.Count));
            graph.Nodes.Add(fileNode);
            fileNodes[path] = fileNode;

            SemanticModel model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            SyntaxNode root = tree.GetRoot();
            foreach (MemberDeclarationSyntax declaration in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
            {
                ISymbol? symbol = declaration switch
                {
                    BaseNamespaceDeclarationSyntax ns => model.GetDeclaredSymbol(ns),
                    BaseTypeDeclarationSyntax type => model.GetDeclaredSymbol(type),
                    DelegateDeclarationSyntax del => model.GetDeclaredSymbol(del),
                    MethodDeclarationSyntax method => model.GetDeclaredSymbol(method),
                    ConstructorDeclarationSyntax ctor => model.GetDeclaredSymbol(ctor),
                    PropertyDeclarationSyntax property => model.GetDeclaredSymbol(property),
                    FieldDeclarationSyntax field => field.Declaration.Variables.Count == 1
                        ? model.GetDeclaredSymbol(field.Declaration.Variables[0]) : null,
                    _ => null
                };
                if (symbol == null || !IsSourceSymbol(symbol)) continue;
                CodeIntelligenceNodeKind? kind = KindOf(symbol);
                if (!kind.HasValue) continue;
                CodeIntelligenceNode node = FromSymbol(symbol, kind.Value, path);
                string key = SymbolKey(symbol);
                if (!bySymbol.TryAdd(key, node)) continue;
                graph.Nodes.Add(node);
                graph.Edges.Add(new CodeIntelligenceEdge(fileNode.Id, node.Id, CodeIntelligenceEdgeKind.Contains));
            }
        }

        foreach (SyntaxTree tree in trees)
        {
            string path = tree.FilePath.Replace('\\', '/');
            SemanticModel model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            SyntaxNode root = tree.GetRoot();

            foreach (UsingDirectiveSyntax usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                ISymbol? imported = model.GetSymbolInfo(usingDirective.Name!).Symbol;
                if (imported != null && bySymbol.TryGetValue(SymbolKey(imported), out CodeIntelligenceNode? target))
                    graph.Edges.Add(new CodeIntelligenceEdge(fileNodes[path].Id, target.Id, CodeIntelligenceEdgeKind.Imports));
            }

            foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                IMethodSymbol? targetSymbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                ISymbol? sourceSymbol = model.GetEnclosingSymbol(invocation.SpanStart);
                CodeIntelligenceNode? source = FindContainingNode(sourceSymbol, bySymbol);
                CodeIntelligenceNode? target = FindNode(targetSymbol, bySymbol);
                if (source != null && target != null && source.Id != target.Id)
                    graph.Edges.Add(new CodeIntelligenceEdge(source.Id, target.Id, CodeIntelligenceEdgeKind.Calls));
            }

            foreach (IdentifierNameSyntax identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                ISymbol? targetSymbol = model.GetSymbolInfo(identifier).Symbol;
                ISymbol? sourceSymbol = model.GetEnclosingSymbol(identifier.SpanStart);
                CodeIntelligenceNode? source = FindContainingNode(sourceSymbol, bySymbol);
                CodeIntelligenceNode? target = FindNode(targetSymbol, bySymbol);
                if (source != null && target != null && source.Id != target.Id)
                    graph.Edges.Add(new CodeIntelligenceEdge(source.Id, target.Id, CodeIntelligenceEdgeKind.References));
            }
        }

        AddTestRelations(graph);
        graph.Edges = graph.Edges.Distinct().ToList();
        IEnumerable<Diagnostic> diagnostics = compilation.GetDiagnostics();
        graph.ErrorDiagnosticCount = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        graph.WarningDiagnosticCount = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        return graph;
    }

    private static IEnumerable<MetadataReference> TrustedPlatformReferences()
    {
        string? value = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(value))
            return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        return value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    private static CodeIntelligenceNode FromSymbol(ISymbol symbol, CodeIntelligenceNodeKind kind, string path)
    {
        FileLinePositionSpan span = symbol.Locations.First(location => location.IsInSource).GetLineSpan();
        // FullyQualifiedFormat's member options default to omitting the containing type, so a
        // method/property/field symbol displays as its bare name (e.g. "Run") rather than
        // "Caller.Run()" - identical to CodeIntelligenceNode.Name and useless for anything that
        // needs to recognize a "Type.Member" reference. CSharpErrorMessageFormat always qualifies
        // a member with its containing type (matching what SymbolKey already falls back to below).
        string full = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return Node(
            "symbol:" + Stable(full + "|" + path + "|" + span.StartLinePosition.Line),
            kind,
            symbol.Name,
            full,
            path,
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1);
    }

    private static CodeIntelligenceNode Node(
        string id, CodeIntelligenceNodeKind kind, string name, string full,
        string path, int start, int end) => new(id, kind, name, full, path, start, end);

    private static CodeIntelligenceNode? FindNode(ISymbol? symbol, IReadOnlyDictionary<string, CodeIntelligenceNode> nodes)
    {
        if (symbol == null) return null;
        if (nodes.TryGetValue(SymbolKey(symbol), out CodeIntelligenceNode? exact)) return exact;
        if (symbol.OriginalDefinition != null
            && nodes.TryGetValue(SymbolKey(symbol.OriginalDefinition), out CodeIntelligenceNode? original)) return original;
        return null;
    }

    private static CodeIntelligenceNode? FindContainingNode(
        ISymbol? symbol,
        IReadOnlyDictionary<string, CodeIntelligenceNode> nodes)
    {
        while (symbol != null)
        {
            CodeIntelligenceNode? node = FindNode(symbol, nodes);
            if (node != null) return node;
            symbol = symbol.ContainingSymbol;
        }
        return null;
    }

    private static CodeIntelligenceNodeKind? KindOf(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => CodeIntelligenceNodeKind.Namespace,
        INamedTypeSymbol => CodeIntelligenceNodeKind.Type,
        IMethodSymbol => CodeIntelligenceNodeKind.Method,
        IPropertySymbol => CodeIntelligenceNodeKind.Property,
        IFieldSymbol => CodeIntelligenceNodeKind.Field,
        _ => null
    };

    private static bool IsSourceSymbol(ISymbol symbol) => symbol.Locations.Any(location => location.IsInSource);

    private static string SymbolKey(ISymbol symbol)
    {
        // FullyQualifiedFormat does not include enough member qualification to distinguish every
        // method. In particular, System.Console.WriteLine and a source type's WriteLine can
        // collapse to the same display key and fabricate an internal call edge. Roslyn's
        // documentation id includes the containing namespace/type and the complete signature.
        return symbol.GetDocumentationCommentId()
            ?? symbol.ContainingAssembly?.Identity + "|"
                + symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
    }

    private static string Stable(string value)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static void AddTestRelations(CSharpCodeIntelligenceGraph graph)
    {
        List<CodeIntelligenceNode> types = graph.Nodes.Where(node => node.Kind == CodeIntelligenceNodeKind.Type).ToList();
        foreach (CodeIntelligenceNode test in types.Where(node =>
            node.Name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || node.Name.EndsWith("Test", StringComparison.OrdinalIgnoreCase)))
        {
            string baseName = test.Name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
                ? test.Name[..^5]
                : test.Name[..^4];
            CodeIntelligenceNode? production = types.FirstOrDefault(node =>
                !ReferenceEquals(node, test)
                && string.Equals(node.Name, baseName, StringComparison.OrdinalIgnoreCase));
            if (production != null)
                graph.Edges.Add(new CodeIntelligenceEdge(test.Id, production.Id, CodeIntelligenceEdgeKind.Tests, .85));
        }
    }
}
