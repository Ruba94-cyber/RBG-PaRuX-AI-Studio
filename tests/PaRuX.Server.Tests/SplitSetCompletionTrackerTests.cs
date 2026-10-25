using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SplitSetCompletionTrackerTests
{
    [Fact]
    public void Recomputed_tracker_counts_parts_already_landed_before_restart_and_claims_completion_once()
    {
        var tracker = new SplitSetCompletionTracker(
            new[] { "model-00001-of-00003.gguf", "model-00002-of-00003.gguf", "model-00003-of-00003.gguf" },
            new[] { "model-00001-of-00003.gguf", "not-in-this-plan.gguf" },
            "model-00001-of-00003.gguf",
            "model");

        Assert.False(tracker.TryMarkLanded("model-00002-of-00003.gguf"));
        Assert.True(tracker.TryMarkLanded("model-00003-of-00003.gguf"));
        Assert.False(tracker.TryMarkLanded("model-00003-of-00003.gguf"));
        Assert.False(tracker.TryMarkLanded("not-in-this-plan.gguf"));
    }
}
