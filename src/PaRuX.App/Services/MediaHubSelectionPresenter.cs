using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services;

public enum ModelHubTab { Text, Media, Downloads }

/// <summary>Tab ids, labels and automation ids of the Model Hub selector bar. Pure.</summary>
public static class ModelHubTabs
{
    public static IReadOnlyList<ModelHubTab> All { get; } = new[] { ModelHubTab.Text, ModelHubTab.Media, ModelHubTab.Downloads };

    public static string Label(ModelHubTab tab) => tab switch
    {
        ModelHubTab.Media => "Media",
        ModelHubTab.Downloads => "Downloads",
        _ => "Text models"
    };

    public static string AutomationId(ModelHubTab tab) => tab switch
    {
        ModelHubTab.Media => "ModelHub_Tab_Media",
        ModelHubTab.Downloads => "ModelHub_Tab_Downloads",
        _ => "ModelHub_Tab_Text"
    };

    public static ModelHubTab Parse(string? tag) => tag switch
    {
        "Media" => ModelHubTab.Media,
        "Downloads" => ModelHubTab.Downloads,
        _ => ModelHubTab.Text
    };
}

/// <summary>One engine card of the Media tab (ComfyUI image/video/edit, speech, image-to-3D, music).</summary>
public sealed record MediaEngineHubCard(
    string Type, string Title, string AutomationId, bool Installed, string StatusLine, string SizeLine, string LicenseLine,
    string Detail, string ButtonLabel, bool ButtonEnabled, bool IsCancel, bool ShowProgress, double Progress, bool Indeterminate);

public sealed record MediaEngineDefinition(string Type, string Title);

/// <summary>
/// Engine cards for the Model Hub. It adds the installed / size / licence facts the Create setup card hides once ready, and
/// reuses <see cref="MediaSetupPresenter"/> for every in-flight, failed, cancelled and blocked state so the wording stays one.
/// </summary>
public static class MediaEngineHubPresenter
{
    public static IReadOnlyList<MediaEngineDefinition> Engines { get; } = new[]
    {
        new MediaEngineDefinition("image", "Image engine (ComfyUI)"),
        new MediaEngineDefinition("video", "Video engine (ComfyUI)"),
        new MediaEngineDefinition("edit", "Image-edit engine (ComfyUI)"),
        new MediaEngineDefinition("speech", "Speech engine"),
        new MediaEngineDefinition("3d", "Image-to-3D engine"),
        new MediaEngineDefinition("music", "Music engine")
    };

    public static string AutomationIdFor(string type) => "ModelHub_Engine_" + type;

    public static MediaEngineHubCard Unavailable(MediaEngineDefinition engine, string reason) => new(
        engine.Type, engine.Title, AutomationIdFor(engine.Type), false, "Status unavailable", string.Empty, string.Empty,
        reason, "Retry", true, false, false, 0, false);

    public static MediaEngineHubCard Present(MediaEngineDefinition engine, MediaSetupStatus status)
    {
        if (status == null) throw new ArgumentNullException(nameof(status));
        string size = SizeLine(status);
        string license = LicenseLine(status);
        if (status.Ready)
        {
            return new MediaEngineHubCard(engine.Type, engine.Title, AutomationIdFor(engine.Type), true, "Installed",
                size, license, string.IsNullOrWhiteSpace(status.Message) ? "Installed and verified." : status.Message,
                "Installed", false, false, false, 0, false);
        }

        MediaSetupCardModel card = MediaSetupPresenter.Present(status, engine.Title);
        bool running = status.State == "running";
        string statusLine = status.State switch
        {
            "running" => "Installing",
            "failed" => "Setup stopped",
            "cancelled" => "Setup cancelled",
            "blocked" => "Not available on this PC",
            "waiting" => "Waiting",
            _ => "Not installed"
        };
        // The Create card words every button "Set up <label>"; in the Hub the engine's own title is already the card heading.
        string button = running ? "Cancel" : status.State switch
        {
            "failed" => "Try again",
            "cancelled" => "Resume",
            "blocked" or "waiting" => "Install",
            _ => "Install"
        };
        bool enabled = card.ButtonEnabled && status.State is not ("blocked" or "waiting");
        return new MediaEngineHubCard(engine.Type, engine.Title, AutomationIdFor(engine.Type), false, statusLine,
            size, license, card.Detail, button, enabled, running, card.ShowProgress, card.Progress, card.Indeterminate);
    }

    internal static string SizeLine(MediaSetupStatus status)
    {
        if (status.Steps.Count == 0) return string.Empty;
        long total = status.Steps.Sum(s => s.SizeBytes);
        if (total <= 0) return string.Empty;
        long pending = status.Steps.Where(s => s.State != "done").Sum(s => s.SizeBytes);
        return status.Ready || pending == 0
            ? "Size on disk: " + FormatBytes(total)
            : "Download: " + FormatBytes(pending) + " of " + FormatBytes(total);
    }

    internal static string LicenseLine(MediaSetupStatus status)
    {
        List<string> licenses = status.Steps.Select(s => s.License).Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return licenses.Count == 0 ? string.Empty : "Licence: " + string.Join(", ", licenses);
    }

