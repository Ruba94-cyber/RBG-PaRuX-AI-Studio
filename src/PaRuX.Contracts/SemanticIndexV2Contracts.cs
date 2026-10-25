namespace PaRuX.Contracts;

public sealed class SemanticRepositoryRegistrationDto
{
    public string Alias { get; set; } = string.Empty;
    public string RepositoryRoot { get; set; } = string.Empty;
    public DateTime RegisteredUtc { get; set; }
    public DateTime? LastSyncedUtc { get; set; }
    public int IndexedFiles { get; set; }
}

public sealed class RegisterSemanticRepositoryRequest
{
    public string Alias { get; set; } = string.Empty;
    public string RepositoryRoot { get; set; } = string.Empty;
}

public sealed class SemanticIndexSyncRequest
{
    public List<string> Aliases { get; set; } = new();
    public bool ForceFull { get; set; }
    public bool VerifyAllHashes { get; set; }
}

public sealed class SemanticRepositorySyncResult
{
    public string Alias { get; set; } = string.Empty;
    public string RepositoryRoot { get; set; } = string.Empty;
    public int AddedFiles { get; set; }
    public int ChangedFiles { get; set; }
    public int RemovedFiles { get; set; }
    public int UnchangedFiles { get; set; }
    public int IndexedFiles { get; set; }
    public int IndexedSymbols { get; set; }
    public bool FullRebuild { get; set; }
    public DateTime SyncedUtc { get; set; }
}

public sealed class SemanticIndexSyncResponse
{
    public List<SemanticRepositorySyncResult> Repositories { get; set; } = new();
    public int AddedFiles => Repositories.Sum(x => x.AddedFiles);
    public int ChangedFiles => Repositories.Sum(x => x.ChangedFiles);
    public int RemovedFiles => Repositories.Sum(x => x.RemovedFiles);
    public int UnchangedFiles => Repositories.Sum(x => x.UnchangedFiles);
}

public sealed class SemanticIndexSearchRequest
{
    public string Query { get; set; } = string.Empty;
    /// <summary>Empty searches all registered repositories.</summary>
    public List<string> Aliases { get; set; } = new();
    public int MaxFiles { get; set; } = 12;
    public int MaxSymbols { get; set; } = 24;
}

public sealed class SemanticIndexSearchResponse
{
    public List<string> RepositoriesSearched { get; set; } = new();
    public List<string> RelevantFiles { get; set; } = new();
    public List<string> RelevantSymbols { get; set; } = new();
    public bool EmbeddingCapable { get; set; }
}
