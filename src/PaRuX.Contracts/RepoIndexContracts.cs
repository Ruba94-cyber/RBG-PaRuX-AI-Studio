namespace PaRuX.Contracts;

/// <summary>
/// Repository Intelligence builds on the existing Local Context Graph rather than a parallel store.
/// Files and symbols are durable graph entities. CONTAINS/TESTS remain the baseline topology;
/// DEPENDS_ON and CALLS add bounded, explainable navigation evidence where local imports/includes
/// or an unambiguous call target can be resolved. These relations are heuristic navigation aids,
/// not a compiler/AST claim. Search also uses bounded Git-history evidence only as a tie/quality
/// boost after lexical/embedding relevance exists, so frequently edited but irrelevant files do not
/// enter context merely because they are hot in history.
/// </summary>
public sealed class IndexRepositoryRequest
{
    public required string RepositoryPath { get; set; }
}

public sealed class RepoIndexResult
{
    public required int FilesIndexed { get; set; }
    public required int SymbolsIndexed { get; set; }
    public required int TestRelationsFound { get; set; }
    /// <summary>Resolved local import/include dependencies. Additive for older clients.</summary>
    public int DependencyRelationsFound { get; set; }
    /// <summary>File-to-symbol call edges with one unambiguous repository-local target.</summary>
    public int CallRelationsFound { get; set; }
    /// <summary>Files carrying bounded Git recency/frequency evidence; zero outside a Git worktree.</summary>
    public int FilesWithGitHistory { get; set; }
}

public sealed class RepoContextSearchRequest
{
    public required string RepositoryPath { get; set; }
    public required string GoalText { get; set; }
    public int MaxFiles { get; set; } = 8;
    public int MaxSymbols { get; set; } = 15;
}

/// <summary>A bounded context pack: the small, ranked slice of a repository judged most relevant
/// to a task goal. Repository-history evidence may reorder already relevant candidates but never
/// bypass relevance filtering.</summary>
public sealed class RepoContextPack
{
    public required List<string> RelevantFiles { get; set; }
    public required List<string> RelevantSymbols { get; set; }
}
