using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ModelStorageReclamationPlannerTests
{
    [Fact]
    public void Plan_separates_protected_entries_and_reports_reclaim_totals()
    {
        var entries = new[]
        {
            new ModelStorageReclamationInput(
                "/fixtures/models/failed.partial", 120, ModelStorageArtifactKind.FailedOrAbandonedPartialDownload),
            new ModelStorageReclamationInput(
                "/fixtures/models/failed.partial.source.json", 12,
                ModelStorageArtifactKind.FailedOrAbandonedPartialIdentitySidecar),
            new ModelStorageReclamationInput(
                "/fixtures/models/old.gguf", 800, ModelStorageArtifactKind.SupersededFile),
            new ModelStorageReclamationInput(
                "/fixtures/models/pinned.gguf", 500, ModelStorageArtifactKind.SupersededFile,
                ModelStorageProtectionFlags.ManuallyPinned)
        };

        ReclaimPlan plan = ModelStorageReclamationPlanner.Plan(entries);

        Assert.Equal(3, plan.TotalItemCount);
        Assert.Equal(932, plan.TotalBytes);
        Assert.Equal(1, plan.ProtectedItemCount);
        Assert.Equal(500, plan.ProtectedBytes);
        Assert.Contains(plan.Items, item => item.Path.EndsWith("failed.partial", StringComparison.Ordinal)
            && item.Bytes == 120 && item.ReasonCode == "failed_partial");
        Assert.Contains(plan.Items, item => item.Path.EndsWith("failed.partial.source.json", StringComparison.Ordinal)
            && item.Bytes == 12 && item.ReasonCode == "failed_partial");
        Assert.Contains(plan.Items, item => item.Path.EndsWith("old.gguf", StringComparison.Ordinal)
            && item.Bytes == 800 && item.ReasonCode == "superseded_unprotected");
        Assert.Equal("manually_pinned", Assert.Single(plan.ProtectedItems).ProtectionReasonCode);
    }

    [Fact]
    public void Every_declared_protection_combination_excludes_partial_and_superseded_entries()
    {
        const int flagCount = 6;
        var entries = new List<ModelStorageReclamationInput>();
        var kinds = new[]
        {
            ModelStorageArtifactKind.FailedOrAbandonedPartialDownload,
            ModelStorageArtifactKind.FailedOrAbandonedPartialIdentitySidecar,
            ModelStorageArtifactKind.SupersededFile
        };

        for (int mask = 1; mask < 1 << flagCount; mask++)
        {
            ModelStorageProtectionFlags flags = (ModelStorageProtectionFlags)mask;
            foreach (ModelStorageArtifactKind kind in kinds)
                entries.Add(new ModelStorageReclamationInput($"/fixtures/{mask}-{(int)kind}", 1, kind, flags));
        }

        ReclaimPlan plan = ModelStorageReclamationPlanner.Plan(entries);

        Assert.Empty(plan.Items);
        Assert.Equal(entries.Count, plan.ProtectedItemCount);
    }

    [Theory]
    [InlineData(ModelStorageProtectionFlags.ManuallyPinned, "manually_pinned")]
    [InlineData(ModelStorageProtectionFlags.UserOriginRoleOverride, "user_role_override")]
    [InlineData(ModelStorageProtectionFlags.ActiveRecommendation, "active_recommendation")]
    [InlineData(ModelStorageProtectionFlags.PreviousWinner, "previous_winner")]
    [InlineData(ModelStorageProtectionFlags.InUseByRuntime, "in_use_by_runtime")]
    [InlineData(ModelStorageProtectionFlags.ActiveDownload, "active_download")]
    public void Protected_entry_includes_its_protection_reason(
        ModelStorageProtectionFlags protectionFlag,
        string expectedReason)
    {
        ReclaimPlan plan = ModelStorageReclamationPlanner.Plan(new[]
        {
            new ModelStorageReclamationInput(
                "/fixtures/protected.gguf", 42, ModelStorageArtifactKind.SupersededFile, protectionFlag)
        });

        Assert.Empty(plan.Items);
        Assert.Equal(expectedReason, Assert.Single(plan.ProtectedItems).ProtectionReasonCode);
    }

    [Fact]
    public void Active_job_is_protected_even_when_entry_is_superseded()
    {
        ReclaimPlan plan = ModelStorageReclamationPlanner.Plan(new[]
        {
            new ModelStorageReclamationInput(
                "/fixtures/in-use.gguf", 64, ModelStorageArtifactKind.SupersededFile,
                IsActiveJob: true)
        });

        Assert.Empty(plan.Items);
        Assert.Equal("active_job", Assert.Single(plan.ProtectedItems).ProtectionReasonCode);
    }

    [Fact]
    public void Plan_is_repeatable_and_orders_candidates_by_reason_then_path()
    {
        var entries = new[]
        {
            new ModelStorageReclamationInput("/fixtures/z.gguf", 1, ModelStorageArtifactKind.SupersededFile),
            new ModelStorageReclamationInput("/fixtures/z.partial", 2, ModelStorageArtifactKind.FailedOrAbandonedPartialDownload),
            new ModelStorageReclamationInput("/fixtures/a.gguf", 3, ModelStorageArtifactKind.SupersededFile),
            new ModelStorageReclamationInput("/fixtures/b.partial.source.json", 4,
                ModelStorageArtifactKind.FailedOrAbandonedPartialIdentitySidecar),
            new ModelStorageReclamationInput("/fixtures/a.partial", 5,
                ModelStorageArtifactKind.FailedOrAbandonedPartialDownload),
            new ModelStorageReclamationInput("/fixtures/z-pinned.gguf", 6,
                ModelStorageArtifactKind.SupersededFile, ModelStorageProtectionFlags.ManuallyPinned),
            new ModelStorageReclamationInput("/fixtures/a-pinned.gguf", 7,
                ModelStorageArtifactKind.SupersededFile, ModelStorageProtectionFlags.ManuallyPinned),
            new ModelStorageReclamationInput("/fixtures/role-override.gguf", 8,
                ModelStorageArtifactKind.SupersededFile, ModelStorageProtectionFlags.UserOriginRoleOverride)
        };

        ReclaimPlan first = ModelStorageReclamationPlanner.Plan(entries);
        ReclaimPlan second = ModelStorageReclamationPlanner.Plan(entries);
        ReclaimPlan reversed = ModelStorageReclamationPlanner.Plan(entries.Reverse());

        Assert.Equal(first.Items, second.Items);
        Assert.Equal(first.Items, reversed.Items);
        Assert.Equal(first.ProtectedItems, second.ProtectedItems);
        Assert.Equal(first.ProtectedItems, reversed.ProtectedItems);
        Assert.Equal(new[]
        {
            "/fixtures/a.partial",
            "/fixtures/b.partial.source.json",
            "/fixtures/z.partial",
            "/fixtures/a.gguf",
            "/fixtures/z.gguf"
        }, first.Items.Select(item => item.Path));
        Assert.Equal(new[]
        {
            "failed_partial",
            "failed_partial",
            "failed_partial",
            "superseded_unprotected",
            "superseded_unprotected"
        }, first.Items.Select(item => item.ReasonCode));
        Assert.Equal(new[]
        {
            "manually_pinned:/fixtures/a-pinned.gguf",
            "manually_pinned:/fixtures/z-pinned.gguf",
            "user_role_override:/fixtures/role-override.gguf"
        }, first.ProtectedItems.Select(item => $"{item.ProtectionReasonCode}:{item.Path}"));
    }

    [Fact]
    public void Empty_input_produces_an_empty_plan()
    {
        ReclaimPlan plan = ModelStorageReclamationPlanner.Plan(Array.Empty<ModelStorageReclamationInput>());

        Assert.Empty(plan.Items);
        Assert.Empty(plan.ProtectedItems);
        Assert.Equal(0, plan.TotalItemCount);
        Assert.Equal(0, plan.TotalBytes);
        Assert.Equal(0, plan.ProtectedItemCount);
        Assert.Equal(0, plan.ProtectedBytes);
    }
}
