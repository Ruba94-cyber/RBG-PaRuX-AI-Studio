using System.Text.Json.Nodes;

namespace PaRuX.Server;

/// <summary>One input of one node in a video workflow template.</summary>
internal readonly record struct VideoNodeInput(string Node, string Input);

/// <summary>How a start image (the user's image, or the previous clip's last frame) reaches the graph.</summary>
internal enum VideoStartImageMode
{
    /// <summary>The text-to-video template takes it: a LoadImage node is added and linked to <see cref="VideoFamilyContract.StartImageLatent"/>.</summary>
    AttachToLatent,
    /// <summary>The family ships a second template that already holds a LoadImage node (<see cref="VideoFamilyContract.StartImageLoadNode"/>).</summary>
    SeparateTemplate
}

/// <summary>What one clip of a video job is made of, so the runner can fill any family's graph.</summary>
internal sealed record VideoClipSettings(
    string Prompt, long Seed, int Width, int Height, int FrameCount, int Steps, int Fps,
    string JobId, int ClipIndex, string? StartImageName);

/// <summary>
/// The node-level contract of one video family: which loader reads the checkpoint, which nodes carry the prompt, seed, size,
/// frame count, steps, frame rate and start image, where the finished file is saved, and how long one clip may be. The video
/// runner reads this instead of assuming the Wan 2.2 graph, so a family is added by declaring a template and a contract here
/// (the templates themselves come from the companion registry), not by editing the runner. The contract of a job is chosen by
/// the family of the ACTIVE video checkpoint (see <see cref="VideoFamilyContracts.For"/>).
/// </summary>
internal sealed record VideoFamilyContract(
    string FamilyId,
    string LoaderNode,
    string LoaderClass,
    string LoaderInput,
    IReadOnlyList<VideoNodeInput> Prompt,
    IReadOnlyList<VideoNodeInput> Seed,
    IReadOnlyList<VideoNodeInput> Width,
    IReadOnlyList<VideoNodeInput> Height,
    IReadOnlyList<VideoNodeInput> Length,
    IReadOnlyList<VideoNodeInput> Steps,
    IReadOnlyList<VideoNodeInput> Fps,
    VideoNodeInput FilenamePrefix,
    string OutputNode,
    int MinClipFrames,
    int MaxClipFrames,
    int FrameQuantum,
    int SizeMultiple,
    VideoStartImageMode StartImageMode,
    VideoNodeInput StartImageLatent,
    string? StartImageLoadNode)
{
    /// <summary>The nearest legal clip length at or above <paramref name="requested"/>: 1 + quantum*n, within the family's range.</summary>
    internal int NormalizeFrameCount(int requested)
    {
        int bounded = Math.Clamp(requested, MinClipFrames, MaxClipFrames);
        return 1 + (int)(FrameQuantum * Math.Ceiling((bounded - 1) / (double)FrameQuantum));
    }

    /// <summary>Clip lengths for a video of <paramref name="requestedFrames"/> unique frames. Clips after the first repeat the
    /// previous clip's last frame (it is the start image), which the finalizer drops again.</summary>
    internal int[] PlanClipFrameCounts(int requestedFrames)
    {
        int remainingUniqueFrames = Math.Max(1, requestedFrames);
        var counts = new List<int>();
        while (remainingUniqueFrames > 0)
        {
            int wanted = Math.Min(MaxClipFrames, remainingUniqueFrames + (counts.Count == 0 ? 0 : 1));
            int clipFrames = NormalizeFrameCount(wanted);
            counts.Add(clipFrames);
            remainingUniqueFrames -= clipFrames - (counts.Count == 1 ? 0 : 1);
        }
        return counts.ToArray();
    }

    /// <summary>The size the graph is given: rounded down to the family's multiple. Identity for a multiple of 1.</summary>
    internal (int Width, int Height) FitSize(int width, int height) =>
        SizeMultiple <= 1 ? (width, height)
            : (Math.Max(SizeMultiple, width - width % SizeMultiple), Math.Max(SizeMultiple, height - height % SizeMultiple));

    /// <summary>Fills the family graph for one clip. The checkpoint file is patched separately (<see cref="ApplyCheckpoint"/>).</summary>
    internal void ApplyClip(JsonObject request, VideoClipSettings clip)
    {
        Set(request, Prompt, clip.Prompt);
        Set(request, Seed, clip.Seed);
        Set(request, Width, clip.Width);
        Set(request, Height, clip.Height);
        Set(request, Length, clip.FrameCount);
        Set(request, Steps, clip.Steps);
        Set(request, Fps, (double)clip.Fps);
        Set(request, new[] { FilenamePrefix }, "LocalAI_videos/PaRuX_" + clip.JobId + "_clip_" + (clip.ClipIndex + 1));
        if (clip.StartImageName != null) ApplyStartImage(request, clip.StartImageName);
    }

    /// <summary>Points the loader at the job's checkpoint file.</summary>
    internal void ApplyCheckpoint(JsonObject request, MediaCheckpointEffective effective)
    {
        // The resolution carries the family's own loader (UNETLoader.unet_name, or CheckpointLoaderSimple.ckpt_name for a single-file checkpoint).
        if (effective.IsOverridden) Set(request, new[] { new VideoNodeInput(effective.LoaderNode, effective.LoaderInput) }, effective.File);
    }

    internal string? ApplyStartImage(JsonObject request, string uploadedImageName)
    {
        if (StartImageMode == VideoStartImageMode.AttachToLatent)
            return ComfyWorkflowRunner.AddWanStartImage(request, uploadedImageName);
        Set(request, new[] { new VideoNodeInput(StartImageLoadNode!, "image") }, uploadedImageName);
        return StartImageLoadNode;
    }

    /// <summary>The template file a clip submits: the start-image template when the family has one and the clip has a start image.</summary>
    internal string TemplateFileFor(RegistryFamily family, bool hasStartImage)
    {
        if (hasStartImage && StartImageMode == VideoStartImageMode.SeparateTemplate)
            return family.StartImageTemplate
                ?? throw new InvalidOperationException(family.DisplayName + " declares no start-image template.");
        return family.TemplateFor("video") ?? throw new InvalidOperationException(family.DisplayName + " has no video template.");
    }

    private static void Set(JsonObject request, IReadOnlyList<VideoNodeInput> targets, object value)
    {
        foreach (VideoNodeInput target in targets)
        {
            JsonObject inputs = request["prompt"]?[target.Node]?["inputs"] as JsonObject
                ?? throw new InvalidDataException("The video workflow has no node " + target.Node + ".");
            inputs[target.Input] = value switch
            {
                string s => JsonValue.Create(s),
                long l => JsonValue.Create(l),
                int i => JsonValue.Create(i),
                double d => JsonValue.Create(d),
                _ => throw new ArgumentException("Unsupported workflow value type: " + value.GetType())
            };
        }
    }
}

