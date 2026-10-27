using System.Runtime.InteropServices;
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
    string? StartImageLoadNode,
    bool StagesCheckpointForComfy)
{
    /// <summary>Every family keeps its checkpoint in ComfyUI's diffusion_models folder (PaRuX's checkpoint store). A family
    /// whose loader is CheckpointLoaderSimple reads ComfyUI's checkpoints folder instead, so the file is exposed there for the job.</summary>
    internal const string StoreFolder = "diffusion_models";

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
        if (effective.IsOverridden) Set(request, new[] { new VideoNodeInput(LoaderNode, LoaderInput) }, effective.File);
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
        StartImageLoadNode: null, StagesCheckpointForComfy: false);

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
        StartImageLoadNode: "14", StagesCheckpointForComfy: true);

    internal static IReadOnlyList<VideoFamilyContract> All { get; } = new[] { Wan, Ltx };

    internal static VideoFamilyContract? ForFamilyId(string? familyId) =>
        All.FirstOrDefault(contract => string.Equals(contract.FamilyId, familyId, StringComparison.Ordinal));

    /// <summary>The contract a job runs, chosen by the family of the active video checkpoint: the registry family whose
    /// template the resolution selected. Never guesses; a template no contract covers is a typed failure.</summary>
    internal static VideoFamilyContract For(MediaCheckpointEffective effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
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

/// <summary>
/// Exposes a checkpoint that PaRuX keeps in ComfyUI's diffusion_models folder to ComfyUI's checkpoints folder for the length
/// of one job, as an NTFS hard link: the same bytes, no copy. Only a family whose graph loads a single-file checkpoint
/// (CheckpointLoaderSimple) needs it. A link this job created is removed again when the job ends; a file that was already
/// there is never touched, and a same-named file of another size is refused rather than overwritten.
/// </summary>
internal sealed class VideoCheckpointStaging : IDisposable
{
    private readonly string? _createdLink;

    private VideoCheckpointStaging(string? createdLink) { _createdLink = createdLink; }

    internal static VideoCheckpointStaging None { get; } = new(null);

    internal static VideoCheckpointStaging Ensure(string modelsRoot, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new InvalidDataException("The checkpoint file name is not a plain file name.");
        string source = Path.Combine(modelsRoot, VideoFamilyContract.StoreFolder, fileName);
        string destinationDirectory = Path.Combine(modelsRoot, "checkpoints");
        string destination = Path.Combine(destinationDirectory, fileName);
        var sourceInfo = new FileInfo(source);
        if (!sourceInfo.Exists)
            throw new MediaDependencyException(ComfyLivePreflight.MissingFile(VideoFamilyContract.StoreFolder, fileName));

        if (File.Exists(destination))
        {
            if (new FileInfo(destination).Length == sourceInfo.Length) return None;
            throw new InvalidOperationException("ComfyUI's checkpoints folder already holds a different file named " + fileName
                + ". Move it aside so PaRuX can expose the video checkpoint there.");
        }

        Directory.CreateDirectory(destinationDirectory);
        if (!TryCreateHardLink(destination, source, out string failure))
            throw new InvalidOperationException("PaRuX could not expose " + fileName + " to ComfyUI's checkpoints folder (" + failure
                + "). Copy the file into " + destinationDirectory + " yourself, or keep both folders on one NTFS volume.");
        return new VideoCheckpointStaging(destination);
    }

    public void Dispose()
    {
        if (_createdLink == null) return;
        // ComfyUI may still hold the file open for a moment; a link that cannot be removed now is harmless (same bytes, no extra
        // space) and is recognised as the same file by the next job.
        try { File.Delete(_createdLink); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr reservedSecurityAttributes);

    private static bool TryCreateHardLink(string link, string existing, out string failure)
    {
        failure = string.Empty;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (CreateHardLink(link, existing, IntPtr.Zero)) return true;
                failure = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }
            File.CreateSymbolicLink(link, existing);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            failure = ex.Message;
            return false;
        }
    }
}
