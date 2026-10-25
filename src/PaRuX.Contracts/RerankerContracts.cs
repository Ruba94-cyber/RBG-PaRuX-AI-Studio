namespace PaRuX.Contracts;

// ---- Reranker engine (Model Roles' RERANKER role, 2026-08-27) - a local cross-encoder used to
// ---- re-score an initial retrieval candidate set for higher precision. See RerankerService. ----

public sealed class RerankerEngineStatus
{
    /// <summary>True once the model + tokenizer files exist on disk.</summary>
    public bool Installed { get; set; }
    /// <summary>True once the ONNX session has actually been loaded into memory - distinct from
    /// Installed because a corrupt/incompatible download can be present but fail to load.</summary>
    public bool Loaded { get; set; }
    public string? LastError { get; set; }
}

public sealed class RerankRequest
{
    public string Query { get; set; } = string.Empty;
    public List<string> Documents { get; set; } = new();
    /// <summary>Caps how many of the top-scored documents are returned - reranking is meant to
    /// run over an already-small candidate set, not to be a bulk-scoring endpoint.</summary>
    public int? TopK { get; set; }
}

public sealed class RerankResult
{
    public int Index { get; set; }
    public string Document { get; set; } = string.Empty;
    public float Score { get; set; }
}
