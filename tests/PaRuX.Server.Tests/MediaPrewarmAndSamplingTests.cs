using System.Text.Json.Nodes;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GpuMemoryReleaseWaiterTests
{
    private static Func<CancellationToken, Task<int?>> Sequence(params int?[] values)
    {
        int index = 0;
        return _ => Task.FromResult(values[Math.Min(Interlocked.Increment(ref index) - 1, values.Length - 1)]);
    }

    [Fact]
    public async Task A_card_that_has_stopped_moving_is_settled_after_one_interval()
    {
        using IDisposable sampler = GpuMemoryReleaseWaiter.OverrideSamplerForTests(Sequence(11_000, 11_020));

        GpuMemoryReleaseWaiter.Result result = await GpuMemoryReleaseWaiter.WaitForSettledAsync(
            CancellationToken.None, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5));

        Assert.True(result.Measured);
        Assert.True(result.Settled);
    }

    [Fact]
    public async Task A_card_still_releasing_is_waited_for_until_it_stops()
    {
        using IDisposable sampler = GpuMemoryReleaseWaiter.OverrideSamplerForTests(Sequence(14_000, 9_000, 4_000, 1_000, 900, 880));

        GpuMemoryReleaseWaiter.Result result = await GpuMemoryReleaseWaiter.WaitForSettledAsync(
            CancellationToken.None, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5));

        Assert.True(result.Settled);
        Assert.Equal(14_000, result.FirstUsedMib);
        Assert.True(result.LastUsedMib < 1_000);
    }

    [Fact]
    public async Task A_card_that_never_settles_ends_at_the_limit_without_failing()
    {
        int value = 20_000;
        using IDisposable sampler = GpuMemoryReleaseWaiter.OverrideSamplerForTests(_ => Task.FromResult<int?>(value -= 1_000));

        GpuMemoryReleaseWaiter.Result result = await GpuMemoryReleaseWaiter.WaitForSettledAsync(
            CancellationToken.None, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(10));

        Assert.True(result.Measured);
        Assert.False(result.Settled);
        Assert.True(result.Waited >= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task An_unreadable_card_does_not_wait_at_all()
    {
        using IDisposable sampler = GpuMemoryReleaseWaiter.OverrideSamplerForTests(Sequence((int?)null));

        GpuMemoryReleaseWaiter.Result result = await GpuMemoryReleaseWaiter.WaitForSettledAsync(CancellationToken.None);

        Assert.False(result.Measured);
        Assert.True(result.Waited < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Cancellation_ends_the_wait()
    {
        using var cancellation = new CancellationTokenSource();
        int value = 20_000;
        using IDisposable sampler = GpuMemoryReleaseWaiter.OverrideSamplerForTests(_ => { cancellation.Cancel(); return Task.FromResult<int?>(value -= 1_000); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GpuMemoryReleaseWaiter.WaitForSettledAsync(
            cancellation.Token, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5)));
    }

    [Fact]
    public void Every_text_launch_that_stops_comfy_waits_for_its_vram_first()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "PaRuX.Server", "LocalAiProcessManager.cs"));

        // StartTextAsync and the arbitrary-model start are the two paths that stop ComfyUI and then size a text model against the card.
        Assert.Equal(2, CountOf(source, "await StopMediaAwaitingVramReleaseAsync(ct)"));
        Assert.DoesNotContain("if (!StopMedia()) return new RuntimeResult(false, \"The media runtime did not stop, so local text", source, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) { count++; index += needle.Length; }
        return count;
    }

    internal static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PaRuX.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }
}

public sealed class MediaPrewarmServiceTests
{
    private sealed class Rig
    {
        public bool Gaming;
        public MediaExecutionCoordinator.Status Schedule = new(null, null, 0, 0);
        public int PrewarmCalls;
        public Func<Func<CancellationToken, Task<string?>>, CancellationToken, Task<LocalAiProcessManager.RuntimeResult>> Prewarm =
            (_, _) => Task.FromResult(new LocalAiProcessManager.RuntimeResult(true, "Started: ComfyUI"));
        public int Releases;
        public bool ReleaseResult = true;
        public DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        public CancellationTokenSource GamingToken = new();
        public TimeSpan IdleExpiry = TimeSpan.FromHours(1);
        public TimeSpan AttemptTimeout = TimeSpan.FromMinutes(4);