    internal static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
            : (bytes / (1024.0 * 1024)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}

/// <summary>What the "Use this model" / "Automatic" controls of one catalogue card show.</summary>
public sealed record MediaSelectionModel(bool ShowUse, bool UseEnabled, string UseLabel, bool IsActive, string ActiveLine);

/// <summary>
/// Model selection per capability. "Use this model" pins the file as the checkpoint override for the media type (POST
/// /media/checkpoints/{type}); "Automatic" clears the override (POST .../reset) so the catalogue's best fit is used again.
/// </summary>
public static class MediaSelectionPresenter
{
    /// <summary>The checkpoint type ("image", "video", "edit") a catalogue capability is assigned through, or null for none.</summary>
    public static string? TypeFor(string capabilityId) => capabilityId switch
    {
        ModelCapabilityIds.ImageEdit => "edit",
        ModelCapabilityIds.VideoGenerate => "video",
        ModelCapabilityIds.ImageGenerate => "image",
        _ => null
    };

    public static MediaSelectionModel ForCard(MediaCatalogueEntryDto entry, MediaCheckpointStatus? current)
    {
        string? type = TypeFor(entry.CapabilityId);
        // Only a model PaRuX has a validated workflow for, that is on disk in ComfyUI's folder, can be pinned.
        if (type == null || entry.Support != "supported" || !entry.Installed || string.IsNullOrEmpty(entry.FileName))
            return new MediaSelectionModel(false, false, string.Empty, false, string.Empty);

        bool pinned = current != null && current.IsOverridden
            && string.Equals(current.CurrentFile, entry.FileName, StringComparison.OrdinalIgnoreCase);
        if (pinned)
            return new MediaSelectionModel(true, false, "In use", true, "Active: you chose this model.");
        string line = entry.UsedAutomatically && (current == null || !current.IsOverridden)
            ? "Active: picked by Automatic."
            : string.Empty;
        return new MediaSelectionModel(true, true, "Use this model", entry.UsedAutomatically && line.Length > 0, line);
    }

    /// <summary>The per-capability header: which choice is active and whether the Automatic button does anything.</summary>
    public static (string ActiveLine, bool AutomaticEnabled, string AutomaticLabel) ForCapability(MediaCheckpointStatus? current)
    {
        if (current == null) return ("Active model unknown.", false, "Automatic");
        if (current.IsOverridden)
            return ("Active: " + current.CurrentFile + " (your choice).", true, "Automatic");
        return (string.IsNullOrWhiteSpace(current.CurrentFile)
            ? "Automatic: no model installed yet."
            : "Automatic: " + current.CurrentFile + ".", false, "Automatic (active)");
    }
}

/// <summary>Client-side shape check of the "Add a checkpoint from Hugging Face" form. The server repeats and enforces it.</summary>
public static class HuggingFaceCheckpointForm
{
    public static readonly string[] Types = { "image", "video", "edit" };

    /// <summary>Null when the three fields are plausible; otherwise the reason to show.</summary>
    public static string? Validate(string? repoId, string? filePath, string? type)
    {
        if (type == null || !Types.Contains(type, StringComparer.Ordinal)) return "Choose image, video or edit.";
        string[] parts = (repoId ?? string.Empty).Trim().Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0))
            return "Repository must look like owner/name.";
        string path = (filePath ?? string.Empty).Trim();
        if (path.Length == 0) return "Enter the file path inside the repository.";
        if (!path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)) return "Only .safetensors checkpoints can be added.";
        if (path.Contains("://", StringComparison.Ordinal)) return "Enter the path inside the repository, not a URL.";
        return null;
    }

    public static string TypeLabel(string type) => type switch { "video" => "Video", "edit" => "Image edit", _ => "Image" };
}

/// <summary>One row of "checkpoints you added from Hugging Face": the download state and what can be done next.</summary>
public sealed record CustomCheckpointRow(string FileName, string Type, string StatusText, bool CanInstall, bool Failed);

public static class CustomCheckpointPresenter
{
    public static CustomCheckpointRow Present(string fileName, string type, ModelDownloadStatus? job)
    {
        if (job == null)
            return new CustomCheckpointRow(fileName, type, "Waiting for the download queue to report it.", false, false);
        switch (job.Status)
        {
            case "completed":
                return new CustomCheckpointRow(fileName, type, "Downloaded and checked: this file belongs to a family the " + type + " workflow can run. Install it to use it.", true, false);
            case "failed":
            {
                string reason = job.ErrorDetail != null
                    ? job.ErrorDetail.Message + (string.IsNullOrWhiteSpace(job.ErrorDetail.NextAction) ? string.Empty : " " + job.ErrorDetail.NextAction)
                    : (string.IsNullOrWhiteSpace(job.Error) ? "The download failed." : job.Error);
                return new CustomCheckpointRow(fileName, type, "Refused or failed: " + reason, false, true);
            }
            case "cancelled":
                return new CustomCheckpointRow(fileName, type, "Download cancelled.", false, true);
            default:
                string progress = job.TotalBytes > 0
                    ? " " + (int)Math.Min(100, job.BytesDownloaded * 100 / job.TotalBytes) + "%"
                    : string.Empty;
                return new CustomCheckpointRow(fileName, type, "Downloading" + progress + ". The model family is checked when it finishes.", false, false);
        }
    }

    /// <summary>The server answers {"error":"..."}; show the sentence, not the JSON.</summary>
    public static string ExtractServerError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Unknown error.";
        int start = message.IndexOf('{');
        if (start >= 0)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(message[start..]);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.String)
                    return error.GetString() ?? message;
            }
            catch (System.Text.Json.JsonException) { }
        }
        return message;
    }
}
