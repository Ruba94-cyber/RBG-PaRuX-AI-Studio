using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComfyProgressTests
{
    [Theory]
    [InlineData("{\"type\":\"progress\",\"data\":{\"value\":3,\"max\":12}}", 25)]
    [InlineData("{\"type\":\"progress_state\",\"data\":{\"nodes\":{\"8\":{\"state\":\"running\",\"value\":6.0,\"max\":12.0}}}}", 50)]
    public void Current_comfy_progress_formats_are_understood(string json, int expected)
    {
        Assert.Equal(expected, ComfyWorkflowRunner.TryParseProgressPercent(
            System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Single_shot_nodes_are_not_reported_as_sampler_progress()
    {
        const string json = "{\"type\":\"progress\",\"data\":{\"value\":1,\"max\":1}}";
        Assert.Null(ComfyWorkflowRunner.TryParseProgressPercent(
            System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData(49, 49)]
    [InlineData(25, 25)]
    [InlineData(22, 25)]
    [InlineData(1, 5)]
    public void Wan_clip_lengths_are_normalized_without_rendering_an_extra_full_tail(int requested, int expected)
    {
        Assert.Equal(expected, ComfyWorkflowRunner.NormalizeWanFrameCount(requested));
    }

    [Theory]
    [InlineData(120, "49,49,25", 121)]
    [InlineData(49, "49", 49)]
    [InlineData(50, "49,5", 53)]
    [InlineData(1, "5", 5)]
    public void Wan_clip_plan_accounts_for_conditioned_overlap(int requested, string expectedCounts, int expectedUniqueFrames)
    {
        int[] counts = ComfyWorkflowRunner.PlanWanClipFrameCounts(requested);
        Assert.Equal(expectedCounts.Split(',').Select(int.Parse), counts);
        Assert.Equal(expectedUniqueFrames, counts.Sum() - counts.Length + 1);
        Assert.True(counts.All(count => (count - 1) % 4 == 0));
    }

    [Fact]
    public void Wan_graph_adds_a_unique_load_image_only_when_a_continuity_frame_exists()
    {
        var request = System.Text.Json.Nodes.JsonNode.Parse("""
            {"prompt":{"6":{"class_type":"Wan22ImageToVideoLatent","inputs":{"length":49}},"11":{"class_type":"SaveVideo","inputs":{}}}}
            """)!.AsObject();

        Assert.Null(ComfyWorkflowRunner.AddWanStartImage(request, null));
        Assert.Equal("12", ComfyWorkflowRunner.AddWanStartImage(request, "continuity.png"));
        Assert.Equal("LoadImage", request["prompt"]!["12"]!["class_type"]!.GetValue<string>());
        Assert.Equal("continuity.png", request["prompt"]!["12"]!["inputs"]!["image"]!.GetValue<string>());
        Assert.Equal("12", request["prompt"]!["6"]!["inputs"]!["start_image"]![0]!.GetValue<string>());
        Assert.Equal(0, request["prompt"]!["6"]!["inputs"]!["start_image"]![1]!.GetValue<int>());
    }

    [Fact]
    public void Wan_concat_filter_drops_only_repeated_start_frames_and_resets_timing()
    {
        Assert.Equal("select=not(eq(n\\,49)+eq(n\\,98)),setpts=N/(24*TB)",
            ComfyWorkflowRunner.BuildWanFrameSelectFilter(new[] { 49, 49, 25 }, 24));
        Assert.Equal("setpts=N/(24*TB)", ComfyWorkflowRunner.BuildWanFrameSelectFilter(new[] { 49 }, 24));
    }

    [Fact]
    public void Wan_clip_output_validation_rejects_traversal_outside_paths_and_unowned_names()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-wan-path-" + Guid.NewGuid().ToString("N"));
        string jobId = Guid.NewGuid().ToString("N");
        using IDisposable localAiScope = LocalAiPaths.UseRootForTests(Path.Combine(root, "localai"));
        string output = Path.Combine(LocalAiPaths.ComfyOutputRoot, "LocalAI_videos");
        Directory.CreateDirectory(output);
        string ownedName = "PaRuX_" + jobId + "_clip_1_00001_.mp4";
        string ownedPath = Path.Combine(output, ownedName);
        File.WriteAllBytes(ownedPath, new byte[] { 1 });

        Assert.Equal(Path.GetFullPath(ownedPath), ComfyWorkflowRunner.ValidateOwnedWanClipOutput(ownedName, "LocalAI_videos", jobId, 1));
        Assert.Throws<InvalidDataException>(() => ComfyWorkflowRunner.ValidateOwnedWanClipOutput(ownedName, "../LocalAI_videos", jobId, 1));
        Assert.Throws<InvalidDataException>(() => ComfyWorkflowRunner.ValidateOwnedWanClipOutput(ownedPath, "LocalAI_videos", jobId, 1));

        string outside = Path.Combine(LocalAiPaths.ComfyOutputRoot, "other", ownedName);
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, new byte[] { 2 });
        Assert.Throws<InvalidDataException>(() => ComfyWorkflowRunner.ValidateOwnedWanClipOutput(ownedName, "other", jobId, 1));

        string unowned = Path.Combine(output, "PaRuX_" + Guid.NewGuid().ToString("N") + "_clip_1_00001_.mp4");
        File.WriteAllBytes(unowned, new byte[] { 3 });
        Assert.Throws<InvalidDataException>(() => ComfyWorkflowRunner.ValidateOwnedWanClipOutput(Path.GetFileName(unowned), "LocalAI_videos", jobId, 1));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(unowned));
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Cancelled_media_process_is_killed_and_partial_output_is_removed()
    {
        string path = Path.Combine(Path.GetTempPath(), "parux-media-partial-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        string command = "echo partial > \"" + path + "\" & ping -n 30 127.0.0.1 > nul";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComfyWorkflowRunner.RunMediaProcessAsync(
            "cmd.exe", new[] { "/d", "/c", command }, path, cancellation.Token));
        Assert.False(File.Exists(path));
    }
}