        public MediaPrewarmService Build() => new(new MediaPrewarmService.Hooks(
            () => Gaming,
            () => GamingToken.Token,
            () => Schedule,
            (admit, ct) => { Interlocked.Increment(ref PrewarmCalls); return Prewarm(admit, ct); },
            _ => Task.FromResult<string?>(null),
            _ => { Interlocked.Increment(ref Releases); return Task.FromResult(ReleaseResult); },
            () => Now,
            IdleExpiry,
            AttemptTimeout));
    }

    [Fact]
    public async Task A_request_returns_at_once_and_the_attempt_ends_warm()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource<LocalAiProcessManager.RuntimeResult>();
        rig.Prewarm = (_, _) => gate.Task;
        MediaPrewarmService service = rig.Build();

        MediaPrewarmService.PrewarmStatus started = service.Request("create-opened");
        Assert.Equal("starting", started.State);
        Assert.Equal("create-opened", started.Reason);

        gate.SetResult(new LocalAiProcessManager.RuntimeResult(true, "Started: ComfyUI"));
        await service.WhenIdleForTests();
        Assert.Equal("warm", service.Snapshot().State);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_attempt()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource<LocalAiProcessManager.RuntimeResult>();
        rig.Prewarm = (_, _) => gate.Task;
        MediaPrewarmService service = rig.Build();

        service.Request("a");
        service.Request("b");
        service.Request("c");
        gate.SetResult(new LocalAiProcessManager.RuntimeResult(true, "ok"));
        await service.WhenIdleForTests();

