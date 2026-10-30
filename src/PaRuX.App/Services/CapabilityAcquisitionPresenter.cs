using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Outcome of the Core capability acquisition planner (codex3-A-06).
    /// </summary>
    public enum CapabilityAcquisitionOutcome
    {
        ProposeWithConsent,
        AlreadySatisfied,
        NoCompatibleCandidate,
        StorageBudgetExceeded,
        LicenceRequiresDecision,
        DoNotOffer
    }

    /// <summary>
    /// User action permitted on the capability acquisition card. No cloud action is offered.
    /// </summary>
    public enum CapabilityAcquisitionAction
    {
        Download,
        NotNow
    }

    /// <summary>
    /// Input proposal describing a missing capability and optional candidate model details.
    /// </summary>
    public sealed record CapabilityAcquisitionProposal
    {
        public CapabilityAcquisitionOutcome Outcome { get; init; } = CapabilityAcquisitionOutcome.DoNotOffer;
        public string Capability { get; init; } = string.Empty;
        public string? ModelName { get; init; }
        public string? RepoId { get; init; }
        public string? FileName { get; init; }
        public string? Revision { get; init; }
        public string? DownloadUrl { get; init; }
        public long? DownloadSizeBytes { get; init; }
        public long? StorageImpactBytes { get; init; }
        public bool LicenceDecisionNeeded { get; init; }
        public string? LicenceSpdx { get; init; }
        public ModelFit HardwareFit { get; init; } = ModelFit.Unknown;
        public string? CustomReason { get; init; }
    }

    /// <summary>
    /// Pure presentation model for the on-demand capability acquisition card (BIC-16, Simple mode).
    /// Maps planner outcomes into plain-words explanations, formatted plain-unit size figures,
    /// and explicit user actions. No outcome ever offers a cloud action.
    /// </summary>
    public sealed record CapabilityAcquisitionPresentation(
        CapabilityAcquisitionOutcome Outcome,
        string Title,
        string Reason,
        string SizeText,
        IReadOnlyList<CapabilityAcquisitionAction> AllowedActions,
        bool CanDownload,
        bool IsLicenceDecisionNeeded,
        string? ModelName = null,
        string? Capability = null,
        string? HardwareFitVerdict = null)
    {
        /// <summary>Guarantees that cloud actions are never offered (no silent cloud).</summary>
        public bool OffersCloudAction => false;
    }

    /// <summary>
    /// Pure presenter for BIC-16 on-demand capability acquisition in Simple mode.
    /// </summary>
    public static class CapabilityAcquisitionPresenter
    {
        public const string CardAutomationId = "ModelHub_CapabilityAcquire_Card";
        public const string DownloadButtonAutomationId = "ModelHub_CapabilityAcquire_Download_Button";
        public const string LaterButtonAutomationId = "ModelHub_CapabilityAcquire_Later_Button";

        public const string DownloadButtonLabel = "Download";
        public const string LaterButtonLabel = "Not now";

        public const int DownloadTabIndex = 0;
        public const int LaterTabIndex = 1;

        private static readonly IReadOnlyList<CapabilityAcquisitionAction> ProposeActions =
            new[] { CapabilityAcquisitionAction.Download, CapabilityAcquisitionAction.NotNow };

        private static readonly IReadOnlyList<CapabilityAcquisitionAction> DismissOnlyActions =
            new[] { CapabilityAcquisitionAction.NotNow };

        private static readonly IReadOnlyList<CapabilityAcquisitionAction> NoActions =
            Array.Empty<CapabilityAcquisitionAction>();

        public static bool OffersCloudAction(CapabilityAcquisitionOutcome outcome) => false;

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double val = bytes;
            int unitIndex = 0;
            while (val >= 1024.0 && unitIndex < units.Length - 1)
            {
                val /= 1024.0;
                unitIndex++;
            }
            return val.ToString(unitIndex == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unitIndex];
        }

        public static string FormatSizeText(long? downloadBytes, long? storageBytes)
        {
            long dl = downloadBytes.GetValueOrDefault();
            long st = storageBytes.GetValueOrDefault();

            if (dl <= 0 && st <= 0) return "0 B";
            if (dl > 0 && st > 0)
            {
                return FormatBytes(dl) + " download · " + FormatBytes(st) + " storage impact";
            }
            if (dl > 0)
            {
                return FormatBytes(dl) + " download";
            }
            return FormatBytes(st) + " storage impact";
        }

        public static string FormatTitle(CapabilityAcquisitionOutcome outcome, string? capability, string? modelName)
        {
            string cap = string.IsNullOrWhiteSpace(capability) ? "capability" : capability.Trim();
            string model = string.IsNullOrWhiteSpace(modelName) ? "model" : modelName.Trim();

            return outcome switch
            {
                CapabilityAcquisitionOutcome.ProposeWithConsent =>
                    "Acquire " + cap + " capability",
                CapabilityAcquisitionOutcome.AlreadySatisfied =>
                    char.ToUpperInvariant(cap[0]) + cap[1..] + " is already installed",
                CapabilityAcquisitionOutcome.NoCompatibleCandidate =>
                    "No compatible model for " + cap,
                CapabilityAcquisitionOutcome.StorageBudgetExceeded =>
                    "Storage budget exceeded for " + cap,
                CapabilityAcquisitionOutcome.LicenceRequiresDecision =>
                    "Licence review needed: " + model,
                CapabilityAcquisitionOutcome.DoNotOffer =>
                    "Capability acquisition unavailable",
                _ => "Acquire " + cap + " capability"
            };
        }

        public static string FormatReason(
            CapabilityAcquisitionOutcome outcome,
            string? capability,
            string? modelName,
            long? downloadBytes,
            long? storageBytes,
            bool licenceDecisionNeeded,
            ModelFit hardwareFit = ModelFit.Unknown)
        {
            string cap = string.IsNullOrWhiteSpace(capability) ? "this task" : capability.Trim();
            string model = string.IsNullOrWhiteSpace(modelName) ? "a proposed model" : modelName.Trim();
            string size = FormatSizeText(downloadBytes, storageBytes);
            string fitWord = hardwareFit switch
            {
                ModelFit.ExcellentFit => " (fits in GPU)",
                ModelFit.GoodFit => " (tight GPU fit)",
                ModelFit.WorksWithOffload => " (uses CPU offload)",
                ModelFit.CpuRamOnly => " (runs on CPU/RAM; performance may be slow)",
                _ => string.Empty
            };

            return outcome switch
            {
                CapabilityAcquisitionOutcome.ProposeWithConsent =>
                    licenceDecisionNeeded
                        ? "Your library needs a model for " + cap + "; downloading " + model + fitWord + " (" + size + ") adds this capability locally and requires a licence decision."
                        : "Your library needs a model for " + cap + "; downloading " + model + fitWord + " (" + size + ") adds this capability locally.",
                CapabilityAcquisitionOutcome.LicenceRequiresDecision =>
                    "Your library needs a model for " + cap + "; " + model + fitWord + " (" + size + ") provides it, but requires reviewing and accepting its licence terms before downloading.",
                CapabilityAcquisitionOutcome.AlreadySatisfied =>
                    "Your local model library already includes an installed model that satisfies the " + cap + " capability.",
                CapabilityAcquisitionOutcome.NoCompatibleCandidate =>
                    "No compatible local model candidate was found that can provide the " + cap + " capability on this machine's hardware.",
                CapabilityAcquisitionOutcome.StorageBudgetExceeded =>
                    "Downloading " + model + " for " + cap + " requires " + size + ", which exceeds the available AI storage budget.",
                CapabilityAcquisitionOutcome.DoNotOffer =>
                    "On-demand capability acquisition is not offered for " + cap + ".",
                _ => "On-demand capability acquisition is not offered for " + cap + "."
            };
        }

        public static IReadOnlyList<CapabilityAcquisitionAction> GetAllowedActions(CapabilityAcquisitionOutcome outcome) =>
            outcome switch
            {
                CapabilityAcquisitionOutcome.ProposeWithConsent => ProposeActions,
                CapabilityAcquisitionOutcome.LicenceRequiresDecision => ProposeActions,
                CapabilityAcquisitionOutcome.AlreadySatisfied => DismissOnlyActions,
                CapabilityAcquisitionOutcome.NoCompatibleCandidate => DismissOnlyActions,
                CapabilityAcquisitionOutcome.StorageBudgetExceeded => DismissOnlyActions,
                CapabilityAcquisitionOutcome.DoNotOffer => NoActions,
                _ => NoActions
            };

        public static CapabilityAcquisitionPresentation Present(CapabilityAcquisitionProposal proposal)
        {
            ArgumentNullException.ThrowIfNull(proposal);

            CapabilityAcquisitionOutcome outcome = proposal.Outcome;
            string title = FormatTitle(outcome, proposal.Capability, proposal.ModelName);
            string sizeText = FormatSizeText(proposal.DownloadSizeBytes, proposal.StorageImpactBytes);
            bool licenceNeeded = proposal.LicenceDecisionNeeded || outcome == CapabilityAcquisitionOutcome.LicenceRequiresDecision;

            string reason = !string.IsNullOrWhiteSpace(proposal.CustomReason)
                ? proposal.CustomReason.Trim()
                : FormatReason(
                    outcome,
                    proposal.Capability,
                    proposal.ModelName,
                    proposal.DownloadSizeBytes,
                    proposal.StorageImpactBytes,
                    licenceNeeded,
                    proposal.HardwareFit);

            IReadOnlyList<CapabilityAcquisitionAction> allowedActions = GetAllowedActions(outcome);
            bool canDownload = allowedActions.Contains(CapabilityAcquisitionAction.Download);
            string? fitVerdict = proposal.HardwareFit != ModelFit.Unknown ? ModelFitClassifier.Describe(proposal.HardwareFit) : null;

            return new CapabilityAcquisitionPresentation(
                Outcome: outcome,
                Title: title,
                Reason: reason,
                SizeText: sizeText,
                AllowedActions: allowedActions,
                CanDownload: canDownload,
                IsLicenceDecisionNeeded: licenceNeeded,
                ModelName: proposal.ModelName,
                Capability: proposal.Capability,
                HardwareFitVerdict: fitVerdict);
        }

        public static CapabilityAcquisitionPresentation Present(
            CapabilityAcquisitionOutcome outcome,
            string? capability = null,
            string? modelName = null,
            long? downloadBytes = null,
            long? storageBytes = null,
            bool licenceDecisionNeeded = false,
            ModelFit hardwareFit = ModelFit.Unknown)
        {
            var proposal = new CapabilityAcquisitionProposal
            {
                Outcome = outcome,
                Capability = capability ?? string.Empty,
                ModelName = modelName,
                DownloadSizeBytes = downloadBytes,
                StorageImpactBytes = storageBytes,
                LicenceDecisionNeeded = licenceDecisionNeeded,
                HardwareFit = hardwareFit
            };
            return Present(proposal);
        }
    }
}
