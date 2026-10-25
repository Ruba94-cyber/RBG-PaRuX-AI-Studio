using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    /// <summary>App-side mirror of GET /api/v1/maintenance/status (camelCase JSON). Only the members Model Hub
    /// reads are mirrored; unknown members are ignored so the server may evolve additively.</summary>
    public sealed class SuggestionFeedStatusDto
    {
        public SuggestionFeedReportDto? LastReport { get; set; }
        public SuggestionFeedFingerprintDto? Fingerprint { get; set; }
    }

    public sealed class SuggestionFeedFingerprintDto
    {
        public List<SuggestionFeedAdapterDto> Adapters { get; set; } = new();
        public long SystemRamBytes { get; set; }
    }

    public sealed class SuggestionFeedAdapterDto
    {
        public string Name { get; set; } = string.Empty;
        public long DedicatedVramBytes { get; set; }
    }

    public sealed class SuggestionFeedReportDto
    {
        public DateTime FinishedUtc { get; set; }
        public string Trigger { get; set; } = string.Empty;
        /// <summary>ok | no_change | skipped_gaming | skipped_disabled | busy | error</summary>
        public string Outcome { get; set; } = string.Empty;
        /// <summary>Discovery refresh result, e.g. "ok" or "failed:offline"; failed means the last good catalogue was kept.</summary>
        public string? DiscoveryOutcome { get; set; }
        public List<SuggestionFeedProposalDto> Proposals { get; set; } = new();
    }

    public sealed class SuggestionFeedProposalDto
    {
        /// <summary>new_candidate | candidate_needs_runtime_update | runtime_update | runtime_note</summary>
        public string Kind { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string? Repo { get; set; }
        public string? Revision { get; set; }
        public List<string> Cohorts { get; set; } = new();
        public long? SizeBytes { get; set; }
        /// <summary>FitsOnGpu | SplitWithRam | CpuOnly (server ModelSuitability name).</summary>
        public string? FitClass { get; set; }
        public string? License { get; set; }
        public string? Capability { get; set; }
        public string? Reason { get; set; }
        /// <summary>standard | uncensored (declared by repository, file or tags).</summary>
        public string? Lane { get; set; }
        public string? LaneEvidence { get; set; }
    }
}