/// <summary>The video families PaRuX can run, and the choice of one for a job.</summary>
internal static class VideoFamilyContracts
{
    private static VideoNodeInput[] One(string node, string input) => new[] { new VideoNodeInput(node, input) };

    /// <summary>Wan 2.2 TI2V-5B: diffusion model + separate VAE, one template for text and start-image clips.</summary>
    internal static readonly VideoFamilyContract Wan = new(
        FamilyId: "wan2.2-ti2v-5b",
        LoaderNode: "1", LoaderClass: "UNETLoader", LoaderInput: "unet_name",
        Prompt: One("4", "text"), Seed: One("8", "seed"),
        Width: One("6", "width"), Height: One("6", "height"), Length: One("6", "length"),
        Steps: One("8", "steps"), Fps: One("10", "fps"),
        FilenamePrefix: new VideoNodeInput("11", "filename_prefix"), OutputNode: "11",
        MinClipFrames: 5, MaxClipFrames: 49, FrameQuantum: 4, SizeMultiple: 1,
        StartImageMode: VideoStartImageMode.AttachToLatent, StartImageLatent: new VideoNodeInput("6", "start_image"),
        StartImageLoadNode: null);

    /// <summary>LTX-Video 2B: one single-file checkpoint (model, VAE) loaded by CheckpointLoaderSimple. Lengths are 8n+1 frames,
    /// sizes multiples of 32. A start image switches to the second template (LTXVImgToVideo); the prompt, seed, size and
    /// length keep the same node ids in both.</summary>
    internal static readonly VideoFamilyContract Ltx = new(
        FamilyId: "ltx-video",
        LoaderNode: "1", LoaderClass: "CheckpointLoaderSimple", LoaderInput: "ckpt_name",
        Prompt: One("4", "text"), Seed: One("8", "noise_seed"),
        Width: One("6", "width"), Height: One("6", "height"), Length: One("6", "length"),
        Steps: One("12", "steps"),
        Fps: new[] { new VideoNodeInput("10", "fps"), new VideoNodeInput("7", "frame_rate") },
        FilenamePrefix: new VideoNodeInput("11", "filename_prefix"), OutputNode: "11",
        MinClipFrames: 9, MaxClipFrames: 97, FrameQuantum: 8, SizeMultiple: 32,
        StartImageMode: VideoStartImageMode.SeparateTemplate, StartImageLatent: new VideoNodeInput("6", "image"),
        StartImageLoadNode: "14");

    internal static IReadOnlyList<VideoFamilyContract> All { get; } = new[] { Wan, Ltx };

    internal static VideoFamilyContract? ForFamilyId(string? familyId) =>
        All.FirstOrDefault(contract => string.Equals(contract.FamilyId, familyId, StringComparison.Ordinal));

    /// <summary>The contract a job runs, chosen by the family of the active video checkpoint: the registry family whose
    /// template the resolution selected. Never guesses; a template no contract covers is a typed failure.</summary>
    internal static VideoFamilyContract For(MediaCheckpointEffective effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (effective.FamilyId != null && ForFamilyId(effective.FamilyId) is { } byFamily) return byFamily;
        RegistryFamily family = MediaCompanionRegistry.Shipped.Families.FirstOrDefault(candidate =>
                candidate.IsSupported && candidate.Pipelines.Contains("video", StringComparer.Ordinal)
                && candidate.AllTemplateFiles.Contains(effective.TemplateFile, StringComparer.Ordinal))
            ?? throw new InvalidOperationException("No supported video family runs the workflow template " + effective.TemplateFile + ".");
        return ForFamilyId(family.Id)
            ?? throw new InvalidOperationException("PaRuX has no video runner contract for " + family.DisplayName + ".");
    }

    internal static RegistryFamily RegistryFamilyOf(VideoFamilyContract contract) =>
        MediaCompanionRegistry.Shipped.FindFamily(contract.FamilyId)
        ?? throw new InvalidOperationException("The registry has no family " + contract.FamilyId + ".");
}
