using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ExecutionProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-exec-profile-" + Guid.NewGuid().ToString("N"));

    public ExecutionProfileStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Defaults_are_deterministic_and_quality_changes_resource_envelope()
    {
        var store = new ExecutionProfileStore(_root);
        ExecutionProfileStatus balanced = store.Get("model-a");
        ExecutionProfileStatus fast = store.SetQuality(ExecutionQualityProfile.Fast, "model-a");
        ExecutionProfileStatus maximum = store.SetQuality(ExecutionQualityProfile.MaximumQuality, "model-a");
        ExecutionProfileStatus overnight = store.SetQuality(ExecutionQualityProfile.Overnight, "model-a");

        Assert.Equal(ExecutionQualityProfile.Balanced, balanced.ActiveQualityProfile);
        Assert.True(fast.Effective.Budget.MaxWallClockMinutes < balanced.Effective.Budget.MaxWallClockMinutes);
        Assert.True(overnight.Effective.Budget.MaxToolSteps > balanced.Effective.Budget.MaxToolSteps);
        Assert.True(overnight.Effective.Budget.ContextFraction > fast.Effective.Budget.ContextFraction);
        Assert.Equal(16_384, fast.Effective.PreferredContextTokens);
        Assert.Equal(65_536, balanced.Effective.PreferredContextTokens);
        Assert.Equal(524_288, maximum.Effective.PreferredContextTokens);
        Assert.Equal(LongContextPerformancePlan.MaximumPaRuXContextTokens, overnight.Effective.PreferredContextTokens);
        Assert.InRange(overnight.Effective.Budget.MaxDirectSubagents, 0, 6);
        Assert.InRange(overnight.Effective.Budget.MaxDelegationDepth, 0, 3);
    }

    [Fact]
    public void Zero_value_override_falls_back_to_the_selected_quality_context_envelope()
    {
        var store = new ExecutionProfileStore(_root);
        ModelExecutionProfile saved = store.SetModelOverride(new ModelExecutionProfile
        {
            ModelKey = "overnight-model",
            QualityProfile = ExecutionQualityProfile.Overnight,
            PreferredContextTokens = 0,
            EvidenceSource = "manual-reviewed"
        });

        Assert.Equal(LongContextPerformancePlan.MaximumPaRuXContextTokens, saved.PreferredContextTokens);
    }

    [Fact]
    public void Model_override_is_persistent_bounded_and_preserves_benchmark_tuning_across_quality_switch()
    {
        var store = new ExecutionProfileStore(_root);
        ModelExecutionProfile tuned = store.UpsertBenchmarkRecommendation(
            "Qwen local", 131072, 42.5, structuredOutputReliable: false, "benchmark:run-42");

        var reloaded = new ExecutionProfileStore(_root);
        ExecutionProfileStatus before = reloaded.Get("Qwen local");
        reloaded.SetQuality(ExecutionQualityProfile.MaximumQuality);
        ExecutionProfileStatus after = reloaded.Get("Qwen local");

        Assert.Equal("validate-repair", tuned.StructuredOutputStrategy);
        Assert.Equal("parallel-evidence", tuned.ToolStrategy);
        Assert.Equal(131072, before.Effective.PreferredContextTokens);
        Assert.Equal("benchmark:run-42", before.Effective.EvidenceSource);
        Assert.Equal(before.Effective.PreferredContextTokens, after.Effective.PreferredContextTokens);
        Assert.Equal(ExecutionQualityProfile.MaximumQuality, after.Effective.QualityProfile);
        Assert.True(after.Effective.Budget.MaxWallClockMinutes > before.Effective.Budget.MaxWallClockMinutes);
    }

    [Fact]
    public void Manual_override_requires_evidence_and_is_clamped()
    {
        var store = new ExecutionProfileStore(_root);
        Assert.Throws<InvalidDataException>(() => store.SetModelOverride(new ModelExecutionProfile
        {
            ModelKey = "m",
            EvidenceSource = ""
        }));

        ModelExecutionProfile saved = store.SetModelOverride(new ModelExecutionProfile
        {
            ModelKey = "m",
            QualityProfile = ExecutionQualityProfile.Balanced,
            PreferredContextTokens = 99_999_999,
            RepositoryEvidenceCharacters = 99_999_999,
            ToolObservationCharacters = 99_999_999,
            DelegationThreshold = -20,
            ReasoningEffort = 99,
            EvidenceSource = "manual-reviewed",
            Budget = new ExecutionResourceBudget
            {
                MaxWallClockMinutes = 99999,
                MaxToolSteps = 999999,
                MaxDirectSubagents = 99,
                MaxDelegationDepth = 99,
                ContextFraction = 10,
                VerificationPasses = 99,
                MaxNetworkRestoreMinutes = 999,
                MaxArtifactBytes = long.MaxValue,
                MaxParallelTasks = 99,
                MaxGpuTemperatureC = 999,
                MaxSystemRamPercent = 999,
                MaxVramPercent = 999
            }
        });

        Assert.Equal(LongContextPerformancePlan.MaximumPaRuXContextTokens, saved.PreferredContextTokens);
        Assert.Equal(240_000, saved.RepositoryEvidenceCharacters);
        Assert.Equal(64_000, saved.ToolObservationCharacters);
        Assert.Equal(0, saved.DelegationThreshold);
        Assert.Equal(4, saved.ReasoningEffort);
        Assert.Equal(6, saved.Budget.MaxDirectSubagents);
        Assert.Equal(3, saved.Budget.MaxDelegationDepth);
        Assert.Equal(8, saved.Budget.MaxParallelTasks);
        Assert.Equal(95, saved.Budget.MaxGpuTemperatureC);
        Assert.Equal(98, saved.Budget.MaxSystemRamPercent);
        Assert.Equal(99, saved.Budget.MaxVramPercent);
    }

    [Fact]
    public void Backup_round_trip_replaces_active_quality_and_complete_override_set()
    {
        string sourceRoot = Path.Combine(_root, "source");
        string targetRoot = Path.Combine(_root, "target");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(targetRoot);
        var source = new ExecutionProfileStore(sourceRoot);
        source.SetQuality(ExecutionQualityProfile.Overnight);
        source.UpsertBenchmarkRecommendation("model-a", 131072, 50, structuredOutputReliable: true, "benchmark:a");
        ExecutionProfileStatus backup = source.ExportForBackup();

        var target = new ExecutionProfileStore(targetRoot);
        target.SetQuality(ExecutionQualityProfile.Fast);
        target.UpsertBenchmarkRecommendation("stale-model", 8192, 5, structuredOutputReliable: false, "benchmark:stale");
        target.ImportForBackup(backup);

        ExecutionProfileStatus restored = target.Get("model-a");
        Assert.Equal(ExecutionQualityProfile.Overnight, restored.ActiveQualityProfile);
        Assert.Single(restored.ModelOverrides);
        Assert.Equal("model-a", restored.ModelOverrides[0].ModelKey);
        Assert.Equal("benchmark:a", restored.ModelOverrides[0].EvidenceSource);
        Assert.Equal(131072, restored.Effective.PreferredContextTokens);
        Assert.DoesNotContain(restored.ModelOverrides, item => item.ModelKey == "stale-model");
    }

    [Fact]
    public void Backup_validation_rejects_unknown_quality_before_mutating_live_state()
    {
        var store = new ExecutionProfileStore(_root);
        store.SetQuality(ExecutionQualityProfile.Fast);
        var invalid = new ExecutionProfileStatus
        {
            ActiveQualityProfile = "turbo",
            ModelOverrides = new List<ModelExecutionProfile>()
        };

        Assert.Throws<InvalidDataException>(() => ExecutionProfileStore.ValidateBackupForImport(invalid));
        Assert.Equal(ExecutionQualityProfile.Fast, store.Get().ActiveQualityProfile);
    }

    [Fact]
    public void Corrupt_state_falls_back_without_destroying_service_availability()
    {
        File.WriteAllText(Path.Combine(_root, "execution-profiles.json"), "not-json");
        var store = new ExecutionProfileStore(_root);
        ExecutionProfileStatus status = store.Get();
        Assert.Equal(ExecutionQualityProfile.Balanced, status.ActiveQualityProfile);
        Assert.Equal("defaults", status.Effective.EvidenceSource);
    }

    [Theory]
    [InlineData("turbo")]
    [InlineData("")]
    public void Unknown_quality_profile_is_rejected(string value)
    {
        var store = new ExecutionProfileStore(_root);
        Assert.Throws<InvalidDataException>(() => store.SetQuality(value));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
