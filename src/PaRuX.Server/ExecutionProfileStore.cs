using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Durable cross-surface execution profile store. The active quality profile is a user preference;
/// model overrides are evidence-backed tuning. Neither chooses a model or external provider.
/// Corrupt state fails soft to deterministic defaults and writes are atomic.
/// </summary>
public sealed class ExecutionProfileStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static ExecutionProfileStore Current { get; } = new(ServerState.StateDirectory);

    private sealed class State
    {
        public string ActiveQualityProfile { get; set; } = ExecutionQualityProfile.Balanced;
        public Dictionary<string, ModelExecutionProfile> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly string _filePath;
    private State? _cache;

    public ExecutionProfileStore(string stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory)) throw new ArgumentException("stateDirectory is required.", nameof(stateDirectory));
        _filePath = Path.Combine(Path.GetFullPath(stateDirectory), "execution-profiles.json");
    }

    public ExecutionProfileStatus Get(string? modelKey = null)
    {
        lock (Gate)
        {
            State state = LoadUnsafe();
            string quality = ExecutionQualityProfile.Normalize(state.ActiveQualityProfile);
            ModelExecutionProfile effective = ResolveUnsafe(state, modelKey, quality);
            return new ExecutionProfileStatus
            {
                ActiveQualityProfile = quality,
                Effective = Clone(effective),
                ModelOverrides = state.Models.Values.OrderBy(x => x.ModelKey, StringComparer.OrdinalIgnoreCase).Select(Clone).ToList()
            };
        }
    }

    public ExecutionProfileStatus SetQuality(string qualityProfile, string? modelKey = null)
    {
        if (!ExecutionQualityProfile.IsKnown(qualityProfile))
            throw new InvalidDataException("Unknown execution quality profile. Use fast, balanced, maximum-quality or overnight.");
        lock (Gate)
        {
            State state = LoadUnsafe();
            state.ActiveQualityProfile = ExecutionQualityProfile.Normalize(qualityProfile);
            SaveUnsafe(state);
            return Get(modelKey);
        }
    }

    public ModelExecutionProfile SetModelOverride(ModelExecutionProfile profile)
    {
        if (profile == null) throw new InvalidDataException("profile is required.");
        string key = NormalizeModelKey(profile.ModelKey);
        string quality = ExecutionQualityProfile.Normalize(profile.QualityProfile);
        ModelExecutionProfile normalized = ValidateAndNormalize(profile, key, quality, requireEvidence: true);
        lock (Gate)
        {
            State state = LoadUnsafe();
            state.Models[key] = normalized;
            SaveUnsafe(state);
            return Clone(normalized);
        }
    }

    public bool DeleteModelOverride(string modelKey)
    {
        string key = NormalizeModelKey(modelKey);
        lock (Gate)
        {
            State state = LoadUnsafe();
            if (!state.Models.Remove(key)) return false;
            SaveUnsafe(state);
            return true;
        }
    }

    /// <summary>
    /// Backup surface intentionally exports the user preference plus evidence-backed overrides,
    /// never the derived Effective profile (it is recomputed from those two inputs on restore).
    /// </summary>
    public ExecutionProfileStatus ExportForBackup() => Get();

    /// <summary>Validates an archive payload without mutating live state so BackupService can keep
    /// its phase-1 all-inputs-valid-before-any-swap guarantee.</summary>
    public static void ValidateBackupForImport(ExecutionProfileStatus backup) => _ = BuildStateFromBackup(backup);

    /// <summary>Atomically replaces both the active quality preference and the complete override
    /// set. Overrides absent from the backup are deliberately removed instead of leaking state from
    /// the machine being restored onto.</summary>
    public void ImportForBackup(ExecutionProfileStatus backup)
    {
        State restored = BuildStateFromBackup(backup);
        lock (Gate) SaveUnsafe(restored);
    }

    /// <summary>
    /// Records benchmark-informed tuning without pretending benchmark evidence exists when it does
    /// not. The caller supplies observed model context and decode speed from PaRuX's own benchmark.
    /// </summary>
    public ModelExecutionProfile UpsertBenchmarkRecommendation(
        string modelKey,
        int observedContextTokens,
        double decodeTokensPerSecond,
        bool structuredOutputReliable,
        string evidenceSource)
    {
        string key = NormalizeModelKey(modelKey);
        if (observedContextTokens < 0) throw new ArgumentOutOfRangeException(nameof(observedContextTokens));
        if (decodeTokensPerSecond < 0 || double.IsNaN(decodeTokensPerSecond) || double.IsInfinity(decodeTokensPerSecond))
            throw new ArgumentOutOfRangeException(nameof(decodeTokensPerSecond));
        if (string.IsNullOrWhiteSpace(evidenceSource)) throw new InvalidDataException("Benchmark evidence source is required.");

        lock (Gate)
        {
            State state = LoadUnsafe();
            string quality = ExecutionQualityProfile.Normalize(state.ActiveQualityProfile);
            ModelExecutionProfile baseline = BuildDefault(key, quality);
            int context = observedContextTokens > 0 ? observedContextTokens : baseline.PreferredContextTokens;
            baseline.PreferredContextTokens = Math.Clamp(context, 2_048, LongContextPerformancePlan.MaximumPaRuXContextTokens);
            baseline.RepositoryEvidenceCharacters = Math.Clamp((int)Math.Round(baseline.PreferredContextTokens * 3.0 * 0.24), 12_000, 240_000);
            baseline.ToolObservationCharacters = decodeTokensPerSecond > 50 ? 16_000 : decodeTokensPerSecond > 20 ? 10_000 : 6_000;
            baseline.StructuredOutputStrategy = structuredOutputReliable ? "schema-first" : "validate-repair";
            baseline.ToolStrategy = decodeTokensPerSecond >= 35 ? "parallel-evidence" : "serial-evidence";
            baseline.DelegationThreshold = decodeTokensPerSecond >= 35 ? 55 : decodeTokensPerSecond >= 15 ? 65 : 78;
            baseline.ReasoningEffort = decodeTokensPerSecond >= 25 ? 3 : 2;
            baseline.EvidenceSource = Bound(evidenceSource, 256);
            baseline.UpdatedUtc = DateTime.UtcNow;
            state.Models[key] = ValidateAndNormalize(baseline, key, quality, requireEvidence: true);
            SaveUnsafe(state);
            return Clone(state.Models[key]);
        }
    }

    public static ModelExecutionProfile BuildDefault(string? modelKey, string? qualityProfile)
    {
        string key = string.IsNullOrWhiteSpace(modelKey) ? "active" : modelKey.Trim();
        string quality = ExecutionQualityProfile.Normalize(qualityProfile);
        ExecutionResourceBudget budget = quality switch
        {
            ExecutionQualityProfile.Fast => new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 15, MaxToolSteps = 120, MaxDirectSubagents = 2, MaxDelegationDepth = 2,
                ContextFraction = 0.35, VerificationPasses = 1, MaxNetworkRestoreMinutes = 8,
                MaxArtifactBytes = 256L * 1024 * 1024, MaxParallelTasks = 2, MaxGpuTemperatureC = 84,
                MaxSystemRamPercent = 82, MaxVramPercent = 88
            },
            ExecutionQualityProfile.MaximumQuality => new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 180, MaxToolSteps = 4_000, MaxDirectSubagents = 6, MaxDelegationDepth = 3,
                ContextFraction = 0.70, VerificationPasses = 2, MaxNetworkRestoreMinutes = 20,
                MaxArtifactBytes = 2L * 1024 * 1024 * 1024, MaxParallelTasks = 4, MaxGpuTemperatureC = 86,
                MaxSystemRamPercent = 90, MaxVramPercent = 94
            },
            ExecutionQualityProfile.Overnight => new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 720, MaxToolSteps = 10_000, MaxDirectSubagents = 6, MaxDelegationDepth = 3,
                ContextFraction = 0.78, VerificationPasses = 3, MaxNetworkRestoreMinutes = 30,
                MaxArtifactBytes = 4L * 1024 * 1024 * 1024, MaxParallelTasks = 6, MaxGpuTemperatureC = 84,
                MaxSystemRamPercent = 92, MaxVramPercent = 96
            },
            _ => new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 60, MaxToolSteps = 1_000, MaxDirectSubagents = 4, MaxDelegationDepth = 3,
                ContextFraction = 0.50, VerificationPasses = 1, MaxNetworkRestoreMinutes = 15,
                MaxArtifactBytes = 1L * 1024 * 1024 * 1024, MaxParallelTasks = 3, MaxGpuTemperatureC = 85,
                MaxSystemRamPercent = 86, MaxVramPercent = 92
            }
        };

        int preferredContext = quality switch
        {
            ExecutionQualityProfile.Fast => 16_384,
            ExecutionQualityProfile.MaximumQuality => 524_288,
            ExecutionQualityProfile.Overnight => LongContextPerformancePlan.MaximumPaRuXContextTokens,
            _ => 65_536
        };
        int repositoryEvidence = quality switch
        {
            ExecutionQualityProfile.Fast => 24_000,
            ExecutionQualityProfile.MaximumQuality => 180_000,
            ExecutionQualityProfile.Overnight => 240_000,
            _ => 96_000
        };
        int toolObservations = quality switch
        {
            ExecutionQualityProfile.Fast => 6_000,
            ExecutionQualityProfile.MaximumQuality => 16_000,
            ExecutionQualityProfile.Overnight => 24_000,
            _ => 12_000
        };

        return new ModelExecutionProfile
        {
            ModelKey = key,
            QualityProfile = quality,
            StructuredOutputStrategy = "schema-first",
            ToolStrategy = "adaptive",
            // This is an upper envelope, not a promise to allocate it. ContextPreferenceLaunch can
            // only reduce the model-aware measured profile; the GGUF/KV capacity planner therefore
            // remains the safety authority. Fast/Balanced stay latency-oriented, while high-quality
            // tiers no longer silently collapse a validated 524k/1M runtime back to 65k/131k.
            PreferredContextTokens = preferredContext,
            RepositoryEvidenceCharacters = repositoryEvidence,
            ToolObservationCharacters = toolObservations,
            DelegationThreshold = quality == ExecutionQualityProfile.Fast ? 78 : quality == ExecutionQualityProfile.MaximumQuality ? 55 : quality == ExecutionQualityProfile.Overnight ? 50 : 65,
            ReasoningEffort = quality == ExecutionQualityProfile.Fast ? 1 : quality == ExecutionQualityProfile.Balanced ? 2 : 3,
            Budget = budget,
            EvidenceSource = "defaults",
            UpdatedUtc = DateTime.UtcNow
        };
    }

    private ModelExecutionProfile ResolveUnsafe(State state, string? modelKey, string quality)
    {
        if (!string.IsNullOrWhiteSpace(modelKey))
        {
            string key = NormalizeModelKey(modelKey);
            if (state.Models.TryGetValue(key, out ModelExecutionProfile? existing))
            {
                ModelExecutionProfile clone = Clone(existing);
                if (!string.Equals(clone.QualityProfile, quality, StringComparison.Ordinal))
                {
                    // Keep benchmark-derived model tuning but apply the user's current resource envelope.
                    ModelExecutionProfile envelope = BuildDefault(key, quality);
                    clone.QualityProfile = quality;
                    clone.Budget = envelope.Budget;
                    clone.UpdatedUtc = existing.UpdatedUtc;
                }
                return clone;
            }
            return BuildDefault(key, quality);
        }
        return BuildDefault("active", quality);
    }

    private State LoadUnsafe()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(_filePath))
            {
                State? parsed = JsonSerializer.Deserialize<State>(File.ReadAllText(_filePath), JsonOptions);
                if (parsed != null)
                {
                    parsed.ActiveQualityProfile = ExecutionQualityProfile.Normalize(parsed.ActiveQualityProfile);
                    parsed.Models = new Dictionary<string, ModelExecutionProfile>(parsed.Models ?? new(), StringComparer.OrdinalIgnoreCase);
                    _cache = parsed;
                    return _cache;
                }
            }
        }
        catch { }
        _cache = new State();
        return _cache;
    }

    private void SaveUnsafe(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string temp = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, _filePath, overwrite: true);
        _cache = state;
    }

    private static State BuildStateFromBackup(ExecutionProfileStatus backup)
    {
        if (backup == null) throw new InvalidDataException("execution profile backup is required.");
        if (!ExecutionQualityProfile.IsKnown(backup.ActiveQualityProfile))
            throw new InvalidDataException("execution profile backup contains an unknown active quality profile.");

        var restored = new State
        {
            ActiveQualityProfile = ExecutionQualityProfile.Normalize(backup.ActiveQualityProfile),
            Models = new Dictionary<string, ModelExecutionProfile>(StringComparer.OrdinalIgnoreCase)
        };
        foreach (ModelExecutionProfile source in backup.ModelOverrides ?? new List<ModelExecutionProfile>())
        {
            if (source == null) throw new InvalidDataException("execution profile backup contains a null model override.");
            if (!ExecutionQualityProfile.IsKnown(source.QualityProfile))
                throw new InvalidDataException("execution profile backup contains an unknown model quality profile.");
            string key = NormalizeModelKey(source.ModelKey);
            if (restored.Models.ContainsKey(key))
                throw new InvalidDataException("execution profile backup contains duplicate model keys.");
            ModelExecutionProfile normalized = ValidateAndNormalize(
                source, key, ExecutionQualityProfile.Normalize(source.QualityProfile), requireEvidence: true);
            normalized.UpdatedUtc = source.UpdatedUtc == default ? DateTime.UtcNow : source.UpdatedUtc;
            restored.Models.Add(key, normalized);
        }
        return restored;
    }

    private static ModelExecutionProfile ValidateAndNormalize(ModelExecutionProfile source, string key, string quality, bool requireEvidence)
    {
        if (requireEvidence && string.IsNullOrWhiteSpace(source.EvidenceSource))
            throw new InvalidDataException("A model execution override must record its evidence source.");
        ModelExecutionProfile fallbackProfile = BuildDefault(key, quality);
        ExecutionResourceBudget fallback = fallbackProfile.Budget;
        ExecutionResourceBudget b = source.Budget ?? fallback;
        return new ModelExecutionProfile
        {
            ModelKey = key,
            QualityProfile = quality,
            StructuredOutputStrategy = source.StructuredOutputStrategy is "schema-first" or "validate-repair" ? source.StructuredOutputStrategy : "schema-first",
            ToolStrategy = source.ToolStrategy is "adaptive" or "parallel-evidence" or "serial-evidence" ? source.ToolStrategy : "adaptive",
            PreferredContextTokens = Math.Clamp(
                source.PreferredContextTokens <= 0 ? fallbackProfile.PreferredContextTokens : source.PreferredContextTokens,
                2_048,
                LongContextPerformancePlan.MaximumPaRuXContextTokens),
            RepositoryEvidenceCharacters = Math.Clamp(
                source.RepositoryEvidenceCharacters <= 0 ? fallbackProfile.RepositoryEvidenceCharacters : source.RepositoryEvidenceCharacters,
                8_000,
                240_000),
            ToolObservationCharacters = Math.Clamp(
                source.ToolObservationCharacters <= 0 ? fallbackProfile.ToolObservationCharacters : source.ToolObservationCharacters,
                2_000,
                64_000),
            DelegationThreshold = Math.Clamp(source.DelegationThreshold, 0, 100),
            ReasoningEffort = Math.Clamp(source.ReasoningEffort, 0, 4),
            Budget = new ExecutionResourceBudget
            {
                MaxWallClockMinutes = Math.Clamp(b.MaxWallClockMinutes <= 0 ? fallback.MaxWallClockMinutes : b.MaxWallClockMinutes, 1, 1440),
                MaxToolSteps = Math.Clamp(b.MaxToolSteps <= 0 ? fallback.MaxToolSteps : b.MaxToolSteps, 1, 20_000),
                MaxDirectSubagents = Math.Clamp(b.MaxDirectSubagents, 0, 6),
                MaxDelegationDepth = Math.Clamp(b.MaxDelegationDepth, 0, 3),
                ContextFraction = Math.Clamp(b.ContextFraction <= 0 ? fallback.ContextFraction : b.ContextFraction, 0.10, 0.80),
                VerificationPasses = Math.Clamp(b.VerificationPasses <= 0 ? fallback.VerificationPasses : b.VerificationPasses, 1, 5),
                MaxNetworkRestoreMinutes = Math.Clamp(b.MaxNetworkRestoreMinutes <= 0 ? fallback.MaxNetworkRestoreMinutes : b.MaxNetworkRestoreMinutes, 1, 60),
                MaxArtifactBytes = Math.Clamp(b.MaxArtifactBytes <= 0 ? fallback.MaxArtifactBytes : b.MaxArtifactBytes, 16L * 1024 * 1024, 8L * 1024 * 1024 * 1024),
                MaxParallelTasks = Math.Clamp(b.MaxParallelTasks <= 0 ? fallback.MaxParallelTasks : b.MaxParallelTasks, 1, 8),
                MaxGpuTemperatureC = Math.Clamp(b.MaxGpuTemperatureC <= 0 ? fallback.MaxGpuTemperatureC : b.MaxGpuTemperatureC, 60, 95),
                MaxSystemRamPercent = Math.Clamp(b.MaxSystemRamPercent <= 0 ? fallback.MaxSystemRamPercent : b.MaxSystemRamPercent, 40, 98),
                MaxVramPercent = Math.Clamp(b.MaxVramPercent <= 0 ? fallback.MaxVramPercent : b.MaxVramPercent, 40, 99)
            },
            EvidenceSource = Bound(source.EvidenceSource, 256),
            UpdatedUtc = DateTime.UtcNow
        };
    }

    private static string NormalizeModelKey(string? value)
    {
        string key = (value ?? string.Empty).Trim();
        if (key.Length == 0 || key.Length > 512) throw new InvalidDataException("modelKey is required and must be <= 512 characters.");
        if (key.Any(char.IsControl)) throw new InvalidDataException("modelKey contains control characters.");
        return key;
    }

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];

    private static ModelExecutionProfile Clone(ModelExecutionProfile p) => new()
    {
        ModelKey = p.ModelKey,
        QualityProfile = p.QualityProfile,
        StructuredOutputStrategy = p.StructuredOutputStrategy,
        ToolStrategy = p.ToolStrategy,
        PreferredContextTokens = p.PreferredContextTokens,
        RepositoryEvidenceCharacters = p.RepositoryEvidenceCharacters,
        ToolObservationCharacters = p.ToolObservationCharacters,
        DelegationThreshold = p.DelegationThreshold,
        ReasoningEffort = p.ReasoningEffort,
        Budget = new ExecutionResourceBudget
        {
            MaxWallClockMinutes = p.Budget.MaxWallClockMinutes,
            MaxToolSteps = p.Budget.MaxToolSteps,
            MaxDirectSubagents = p.Budget.MaxDirectSubagents,
            MaxDelegationDepth = p.Budget.MaxDelegationDepth,
            ContextFraction = p.Budget.ContextFraction,
            VerificationPasses = p.Budget.VerificationPasses,
            MaxNetworkRestoreMinutes = p.Budget.MaxNetworkRestoreMinutes,
            MaxArtifactBytes = p.Budget.MaxArtifactBytes,
            MaxParallelTasks = p.Budget.MaxParallelTasks,
            MaxGpuTemperatureC = p.Budget.MaxGpuTemperatureC,
            MaxSystemRamPercent = p.Budget.MaxSystemRamPercent,
            MaxVramPercent = p.Budget.MaxVramPercent
        },
        EvidenceSource = p.EvidenceSource,
        UpdatedUtc = p.UpdatedUtc
    };
}
