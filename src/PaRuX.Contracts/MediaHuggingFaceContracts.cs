namespace PaRuX.Contracts;

/// <summary>
/// A single safetensors file of a Hugging Face repository, resolved by PaRuX Core (never by the client): the pinned
/// commit, the exact byte size from the repository listing, and the resolve URL PaRuX itself built from them.
/// </summary>
public sealed class HuggingFaceCheckpointResolution
{
    public string RepoId { get; set; } = string.Empty;
    /// <summary>Immutable commit SHA the listing was read at. The download must be pinned to it.</summary>
    public string Revision { get; set; } = string.Empty;
    /// <summary>Path of the file inside the repository, exactly as listed.</summary>
    public string FilePath { get; set; } = string.Empty;
    /// <summary>Last path segment: the name the file will have in the managed library.</summary>
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    /// <summary>Publisher-declared licence from the repository card, or null when none is declared.</summary>
    public string? License { get; set; }
}