        Assert.Equal(1, rig.PrewarmCalls);
    }

    [Fact]
    public void Gaming_mode_refuses_without_starting_anything()
    {
        var rig = new Rig { Gaming = true };
        MediaPrewarmService service = rig.Build();

        MediaPrewarmService.PrewarmStatus status = service.Request("create-opened");

        Assert.Equal("skipped", status.State);
        Assert.Contains("Gaming", status.Message, StringComparison.Ordinal);
        Assert.Equal(0, rig.PrewarmCalls);
    }

    [Fact]
    public void A_busy_gpu_refuses_without_starting_anything()
    {
        var rig = new Rig { Schedule = new MediaExecutionCoordinator.Status("media:image", DateTime.UtcNow, 0, 0) };
        MediaPrewarmService service = rig.Build();

        Assert.Equal("skipped", service.Request("chat-media-intent").State);
        rig.Schedule = new MediaExecutionCoordinator.Status(null, null, 1, 0);
        Assert.Equal("skipped", service.Request("chat-media-intent").State);
        Assert.Equal(0, rig.PrewarmCalls);
    }

    [Fact]
    public async Task A_refusal_from_admission_is_reported_as_skipped_and_not_retried_inside_the_cooldown()
    {
        var rig = new Rig();
        rig.Prewarm = (_, _) => Task.FromResult(new LocalAiProcessManager.RuntimeResult(false, "Not prewarmed: only 900 MiB of VRAM is free."));
        MediaPrewarmService service = rig.Build();

        service.Request("create-opened");
        await service.WhenIdleForTests();
        MediaPrewarmService.PrewarmStatus first = service.Snapshot();
        MediaPrewarmService.PrewarmStatus again = service.Request("create-opened");

        Assert.Equal("skipped", first.State);
        Assert.Contains("900 MiB", first.Message, StringComparison.Ordinal);
        Assert.Equal("skipped", again.State);
        Assert.Equal(1, rig.PrewarmCalls);

        rig.Now += MediaPrewarmService.RetryCooldown + TimeSpan.FromSeconds(1);
        service.Request("create-opened");
        await service.WhenIdleForTests();
        Assert.Equal(2, rig.PrewarmCalls);
    }

    [Fact]
    public async Task A_launch_failure_is_reported_as_failed_not_warm()
    {
        var rig = new Rig();
        rig.Prewarm = (_, _) => Task.FromResult(new LocalAiProcessManager.RuntimeResult(false, "python.exe exited unexpectedly."));
        MediaPrewarmService service = rig.Build();

        service.Request("create-opened");
        await service.WhenIdleForTests();

        Assert.Equal("failed", service.Snapshot().State);
        Assert.Contains("exited unexpectedly", service.Snapshot().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gaming_mode_starting_mid_prewarm_cancels_it_honestly()
    {
        var rig = new Rig();
        rig.Prewarm = async (_, ct) =>
        {
            rig.Gaming = true;
            rig.GamingToken.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new LocalAiProcessManager.RuntimeResult(true, "unreachable");
        };
        MediaPrewarmService service = rig.Build();

        service.Request("create-opened");
        await service.WhenIdleForTests();

        MediaPrewarmService.PrewarmStatus status = service.Snapshot();
        Assert.Equal("cancelled", status.State);
        Assert.Contains("Gaming", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attempt_that_outlives_its_budget_is_cancelled_and_reported()
    {
        var rig = new Rig { AttemptTimeout = TimeSpan.FromMilliseconds(50) };
        rig.Prewarm = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new LocalAiProcessManager.RuntimeResult(true, "unreachable");
        };
        MediaPrewarmService service = rig.Build();

        service.Request("create-opened");
        await service.WhenIdleForTests();

        Assert.Equal("cancelled", service.Snapshot().State);
        Assert.Contains("in time", service.Snapshot().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeat_request_on_a_warm_runtime_verifies_it_instead_of_trusting_the_old_answer()
    {
        var rig = new Rig();
        MediaPrewarmService service = rig.Build();
        service.Request("create-opened");
        await service.WhenIdleForTests();
        Assert.Equal("warm", service.Snapshot().State);

        // ComfyUI was stopped by a text restore in the meantime: the repeat request must discover that, not report warm.
        rig.Prewarm = (_, _) => Task.FromResult(new LocalAiProcessManager.RuntimeResult(false, "Not prewarmed: only 100 MiB of VRAM is free."));
        MediaPrewarmService.PrewarmStatus during = service.Request("chat-media-intent");
        await service.WhenIdleForTests();

        Assert.Equal("warm", during.State); // unchanged until the check answers
        Assert.Equal(2, rig.PrewarmCalls);
        Assert.Equal("skipped", service.Snapshot().State);
    }

    [Fact]
    public async Task An_unused_warm_runtime_is_stopped_after_the_idle_window()
    {
        var rig = new Rig { IdleExpiry = TimeSpan.FromMilliseconds(30) };
        MediaPrewarmService service = rig.Build();

        service.Request("create-opened");
        await service.WhenIdleForTests();

        for (int i = 0; i < 200 && service.Snapshot().State == "warm"; i++) await Task.Delay(25);
        Assert.Equal("expired", service.Snapshot().State);
        Assert.Equal(1, rig.Releases);
    }

    [Theory]
    [InlineData("create-opened", "create-opened")]
    [InlineData("Chat Media Intent!", "chatmediaintent")]
    [InlineData(null, "")]
    public void The_reason_is_reduced_to_a_short_safe_token(string? input, string expected) =>
        Assert.Equal(expected, MediaPrewarmService.SanitizeReason(input));

    [Fact]
    public void The_hint_is_wired_to_create_and_to_media_chat_routes_and_served_to_the_desktop_owner_only()
    {
        string root = GpuMemoryReleaseWaiterTests.FindRepoRoot();
        string program = File.ReadAllText(Path.Combine(root, "src", "PaRuX.Server", "Program.cs"));
        Assert.Contains("MapPost(\"/api/v1/media/prewarm\"", program, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<MediaPrewarmService>", program, StringComparison.Ordinal);
        int post = program.IndexOf("MapPost(\"/api/v1/media/prewarm\"", StringComparison.Ordinal);
        Assert.Contains("RequireDesktopOwner()", program[post..(post + 400)], StringComparison.Ordinal);

        Assert.Contains("MediaPrewarmClient.Request(\"create-opened\")",
            File.ReadAllText(Path.Combine(root, "src", "PaRuX.App", "Views", "CreateWorkspaceView.cs")), StringComparison.Ordinal);
        Assert.Contains("MediaPrewarmClient.Request(\"chat-media-intent\")",
            File.ReadAllText(Path.Combine(root, "src", "PaRuX.App", "Services", "ChatSession.cs")), StringComparison.Ordinal);
    }
}

public sealed class ImageGraphSamplingTests
{
    private static JsonObject Template(string name)
    {
        string path = Path.Combine(GpuMemoryReleaseWaiterTests.FindRepoRoot(), "src", "PaRuX.Server", "Assets", "config", name);
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    [Theory]
    [InlineData("sd_xl_turbo_1.0_fp16.safetensors", 4, 1.0, "euler_ancestral")]
    [InlineData("sdxl_lightning_4step.safetensors", 4, 1.0, "euler")]
    [InlineData("LCM_Dreamshaper_v7.safetensors", 6, 1.5, "lcm")]
    public void A_distilled_sdxl_checkpoint_runs_its_few_step_settings(string file, int steps, double cfg, string sampler)
    {
        MediaSamplingHint? hint = MediaImageGraph.DistilledSdxlSampling("sdxl", file);

        Assert.NotNull(hint);
        Assert.Equal(steps, hint!.Steps);
        Assert.Equal(cfg, hint.Cfg);
        Assert.Equal(sampler, hint.Sampler);

        JsonObject request = Template("sdxl-checkpoint-api.json");
        MediaImageGraph.ApplySampling(request, hint);
        JsonObject sampling = request["prompt"]!["8"]!["inputs"]!.AsObject();
        Assert.Equal(steps, (int?)sampling["steps"]);
        Assert.Equal(cfg, (double?)sampling["cfg"]);
    }

    [Theory]
    [InlineData("sdxl", "sd_xl_base_1.0.safetensors")]
    [InlineData("sdxl", "Juggernaut-XL_v9.safetensors")]
    [InlineData("flux1-schnell", "flux1-schnell-turbo.safetensors")]
    [InlineData(null, "sd_xl_turbo_1.0_fp16.safetensors")]
    [InlineData("sdxl", "")]
    public void Anything_else_keeps_the_template_settings(string? family, string file) =>
        Assert.Null(MediaImageGraph.DistilledSdxlSampling(family, file));

    [Fact]
    public void An_activated_turbo_checkpoint_not_in_the_catalogue_is_sampled_at_four_steps_not_twenty_five()
    {
        var effective = new MediaCheckpointEffective("image", "sdxl-checkpoint-api.json", "sd_xl_turbo_1.0_fp16.safetensors", true,
            Array.Empty<string>(), null, FamilyId: "sdxl", LoaderNode: "1", LoaderClass: "CheckpointLoaderSimple", LoaderInput: "ckpt_name");
        JsonObject request = Template("sdxl-checkpoint-api.json");

        MediaImageGraph.ApplyEffective(request, effective);

        JsonObject prompt = request["prompt"]!.AsObject();
        Assert.Equal("sd_xl_turbo_1.0_fp16.safetensors", (string?)prompt["1"]!["inputs"]!["ckpt_name"]);
        Assert.Equal(4, (int?)prompt["8"]!["inputs"]!["steps"]);
        Assert.Equal(1.0, (double?)prompt["8"]!["inputs"]!["cfg"]);
    }

    [Fact]
    public void The_shipped_sdxl_template_matches_the_settings_its_catalogue_base_entry_recommends()
    {
        MediaSamplingHint expected = MediaCatalogue.Shipped.FindByFileName("sd_xl_base_1.0.safetensors")!.Value.Variant.Sampling!;
        JsonObject sampler = Template("sdxl-checkpoint-api.json")["prompt"]!["8"]!["inputs"]!.AsObject();

        Assert.Equal(expected.Steps, (int?)sampler["steps"]);
        Assert.Equal(expected.Cfg, (double?)sampler["cfg"]);
        Assert.Equal(expected.Sampler, (string?)sampler["sampler_name"]);
        Assert.Equal(expected.Scheduler, (string?)sampler["scheduler"]);
    }

    [Fact]
    public void The_qwen_image_template_asks_for_no_cpu_placement_or_tiling_and_the_cost_is_documented()
    {
        JsonObject prompt = Template("qwen-image-api.json")["prompt"]!.AsObject();

        Assert.Equal("default", (string?)prompt["1"]!["inputs"]!["weight_dtype"]);
        Assert.Equal("default", (string?)prompt["2"]!["inputs"]!["device"]);
        Assert.DoesNotContain(prompt, node => ((string?)node.Value!["class_type"] ?? "").Contains("Tiled", StringComparison.Ordinal));

        // The 20 GB of fp8 weights exceed a 16 GB card, so the offload is inherent; the catalogue says so with the measured time.
        string catalogue = File.ReadAllText(Path.Combine(GpuMemoryReleaseWaiterTests.FindRepoRoot(), "src", "PaRuX.Server", "Assets", "media", "media-catalogue.json"));
        Assert.Contains("about 52 s for a 1024 x 1024 image", catalogue, StringComparison.Ordinal);
    }
}
