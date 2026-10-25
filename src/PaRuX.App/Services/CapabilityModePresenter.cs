using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record CapabilityModeRowPresentation(
        string Capability, string Label, string Mode, string? ModelId,
        bool ModelEnabled, bool CanSave, string TraceText, string State);

    /// <summary>
    /// Pure presentation for the per-capability Automatic / Prefer / Force section of the Model Routing panel.
    /// Prefer and Force need a model; the trace text explains what the router will do (or did) and why, honestly.
    /// </summary>
    public static class CapabilityModePresenter
    {
        public static readonly IReadOnlyList<(string Capability, string Label)> Capabilities = new[]
        {
            (ModelCapabilityIds.GeneralChat, "Chat"),
            (ModelCapabilityIds.Reasoning, "Reasoning"),
            (ModelCapabilityIds.Coding, "Coding"),
            (ModelCapabilityIds.Vision, "Vision"),
            (ModelCapabilityIds.ImageGenerate, "Image generation"),
            (ModelCapabilityIds.ImageEdit, "Image editing"),
            (ModelCapabilityIds.VideoGenerate, "Video generation"),
        };

        public static readonly IReadOnlyList<(string Mode, string Label)> ModeOptions = new[]
        {
            (ScopedRouteMode.Automatic, "Automatic"),
            (ScopedRouteMode.Prefer, "Prefer"),
            (ScopedRouteMode.Force, "Force"),
        };

        public static string ModeAutomationId(string capability) => "routing.cap." + capability + ".mode";
        public static string ModelAutomationId(string capability) => "routing.cap." + capability + ".model";
        public static string TraceAutomationId(string capability) => "routing.cap." + capability + ".trace";

        public static string NormalizeMode(string? mode)
        {
            string m = (mode ?? string.Empty).Trim().ToLowerInvariant();
            return ScopedRouteMode.All.Contains(m) ? m : ScopedRouteMode.Automatic;
        }

        public static bool RequiresModel(string? mode) => NormalizeMode(mode) != ScopedRouteMode.Automatic;

        /// <summary>Presentation for the current selection, optionally enriched by the server's dry-run status.</summary>
        public static CapabilityModeRowPresentation Present(
            string capability, string? mode, string? selectedModel, CapabilityRoutingStatusDto? status)
        {
            string m = NormalizeMode(mode);
            string label = Capabilities.FirstOrDefault(c => c.Capability == capability).Label ?? capability;
            bool requires = RequiresModel(m);
            string? model = requires ? (string.IsNullOrWhiteSpace(selectedModel) ? null : selectedModel) : null;
            bool canSave = !requires || model is not null;

            string trace;
            string state = "ok";
            if (requires && model is null)
            {
                trace = m == ScopedRouteMode.Force
                    ? "Choose a model to force. Nothing else will be substituted."
                    : "Choose a model to prefer. PaRuX falls back to automatic routing if it is unavailable.";
            }
            else if (status is not null && status.Mode == m && string.Equals(status.ModelId ?? null, model, StringComparison.OrdinalIgnoreCase))
            {
                state = status.State;
                trace = FormatTrace(status);
            }
            else
            {
                trace = m switch
                {
                    ScopedRouteMode.Force => $"Pending save: \"{model}\" will be forced; if it cannot run, the request is refused.",
                    ScopedRouteMode.Prefer => $"Pending save: \"{model}\" will be preferred, with a visible fallback.",
                    _ => "Automatic: PaRuX picks the best installed model and explains why."
                };
            }
            return new CapabilityModeRowPresentation(capability, label, m, model, requires, canSave, trace, state);
        }

        public static string FormatTrace(CapabilityRoutingStatusDto status)
        {
            var lines = new List<string> { status.Explanation };
            if (status.Trace.Count > 0) lines.Add("Trace: " + string.Join(" > ", status.Trace));
            if (!string.IsNullOrWhiteSpace(status.LastJobExplanation))
            {
                lines.Add("Last job: " + status.LastJobExplanation);
                if (status.LastJobTrace.Count > 0) lines.Add("Last job trace: " + string.Join(" > ", status.LastJobTrace));
            }
            return string.Join(Environment.NewLine, lines.Where(l => !string.IsNullOrWhiteSpace(l)));
        }

        public static SetCapabilityModeRequest BuildRequest(string? mode, string? selectedModel)
        {
            string m = NormalizeMode(mode);
            if (m == ScopedRouteMode.Automatic) return new SetCapabilityModeRequest { Mode = m, ModelId = null };
            if (string.IsNullOrWhiteSpace(selectedModel))
                throw new InvalidOperationException("Prefer and Force need a model.");
            return new SetCapabilityModeRequest { Mode = m, ModelId = selectedModel.Trim() };
        }
    }
}
