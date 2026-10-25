using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    public sealed record PolicyControlPresentation(
        string Label,
        string Explanation,
        bool IsEnabled,
        string Value,
        string? UnavailableReason = null);

    public sealed record PromotionOptionPresentation(
        string Value,
        string Label,
        string Explanation);

    public sealed record DiscoveryPolicyPresentation(
        PolicyControlPresentation AutoEvaluate,
        PolicyControlPresentation Promotion,
        IReadOnlyList<PromotionOptionPresentation> PromotionOptions,
        string StatusText);

    public sealed record DiscoveryPolicyState(
        string AutoEvaluate = DiscoveryPolicyPresenter.DefaultAutoEvaluate,
        string PromotionPolicy = DiscoveryPolicyPresenter.DefaultPromotion,
        bool GamingActive = false);

    public interface IDiscoveryPolicyClient
    {
        Task<DiscoveryPolicyState> GetPolicyAsync(CancellationToken ct = default);
        Task<DiscoveryPolicyState> SetAutoEvaluateAsync(string autoEvaluate, CancellationToken ct = default);
        Task<DiscoveryPolicyState> SetPromotionPolicyAsync(string promotionPolicy, CancellationToken ct = default);
    }

    public sealed class StubDiscoveryPolicyClient : IDiscoveryPolicyClient
    {
        private string _autoEvaluate = DiscoveryPolicyPresenter.DefaultAutoEvaluate;
        private string _promotionPolicy = DiscoveryPolicyPresenter.DefaultPromotion;
        private bool _gamingActive;
        private readonly object _lock = new();

        public StubDiscoveryPolicyClient(bool initialGamingActive = false)
        {
            _gamingActive = initialGamingActive;
        }

        public void SetGamingActive(bool active)
        {
            lock (_lock) _gamingActive = active;
        }

        public Task<DiscoveryPolicyState> GetPolicyAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(new DiscoveryPolicyState(_autoEvaluate, _promotionPolicy, _gamingActive));
            }
        }

        public Task<DiscoveryPolicyState> SetAutoEvaluateAsync(string autoEvaluate, CancellationToken ct = default)
        {
            lock (_lock)
            {
                _autoEvaluate = DiscoveryPolicyPresenter.NormalizeAutoEvaluate(autoEvaluate);
                return Task.FromResult(new DiscoveryPolicyState(_autoEvaluate, _promotionPolicy, _gamingActive));
            }
        }

        public Task<DiscoveryPolicyState> SetPromotionPolicyAsync(string promotionPolicy, CancellationToken ct = default)
        {
            lock (_lock)
            {
                _promotionPolicy = DiscoveryPolicyPresenter.NormalizePromotion(promotionPolicy);
                return Task.FromResult(new DiscoveryPolicyState(_autoEvaluate, _promotionPolicy, _gamingActive));
            }
        }
    }

    public static class DiscoveryPolicyClient
    {
        private static IDiscoveryPolicyClient _current = new StubDiscoveryPolicyClient();

        public static IDiscoveryPolicyClient Current
        {
            get => _current;
            set => _current = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    public static class DiscoveryPolicyPresenter
    {
        public const string AutoEvaluateAsk = "ask";
        public const string AutoEvaluateAuto = "auto";

        public const string PromotionAsk = "ask";
        public const string PromotionClearlyBetter = "clearly_better";
        public const string PromotionNever = "never";

        public const string DefaultAutoEvaluate = AutoEvaluateAsk;
        public const string DefaultPromotion = PromotionAsk;

        public const string GamingPausedReason = "Paused while Gaming is active";

        public const string AutoEvaluateLabel = "Evaluate suitable models automatically";
        public const string PromotionPolicyLabel = "Promotion policy";

        public const string SavedStatusText = "Applied for this session only. Saving to PaRuX Core is not available yet.";

        public static readonly IReadOnlyList<PromotionOptionPresentation> PromotionOptions = new PromotionOptionPresentation[]
        {
            new(PromotionAsk, "Ask before replacing", "Ask before switching your active model to a candidate."),
            new(PromotionClearlyBetter, "Auto-promote only when clearly better", "Automatically switch only when a candidate demonstrates strictly better quality and speed."),
            new(PromotionNever, "Never replace pinned or manual choices", "Never replace pinned or manual choices automatically.")
        };

        public static string NormalizeAutoEvaluate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DefaultAutoEvaluate;
            string trimmed = value.Trim();
            if (string.Equals(trimmed, AutoEvaluateAuto, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "automatic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "on", StringComparison.OrdinalIgnoreCase))
            {
                return AutoEvaluateAuto;
            }
            return AutoEvaluateAsk;
        }

        public static string NormalizePromotion(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DefaultPromotion;
            string trimmed = value.Trim();
            if (string.Equals(trimmed, PromotionClearlyBetter, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "when_clearly_better", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "better", StringComparison.OrdinalIgnoreCase))
            {
                return PromotionClearlyBetter;
            }
            if (string.Equals(trimmed, PromotionNever, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "never_replace", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "manual", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "pinned_only", StringComparison.OrdinalIgnoreCase))
            {
                return PromotionNever;
            }
            return PromotionAsk;
        }

        public static bool IsAutoEvaluateOn(string? value) =>
            string.Equals(NormalizeAutoEvaluate(value), AutoEvaluateAuto, StringComparison.Ordinal);

        public static string ToAutoEvaluateValue(bool isOn) =>
            isOn ? AutoEvaluateAuto : AutoEvaluateAsk;

        public static string GetAutoEvaluateLabel() => AutoEvaluateLabel;

        public static string GetAutoEvaluateExplanation(string? value, bool gamingActive = false)
        {
            if (gamingActive) return GamingPausedReason;
            return IsAutoEvaluateOn(value)
                ? "Automatically benchmark newly discovered models that match this machine's hardware."
                : "Ask before running local benchmarks on newly discovered model candidates.";
        }

        public static bool IsAutoEvaluateEnabled(bool gamingActive) => !gamingActive;

        public static string GetPromotionLabel() => PromotionPolicyLabel;

        public static string GetPromotionExplanation(string? value)
        {
            string normalized = NormalizePromotion(value);
            return normalized switch
            {
                PromotionClearlyBetter => "Automatically switch only when a candidate demonstrates strictly better quality and speed.",
                PromotionNever => "Never replace pinned or manual choices automatically.",
                _ => "Ask before switching your active model to a candidate."
            };
        }

        public static bool IsPromotionEnabled(bool gamingActive = false) => true;

        public static IReadOnlyList<PromotionOptionPresentation> GetPromotionOptions() => PromotionOptions;

        public static PolicyControlPresentation PresentAutoEvaluate(string? value, bool gamingActive = false)
        {
            string normalized = NormalizeAutoEvaluate(value);
            bool enabled = IsAutoEvaluateEnabled(gamingActive);
            string explanation = GetAutoEvaluateExplanation(normalized, gamingActive);
            string? reason = gamingActive ? GamingPausedReason : null;
            return new PolicyControlPresentation(AutoEvaluateLabel, explanation, enabled, normalized, reason);
        }

        public static PolicyControlPresentation PresentAutoEvaluate(bool isOn, bool gamingActive = false) =>
            PresentAutoEvaluate(ToAutoEvaluateValue(isOn), gamingActive);

        public static PolicyControlPresentation PresentPromotion(string? value, bool gamingActive = false)
        {
            string normalized = NormalizePromotion(value);
            string explanation = GetPromotionExplanation(normalized);
            bool enabled = IsPromotionEnabled(gamingActive);
            return new PolicyControlPresentation(PromotionPolicyLabel, explanation, enabled, normalized, null);
        }

        public static DiscoveryPolicyPresentation Present(
            string? autoEvaluate, string? promotionPolicy, bool gamingActive, string? status = null)
        {
            PolicyControlPresentation autoPresentation = PresentAutoEvaluate(autoEvaluate, gamingActive);
            PolicyControlPresentation promotionPresentation = PresentPromotion(promotionPolicy, gamingActive);
            string statusText = status ?? string.Empty;
            return new DiscoveryPolicyPresentation(autoPresentation, promotionPresentation, PromotionOptions, statusText);
        }

        public static DiscoveryPolicyPresentation Present(
            DiscoveryPolicyState state, bool gamingActive, string? status = null)
        {
            ArgumentNullException.ThrowIfNull(state);
            return Present(state.AutoEvaluate, state.PromotionPolicy, gamingActive || state.GamingActive, status);
        }

        public static string FormatSavedStatus() => SavedStatusText;

        public static string FormatErrorStatus(string error)
        {
            string message = string.IsNullOrWhiteSpace(error) ? "An unexpected error occurred." : error.Trim();
            return "Couldn't save candidate discovery policy: " + message;
        }

        public static string FormatErrorStatus(string action, string error)
        {
            string message = string.IsNullOrWhiteSpace(error) ? "An unexpected error occurred." : error.Trim();
            return $"Couldn't save {action}: {message}";
        }

        public static bool ResolveRevertAutoEvaluate(bool requested, bool lastConfirmedState) => lastConfirmedState;

        public static string ResolveRevertPromotion(string requested, string lastConfirmedPolicy) =>
            NormalizePromotion(lastConfirmedPolicy);
    }
}
