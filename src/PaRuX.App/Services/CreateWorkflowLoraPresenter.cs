using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure presenter and AutomationId spec for the Create workspace "Import ComfyUI workflow" and "LoRA" panels.
    /// No WinUI dependencies. The view binds these strings; it never decides wording or safety itself.
    /// </summary>
    public static class CreateWorkflowLoraPresenter
    {
        public const string WorkflowImportButton = "Create_Workflow_Import_Button";
        public const string WorkflowImportFile = "Create_Workflow_Import_File";
        public const string WorkflowImportName = "Create_Workflow_Import_Name";
        public const string WorkflowImportStatus = "Create_Workflow_Import_Status";
        public const string WorkflowImportParameters = "Create_Workflow_Import_Parameters";
        public const string WorkflowImportConfirm = "Create_Workflow_Import_Confirm";
        public const string LoraSearchBox = "Create_Lora_Search";
        public const string LoraResultList = "Create_Lora_Results";
        public const string LoraNsfwBadge = "Create_Lora_NsfwBadge";
        public const string LoraTriggerWords = "Create_Lora_TriggerWords";
        public const string LoraInstalledList = "Create_Lora_Installed";
        public const string LoraInstallConfirm = "Create_Lora_InstallConfirm";

        public static string ImportStatus(bool ok, string? workflowType, int parameterCount,
            IReadOnlyList<string> errors, IReadOnlyList<string> warnings, bool verified)
        {
            if (!ok) return "Cannot import: " + string.Join(" ", errors.Take(3)) + (errors.Count > 3 ? " (+" + (errors.Count - 3) + " more)" : "");
            string text = "Ready: " + (workflowType ?? "workflow") + " workflow, " + parameterCount + " editable setting" + (parameterCount == 1 ? "" : "s") + ".";
            if (!verified) text += " Not checked against ComfyUI yet.";
            if (warnings.Count > 0) text += " " + warnings.Count + " warning" + (warnings.Count == 1 ? "" : "s") + ".";
            return text;
        }

        public static string NsfwLabel(bool nsfw) => nsfw ? "NSFW" : string.Empty;

        public static string TriggerWords(IReadOnlyList<string> words) =>
            words.Count == 0 ? "No trigger words listed" : "Trigger words: " + string.Join(", ", words.Take(8)) + (words.Count > 8 ? ", ..." : "");

        public static string Compatibility(string state) => state switch
        {
            "compatible" => "Matches the active model family",
            "incompatible" => "Made for a different model family",
            _ => "Model family unknown"
        };

        /// <summary>The line shown next to the install button; it names size and source so the confirmation is informed.</summary>
        public static string InstallPrompt(string fileName, long sizeBytes, string source, bool nsfw) =>
            "Download " + fileName + " (" + Math.Round(sizeBytes / 1048576.0, 1) + " MB) from " + source
            + (nsfw ? " [NSFW]" : "") + "? It is checked against its published hash before it is added.";
    }
}
