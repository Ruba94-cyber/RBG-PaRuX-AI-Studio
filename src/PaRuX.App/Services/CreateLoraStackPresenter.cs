using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>One installed LoRA as listed by GET /api/v1/media/loras/installed. Compatibility is compatible | incompatible | unknown.</summary>
    public sealed record InstalledLoraChoice(string Name, string FamilyName, string Compatibility, string? BaseModel = null);

    /// <summary>One row of the picker: which LoRA (null = empty slot) and how strongly.</summary>
    public sealed record LoraStackSlot(string? Name, double Strength);

    /// <summary>
    /// Create > Image "LoRA stack": up to three slots, each with a LoRA and a strength. This presenter owns the wording,
    /// the AutomationIds and the pre-checks that mirror PaRuX Core's rules (the server stays the gate: it refuses a
    /// family mismatch with its own reason). No WinUI types, so it is unit-tested.
    /// </summary>
    public static class CreateLoraStackPresenter
    {
        public const int MaxSlots = 3;
        public const double MinStrength = 0.05;
        public const double MaxStrength = 2.0;
        public const double DefaultStrength = 0.8;

        public const string Panel = "Create_LoraStack_Panel";
        public const string Status = "Create_LoraStack_Status";
        public const string AddButton = "Create_LoraStack_Add_Button";
        public const string RefreshButton = "Create_LoraStack_Refresh_Button";

        public static string SlotCombo(int index) => "Create_LoraStack_Slot" + (index + 1) + "_Combo";
        public static string SlotStrength(int index) => "Create_LoraStack_Slot" + (index + 1) + "_Strength_Slider";
        public static string SlotStrengthText(int index) => "Create_LoraStack_Slot" + (index + 1) + "_Strength_Text";
        public static string SlotRemove(int index) => "Create_LoraStack_Slot" + (index + 1) + "_Remove_Button";

        public const string NoneLabel = "(no LoRA)";

        public static string StrengthText(double strength) => "Strength " + strength.ToString("0.00", CultureInfo.InvariantCulture);

        public static string ChoiceLabel(InstalledLoraChoice choice) => choice.Compatibility switch
        {
            "compatible" => choice.Name + " · " + choice.FamilyName + " · matches the active model",
            "incompatible" => choice.Name + " · made for " + choice.FamilyName + " · will not work with the active model",
            _ => choice.Name + " · family not recorded"
        };

        public static string Intro(string checkpointFamilyName, string? checkpointFile = null) =>
            "Pick up to " + MaxSlots + " LoRAs. They are applied in order and must be made for the active model"
            + (string.IsNullOrWhiteSpace(checkpointFamilyName) || checkpointFamilyName == "unknown"
                ? (string.IsNullOrWhiteSpace(checkpointFile) ? "." : " (" + checkpointFile + ", family not recognised).")
                : " (" + checkpointFamilyName + (string.IsNullOrWhiteSpace(checkpointFile) ? "" : ": " + checkpointFile) + ").");

        /// <summary>The checkpoint file the compatibility checks used, from the installed-LoRAs response; null when absent.</summary>
        public static string? ParseCheckpointFile(System.Text.Json.JsonElement root) =>
            root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("checkpointFile", out var f)
                && f.ValueKind == System.Text.Json.JsonValueKind.String ? f.GetString() : null;

        public static string EmptyList =>
            "No LoRA is installed. Install one from COMFYUI WORKFLOWS AND LORAS below, or copy a .safetensors file into ComfyUI's models/loras folder.";

        /// <summary>The line under the slots: how many are chosen and the first reason the stack cannot be used, if any.</summary>
        public static string Summary(IReadOnlyList<LoraStackSlot> slots, IReadOnlyList<InstalledLoraChoice> installed)
        {
            int used = slots.Count(s => !string.IsNullOrWhiteSpace(s.Name));
            string? problem = Problem(slots, installed);
            if (problem != null) return problem;
            return used == 0 ? "No LoRA selected: the model runs as it is." : used + " of " + MaxSlots + " LoRA slots in use.";
        }

        /// <summary>The first local reason the stack cannot run, or null. Mirrors the server rules for a fast, specific message.</summary>
        public static string? Problem(IReadOnlyList<LoraStackSlot> slots, IReadOnlyList<InstalledLoraChoice> installed)
        {
            var chosen = slots.Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList();
            if (chosen.Count > MaxSlots) return "Use at most " + MaxSlots + " LoRAs.";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LoraStackSlot slot in chosen)
            {
                if (!seen.Add(slot.Name!)) return "\"" + slot.Name + "\" is selected twice. Use each LoRA once.";
                if (double.IsNaN(slot.Strength) || slot.Strength < MinStrength || slot.Strength > MaxStrength)
                    return "Strength for \"" + slot.Name + "\" must be between " + MinStrength.ToString("0.00", CultureInfo.InvariantCulture)
                        + " and " + MaxStrength.ToString("0.00", CultureInfo.InvariantCulture) + ".";
                InstalledLoraChoice? match = installed.FirstOrDefault(c => string.Equals(c.Name, slot.Name, StringComparison.Ordinal));
                if (match == null) return "\"" + slot.Name + "\" is no longer installed. Refresh the list.";
                if (match.Compatibility == "incompatible")
                    return "\"" + slot.Name + "\" is made for " + match.FamilyName + " and will not work with the active model. Choose a matching LoRA or remove it.";
            }
            return null;
        }

        /// <summary>The selections sent with the job: empty slots are dropped and the order of the slots is kept.</summary>
        public static List<MediaLoraSelection>? BuildSelections(IReadOnlyList<LoraStackSlot> slots)
        {
            var list = slots.Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => new MediaLoraSelection { Name = s.Name!, Strength = Math.Round(s.Strength, 2) }).ToList();
            return list.Count == 0 ? null : list;
        }

        /// <summary>Parses the installed-list response into picker choices. Tolerates a missing or odd body by returning an empty list.</summary>
        public static (string CheckpointFamilyName, List<InstalledLoraChoice> Choices) ParseInstalled(System.Text.Json.JsonElement root)
        {
            string family = root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("checkpointFamilyName", out var fn) && fn.ValueKind == System.Text.Json.JsonValueKind.String
                ? fn.GetString() ?? "unknown" : "unknown";
            var choices = new List<InstalledLoraChoice>();
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("loras", out var loras)
                && loras.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in loras.EnumerateArray())
                {
                    if (item.ValueKind != System.Text.Json.JsonValueKind.Object || !item.TryGetProperty("name", out var n) || n.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                    string Text(string key, string fallback) => item.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? fallback : fallback;
                    choices.Add(new InstalledLoraChoice(n.GetString()!, Text("familyName", "unknown"), Text("compatibility", "unknown"),
                        item.TryGetProperty("baseModel", out var bm) && bm.ValueKind == System.Text.Json.JsonValueKind.String ? bm.GetString() : null));
                }
            }
            return (family, choices);
        }

        /// <summary>One line per installed LoRA for the Model Hub media tab.</summary>
        public static string HubLine(InstalledLoraChoice choice) => ChoiceLabel(choice);

        public static string HubHeader(int count, string checkpointFamilyName) =>
            count == 0
                ? "Installed LoRAs: none."
                : "Installed LoRAs (" + count + ") · active model: " + (string.IsNullOrWhiteSpace(checkpointFamilyName) ? "unknown" : checkpointFamilyName);
    }
}
