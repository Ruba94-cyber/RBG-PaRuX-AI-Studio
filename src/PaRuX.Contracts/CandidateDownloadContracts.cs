using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaRuX.Contracts;

/// <summary>Explicit one-time consent evidence for starting a pinned Candidate transfer.</summary>
public sealed class StartCandidateDownloadRequest
{
    public ModelDownloadLicenseEvidence? LicenseEvidence { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? OtherFields { get; set; }
}
