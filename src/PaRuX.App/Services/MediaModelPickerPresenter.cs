using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>One entry of the per-tab Model picker. A null FileName is "Automatic".</summary>
    public sealed record MediaModelPickerItem(string Label, string? FileName);

    public sealed record MediaModelPickerState(IReadOnlyList<MediaModelPickerItem> Items, int SelectedIndex, string Hint);

    /// <summary>What a Model picker selection must do on the server: clear the override (Automatic) or pin a file.</summary>
    public sealed record MediaModelPickerAction(bool ClearOverride, string? FileName);

    /// <summary>
    /// Pure presenter for the Create "Model" row and the content-lane control: wording, ordering and the selection rules live
    /// here, not in the view. Automatic is always first and is the default; only installed models the workflow can run are listed.
    /// </summary>
    public static class MediaModelPickerPresenter
    {
        public const string AutomaticLabel = "Automatic (recommended)";
        public static readonly IReadOnlyList<string> Lanes = new[] { "Standard", "Uncensored", "Automatic" };

        public static MediaModelPickerState Build(string? currentFile, bool isOverridden, IEnumerable<string>? compatibleFiles)
        {
            var items = new List<MediaModelPickerItem> { new(AutomaticLabel, null) };
            var files = (compatibleFiles ?? Array.Empty<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (string f in files) items.Add(new MediaModelPickerItem(f, f));

            int selected = 0;
            string hint;
            if (isOverridden && !string.IsNullOrWhiteSpace(currentFile))
            {
                int index = items.FindIndex(i => string.Equals(i.FileName, currentFile, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    // A pinned file this workflow cannot run (or that vanished): show it honestly instead of hiding the pin.
                    items.Add(new MediaModelPickerItem(currentFile + " (not compatible)", currentFile));
                    index = items.Count - 1;
                }
                selected = index;
                hint = "Pinned to " + currentFile + ". Every request uses it; choose Automatic to let PaRuX pick per prompt.";
            }
            else if (files.Count == 0)
            {
                hint = "No compatible model is installed yet. Use the setup card above or the Model Hub; until then the workflow default is used.";
            }
            else
            {
                hint = "PaRuX picks the best installed model for each prompt and tells you why after the result.";
            }
            return new MediaModelPickerState(items, selected, hint);
        }

        public static MediaModelPickerAction ActionFor(MediaModelPickerItem? selected) =>
            selected?.FileName is { Length: > 0 } file ? new MediaModelPickerAction(false, file) : new MediaModelPickerAction(true, null);

        public static string LaneHint(string? lane) => lane switch
        {
            "Uncensored" => "Only uncensored models are used. If none is installed the request stops and says which one to install; it never falls back to Standard.",
            "Automatic" => "Standard models, unless your prompt explicitly asks for adult content and an uncensored model is installed.",
            _ => "Only standard models are used. Uncensored models are never picked."
        };

        /// <summary>The lane to show for a server value; anything unrecognised reads as Standard, the safe default.</summary>
        public static string NormalizeLane(string? lane) =>
            Lanes.FirstOrDefault(l => string.Equals(l, lane?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Standard";
    }
}
