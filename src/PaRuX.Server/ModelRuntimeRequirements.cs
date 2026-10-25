using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>One data row: a model identity (GGUF architecture and/or repo-id pattern) and the oldest llama.cpp build known to load it.</summary>
/// <param name="Architecture">GGUF architecture name as llama.cpp reports it.</param>
/// <param name="RepoPattern">Matched against the Hugging Face repo id (owner/name), case-insensitive.</param>
/// <param name="MinimumBuild">Numeric part of the llama.cpp build tag (bNNNNN).</param>
/// <param name="Provenance">Where the minimum build comes from, so an unverified value is never presented as fact.</param>
public sealed record ModelRuntimeRequirement(string Architecture, Regex RepoPattern, int MinimumBuild, string Provenance, string DisplayName);

/// <summary>The installed text runtime as far as a suitability check needs it.</summary>
public sealed record InstalledRuntime(string? BuildTag, ComponentOwnership Ownership);

public enum RuntimeRequirementVerdict
{
    /// <summary>No known runtime requirement for this model.</summary>
    NotApplicable,
    Satisfied,
    /// <summary>The runtime is PaRuX-managed, so PaRuX may propose (not silently apply) an update.</summary>
    NeedsRuntimeUpdate,
    /// <summary>The runtime is user-managed or system-detected: tell the user, never modify it.</summary>
    NeedsUserAction
}

public sealed record ModelRuntimeAssessment(
    RuntimeRequirementVerdict Verdict,
    string Message,
    bool CanProposeManagedUpdate,
    int? RequiredBuild = null,
    string? Provenance = null);

/// <summary>
/// Data-driven "this model needs at least this llama.cpp build" table. It exists so a model whose GGUF
/// architecture is newer than the installed runtime is reported as an actionable runtime problem before
/// download/launch instead of failing at load with "unknown model architecture".
/// </summary>
public static class ModelRuntimeRequirements
{
    public static IReadOnlyList<ModelRuntimeRequirement> Table { get; } = new List<ModelRuntimeRequirement>
    {
        new(
            Architecture: "flint",
            RepoPattern: new Regex(@"(^|/)flint(-gguf)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            // Verified: architecture merged into llama.cpp master 2026-10-21 (ggml-org/llama.cpp#30928).
            // The first release build number below comes from a secondary source only.
            MinimumBuild: 11827,
            Provenance: "secondary-source, unverified: first included in llama.cpp build b11827 (architecture merged 2026-10-21 in ggml-org/llama.cpp#30928)",
            DisplayName: "Flint")
    };

    public static ModelRuntimeRequirement? Find(string? repoId, string? architecture)
    {
        foreach (ModelRuntimeRequirement row in Table)
        {
            if (!string.IsNullOrWhiteSpace(architecture)
                && string.Equals(architecture.Trim(), row.Architecture, StringComparison.OrdinalIgnoreCase))
                return row;
            if (!string.IsNullOrWhiteSpace(repoId) && row.RepoPattern.IsMatch(repoId.Trim()))
                return row;
        }
        return null;
    }

    /// <summary>Numeric part of a "bNNNN" llama.cpp tag; null for anything else (never guessed).</summary>
    public static int? TryParseBuild(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        Match m = Regex.Match(tag.Trim(), @"^b(\d{1,9})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : null;
    }

    public static ModelRuntimeAssessment Evaluate(
        string? repoId, string? architecture, string? installedBuildTag, ComponentOwnership ownership)
    {
        ModelRuntimeRequirement? req = Find(repoId, architecture);
        if (req is null)
            return new ModelRuntimeAssessment(RuntimeRequirementVerdict.NotApplicable, string.Empty, false);

        int? installed = TryParseBuild(installedBuildTag);
        if (installed is int have && have >= req.MinimumBuild)
            return new ModelRuntimeAssessment(RuntimeRequirementVerdict.Satisfied, string.Empty, false, req.MinimumBuild, req.Provenance);

        bool managed = ownership == ComponentOwnership.PARUX_MANAGED;
        string state = installed is int h
            ? $"installed build b{h} is older than the b{req.MinimumBuild} it needs"
            : "the installed llama.cpp build could not be determined, so support cannot be confirmed";
        string basis = $"{req.DisplayName} needs a newer llama.cpp runtime: {state} (minimum {req.Provenance}). ";

        return managed
            ? new ModelRuntimeAssessment(RuntimeRequirementVerdict.NeedsRuntimeUpdate,
                basis + "PaRuX can propose updating its managed runtime; nothing changes until you approve it.",
                CanProposeManagedUpdate: true, req.MinimumBuild, req.Provenance)
            : new ModelRuntimeAssessment(RuntimeRequirementVerdict.NeedsUserAction,
                basis + "This runtime is not managed by PaRuX, so it will not be modified. Update it yourself or choose a PaRuX-managed runtime.",
                CanProposeManagedUpdate: false, req.MinimumBuild, req.Provenance);
    }
}
