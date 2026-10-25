using System.Globalization;
using PaRuX.Contracts;
using PaRuX.Intelligence;

namespace PaRuX.Server;

/// <summary>Pure text for the "Find models from other apps" list. Every model is owned by the other app.</summary>
public static class ExternalModelsPresenter
{
    public sealed record Line(string Source, string Title, string Detail);

    public static string SourceLabel(string source) => source switch
    {
        ExternalModelStoreImporter.OllamaSource => "Ollama",
        ExternalModelStoreImporter.LmStudioSource => "LM Studio",
        ExternalModelStoreImporter.JanSource => "Jan",
        ExternalModelStoreImporter.KoboldCppSource => "KoboldCpp",
        _ => source
    };

    public static IReadOnlyList<Line> Lines(IEnumerable<ExternalModelCandidate> candidates) =>
        candidates.OrderBy(c => SourceLabel(c.Source), StringComparer.Ordinal).ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(c => new Line(SourceLabel(c.Source), c.DisplayName,
                FormatSize(c.SizeBytes) + " - " + GgufFileNaming.QuantizationOrUnknown(c.SuggestedFileName)
                + " - owned by " + SourceLabel(c.Source) + " (read-only)"
                + (c.AlreadyImported ? " - already in your library"
                    : c.SameVolumeAsLibrary ? " - linked, no extra disk space" : " - needs a copy (asks first)")))
            .ToList();

    /// <summary>Honest empty state: names every folder that was checked and why it gave nothing.</summary>
    public static string EmptyState(IEnumerable<ExternalModelStoreImporter.StoreReport> reports)
    {
        var notes = reports.Where(r => r.ModelCount == 0 && r.Note != null)
            .Select(r => SourceLabel(r.Source) + " (" + r.Root + "): " + r.Note).ToList();
        return notes.Count == 0 ? "No models from other apps were found."
            : "No models from other apps were found. " + string.Join(" ", notes);
    }

    private static string FormatSize(long bytes) =>
        bytes < 0 ? "unknown size" : (bytes / (1024d * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
}
