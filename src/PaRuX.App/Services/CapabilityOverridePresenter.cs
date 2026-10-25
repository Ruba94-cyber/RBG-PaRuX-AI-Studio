using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record CapabilityOverrideOptionPresentation(
        string Mode,
        string Label,
        string Explanation,
        bool RequiresModel,
        bool RequiresConfirmation);

    public sealed record CapabilityOverrideValidationResult(
        bool IsValid,
        string? ErrorMessage);

    public sealed record CapabilityOverridePresentation(
        string CurrentMode,
        string CurrentLabel,
        string CurrentExplanation,
        string? SelectedModel,
        bool IsModelSelectorEnabled,
        string StatusText,
        bool WarningVisible,
        string? WarningText);

    public interface ICapabilityOverrideClient
    {
        Task<IReadOnlyList<ScopedRoutingOverrideDto>> ListOverridesAsync(CancellationToken ct = default);
        Task<ScopedRoutingOverrideDto?> GetOverrideAsync(string role, string profile, CancellationToken ct = default);
        Task<ScopedRoutingOverrideDto> SetOverrideAsync(string role, string profile, SetScopedRoutingOverrideRequest request, CancellationToken ct = default);
        Task<bool> DeleteOverrideAsync(string role, string profile, CancellationToken ct = default);
    }

    public sealed class StubCapabilityOverrideClient : ICapabilityOverrideClient
    {
        private readonly Dictionary<(string Role, string Profile), ScopedRoutingOverrideDto> _overrides = new();
        private readonly object _lock = new();

        public Task<IReadOnlyList<ScopedRoutingOverrideDto>> ListOverridesAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<ScopedRoutingOverrideDto>>(_overrides.Values.ToList());
            }
        }

        public Task<ScopedRoutingOverrideDto?> GetOverrideAsync(string role, string profile, CancellationToken ct = default)
        {
            lock (_lock)
            {
                _overrides.TryGetValue((role.ToLowerInvariant(), profile.ToLowerInvariant()), out ScopedRoutingOverrideDto? dto);
                return Task.FromResult(dto);
            }
        }

        public Task<ScopedRoutingOverrideDto> SetOverrideAsync(string role, string profile, SetScopedRoutingOverrideRequest request, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var dto = new ScopedRoutingOverrideDto
                {
                    RouteRole = role.ToLowerInvariant(),
                    AutoProfile = profile.ToLowerInvariant(),
                    Mode = CapabilityOverridePresenter.NormalizeMode(request.Mode),
                    CandidateKey = request.CandidateKey,
                    Revision = request.Revision,
                    ManagedFileName = request.ManagedFileName
                };
                _overrides[(role.ToLowerInvariant(), profile.ToLowerInvariant())] = dto;
                return Task.FromResult(dto);
            }
        }

        public Task<bool> DeleteOverrideAsync(string role, string profile, CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_overrides.Remove((role.ToLowerInvariant(), profile.ToLowerInvariant())));
            }
        }
    }

    public static class CapabilityOverrideClient
    {
        private static ICapabilityOverrideClient _current = new StubCapabilityOverrideClient();

        public static ICapabilityOverrideClient Current
        {
            get => _current;
            set => _current = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    /// <summary>
    /// Pure presenter for BIC-16 Advanced capability overrides in Model Intelligence & Routing.
    /// Maps Automatic, Prefer, and Force states into plain labels, single-sentence explanations,
    /// strict model-choice validations, and Force confirmation rules.
    /// </summary>
    public static class CapabilityOverridePresenter
    {
        public const string OverrideComboBoxAutomationId = "ModelRouting_CapabilityOverride_ComboBox";
        public const string OverrideModelComboBoxAutomationId = "ModelRouting_CapabilityOverride_Model_ComboBox";
        public const string OverrideStatusTextAutomationId = "ModelRouting_CapabilityOverride_Status_Text";

        public const string OverrideComboBoxLabel = "Advanced override";
        public const string OverrideModelComboBoxLabel = "Override model";
        public const string OverrideStatusLabel = "Override status";

        public const string AutomaticLabel = "Automatic · PaRuX decides";
        public const string AutomaticExplanation = "PaRuX decides which model to use based on compatibility, speed, and intelligence.";

        public const string PreferLabel = "Prefer · compatible model with fallback";
        public const string PreferExplanation = "Use this model when it is compatible, with a visible fallback.";

        public const string ForceLabel = "Force · strict model, fail if incompatible";
        public const string ForceExplanation = "Use only this model; fail with the reason rather than using another model.";

        public const string ForceWarningText = "A request will fail rather than use another model.";

        public const string ForceConfirmationTitle = "Confirm Force Override";
        public const string ForceConfirmationPrompt = "Requests for this capability will use only this model and will fail rather than use another model if it is unavailable or incompatible. Are you sure you want to force this model?";

        public const string MissingModelForPreferMessage = "Prefer requires a model choice.";
        public const string MissingModelForForceMessage = "Force requires a model choice.";

        public const string DefaultProfile = "balanced";

        public static string NormalizeMode(string? mode)
        {
            string trimmed = (mode ?? string.Empty).Trim().ToLowerInvariant();
            return trimmed switch
            {
                ScopedRouteMode.Prefer => ScopedRouteMode.Prefer,
                ScopedRouteMode.Force => ScopedRouteMode.Force,
                _ => ScopedRouteMode.Automatic
            };
        }

        public static string GetStateLabel(string? mode) => NormalizeMode(mode) switch
        {
            ScopedRouteMode.Prefer => PreferLabel,
            ScopedRouteMode.Force => ForceLabel,
            _ => AutomaticLabel
        };

        public static string GetStateExplanation(string? mode) => NormalizeMode(mode) switch
        {
            ScopedRouteMode.Prefer => PreferExplanation,
            ScopedRouteMode.Force => ForceExplanation,
            _ => AutomaticExplanation
        };

        public static bool RequiresModelSelection(string? mode)
        {
            string normalized = NormalizeMode(mode);
            return normalized is ScopedRouteMode.Prefer or ScopedRouteMode.Force;
        }

        public static bool RequiresConfirmation(string? mode) =>
            string.Equals(NormalizeMode(mode), ScopedRouteMode.Force, StringComparison.OrdinalIgnoreCase);

        public static string? GetForceWarning(string? mode) =>
            RequiresConfirmation(mode) ? ForceWarningText : null;

        public static string GetMissingModelMessage(string? mode) =>
            NormalizeMode(mode) == ScopedRouteMode.Force
                ? MissingModelForForceMessage
                : MissingModelForPreferMessage;

        public static CapabilityOverrideValidationResult Validate(string? mode, string? selectedModel)
        {
            string normalized = NormalizeMode(mode);
            if (normalized == ScopedRouteMode.Prefer)
            {
                if (string.IsNullOrWhiteSpace(selectedModel))
                    return new CapabilityOverrideValidationResult(false, MissingModelForPreferMessage);
            }
            else if (normalized == ScopedRouteMode.Force)
            {
                if (string.IsNullOrWhiteSpace(selectedModel))
                    return new CapabilityOverrideValidationResult(false, MissingModelForForceMessage);
            }

            return new CapabilityOverrideValidationResult(true, null);
        }

        public static IReadOnlyList<CapabilityOverrideOptionPresentation> GetStateOptions() => new[]
        {
            new CapabilityOverrideOptionPresentation(
                ScopedRouteMode.Automatic,
                AutomaticLabel,
                AutomaticExplanation,
                RequiresModel: false,
                RequiresConfirmation: false),
            new CapabilityOverrideOptionPresentation(
                ScopedRouteMode.Prefer,
                PreferLabel,
                PreferExplanation,
                RequiresModel: true,
                RequiresConfirmation: false),
            new CapabilityOverrideOptionPresentation(
                ScopedRouteMode.Force,
                ForceLabel,
                ForceExplanation,
                RequiresModel: true,
                RequiresConfirmation: true)
        };

        public static CapabilityOverridePresentation Present(
            string? mode,
            string? selectedModel,
            string? customStatus = null)
        {
            string normalized = NormalizeMode(mode);
            string label = GetStateLabel(normalized);
            string explanation = GetStateExplanation(normalized);
            bool isModelEnabled = RequiresModelSelection(normalized);
            bool isForce = string.Equals(normalized, ScopedRouteMode.Force, StringComparison.OrdinalIgnoreCase);

            string status = customStatus ?? (isForce ? ForceWarningText : explanation);

            return new CapabilityOverridePresentation(
                CurrentMode: normalized,
                CurrentLabel: label,
                CurrentExplanation: explanation,
                SelectedModel: selectedModel,
                IsModelSelectorEnabled: isModelEnabled,
                StatusText: status,
                WarningVisible: isForce,
                WarningText: isForce ? ForceWarningText : null);
        }

        public static string FormatSaveSuccess(string role, string? mode, string? modelFileName)
        {
            string normalized = NormalizeMode(mode);
            return normalized switch
            {
                ScopedRouteMode.Prefer => $"Prefer {modelFileName} override saved for {role}.",
                ScopedRouteMode.Force => $"Force {modelFileName} override saved for {role}.",
                _ => $"Automatic routing override saved for {role}."
            };
        }
    }
}
