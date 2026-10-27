using System.Reflection;

namespace PaRuX.Server;

/// <summary>
/// The configuration files PaRuX's local runtimes need in <see cref="LocalAiPaths.ConfigDir"/>,
/// carried inside the Server binary and written out the first time one is missing.
///
/// They used to be hand-placed files that only ever existed on a machine somebody had set up by
/// hand, which made them invisible until they were gone. Deleting the LocalAI root's config folder
/// - a reasonable thing to do while tidying up - took every local capability with it, and a fresh
/// install had nothing to create them from at all:
///
/// - <c>llama-ui.json</c> is passed to llama-server as <c>--ui-config-file</c>, and llama.cpp exits
///   with a usage error when that path does not open. A missing file therefore did not degrade the
///   web UI, it stopped every text model from loading, CPU fallback included.
/// - the four ComfyUI graphs are the image/img2img/edit/upscale/video pipelines themselves;
///   without them <see cref="ComfyWorkflowRunner"/> can only throw "Missing workflow template".
///
/// So they ship as embedded resources instead. Writing is create-if-absent and never an overwrite:
/// a user who tuned a graph keeps their version, and PaRuX only ever fills a hole. Failure to write
/// is reported by returning null rather than by throwing, because the caller can still do something
/// sensible - drop an optional argument, or surface one honest error about one capability - and a
/// read-only or locked config directory must not take down server startup.
/// </summary>
internal static class ConfigAssets
{
    internal const string UiConfigFileName = "llama-ui.json";

    /// <summary>Every asset carried in the binary, by file name. The ComfyUI graphs are in
    /// ComfyUI's own "API format" and their node ids are the contract
    /// <see cref="ComfyWorkflowRunner"/> patches (prompt/seed/size/filename nodes), so renumbering
    /// a node here silently breaks generation - change both together.</summary>
    internal static readonly string[] FileNames =
    {
        UiConfigFileName,
        "z-image-smoke-api.json",
        "z-image-img2img-api.json",
        "z-image-inpaint-api.json",
        "flux2-klein-edit-api.json",
        "wan-video-smoke-api.json",
        "upscale-api.json"
    };

    private const string ResourcePrefix = "PaRuX.Server.Assets.config.";

    /// <summary>Writes every missing asset. Idempotent, and cheap once they exist - a handful of
    /// File.Exists calls against a directory the runtime is about to read anyway.</summary>
    internal static void EnsureAll()
    {
        foreach (string fileName in FileNames) Ensure(fileName);
    }

    /// <summary>The llama-server web UI config, or null when it is absent and could not be
    /// written. Callers drop <c>--ui-config-file</c> in that case rather than handing llama.cpp a
    /// path it will refuse to start over.</summary>
    internal static string? EnsureUiConfig() => Ensure(UiConfigFileName);

    /// <summary>The on-disk path once the asset exists, or null when it is missing and could not
    /// be created. An existing file is returned untouched, whatever its contents.</summary>
    internal static string? Ensure(string fileName) => EnsureIn(LocalAiPaths.ConfigDir, fileName);

    /// <summary>Test seam: the same rule against an explicit directory, so the behaviour can be
    /// exercised without writing into the developer's real LocalAI root.</summary>
    internal static string? EnsureIn(string configDirectory, string fileName)
    {
        string target = Path.Combine(configDirectory, fileName);
        if (File.Exists(target)) return target;

        string? content = ReadEmbedded(fileName);
        if (content == null) return null;

        try
        {
            Directory.CreateDirectory(configDirectory);
            // Written via a temp file in the same directory and moved into place, so a reader that
            // opens the path concurrently sees either nothing or the whole file, never a partial
            // JSON document that would fail to parse and look like a corrupt template.
            string temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, content);
            try
            {
                File.Move(temporary, target);
            }
            catch (IOException)
            {
                // Lost the race with another process creating the same asset - theirs is just as
                // good as ours, so keep it and drop our copy.
                try { File.Delete(temporary); } catch { }
                return File.Exists(target) ? target : null;
            }
            return target;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static string? ReadEmbedded(string fileName)
    {
        using Stream? stream = typeof(ConfigAssets).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourcePrefix + fileName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
