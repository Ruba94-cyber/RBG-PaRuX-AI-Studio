using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class ComponentDisplayItem
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public ComponentOwnership Ownership { get; set; }
        public string OwnershipLabel { get; set; } = string.Empty;
        public bool IsDetected { get; set; }
        public bool IsSkipped { get; set; }
        public bool IsInstalled => IsDetected && !string.Equals(Health, "missing", StringComparison.OrdinalIgnoreCase);
        public string? Version { get; set; }
        public string? Path { get; set; }
        public string Health { get; set; } = "unknown";
        public string ValidationStatus { get; set; } = "unverified";
        public string? ValidationMessage { get; set; }

        public string StateSummary
        {
            get
            {
                if (IsSkipped) return "skipped for now";
                if (!IsDetected) return "not configured (missing)";
                string baseState = !string.IsNullOrWhiteSpace(Health) ? Health : "detected";
                return string.IsNullOrWhiteSpace(Version) ? baseState : baseState + " · " + Version;
            }
        }

        public bool CanInstall { get; set; }
        public string InstallActionLabel => IsDetected ? "Install / update…" : "Install with PaRuX…";

        public bool CanLocate { get; set; }
        public string LocateActionLabel => CanRelocate ? "Change location…" : "Locate existing…";
        public bool CanRelocate { get; set; }

        public bool CanRemove { get; set; }
        public string RemoveActionLabel => "Forget location";

        public bool CanSkip { get; set; }
        public string SkipActionLabel => "Skip for now";

        public bool CanUnskip { get; set; }
        public string UnskipActionLabel => "Configure now";
    }

    public sealed class SimpleModeRuntimeHealthInfo
    {
        public bool HasAnyComponents { get; set; }
        public bool IsInstalled { get; set; }
        public bool IsHealthy { get; set; }
        public bool IsSkipped { get; set; }
        public string PrimaryRuntimeId { get; set; } = string.Empty;
        public string PrimaryRuntimeName { get; set; } = "llama.cpp";
        public string? Version { get; set; }
        public string? Path { get; set; }
        public string OwnershipLabel { get; set; } = "None";
        public string Health { get; set; } = "unknown";
        /// <summary>
        /// Plain runtime health state for Simple mode: "Ready", "Starting" or "Needs attention".
        /// </summary>
        public string HealthState { get; set; } = "Needs attention";

        /// <summary>
        /// The next action the user can take, chosen by the presenter.
        /// </summary>
        public string NextAction { get; set; } = string.Empty;

        /// <summary>
        /// One plain line for the Simple mode indicator, e.g. "Ready · Start chatting in Chat".
        /// </summary>
        public string OneLineSummary => string.IsNullOrWhiteSpace(NextAction)
            ? HealthState
            : $"{HealthState} · {NextAction}";

        /// <summary>
        /// Text shown by the Simple mode runtime health indicator (the one-line summary).
        /// </summary>
        public string BadgeText => OneLineSummary;

        public string AccessibleName { get; set; } = string.Empty;
        public string TooltipText { get; set; } = string.Empty;
        public ComponentDisplayItem? PrimaryComponent { get; set; }
    }

    /// <summary>
    /// Pure, unit-testable presentation helper for local AI components and runtime lifecycle.
    /// Formats component state, ownership (PaRuX-managed, user-managed, system-detected),
    /// Simple mode runtime health indicators, and validates permitted lifecycle actions.
    /// </summary>
    public static class ComponentLifecyclePresenter
    {
        public static string FormatOwnership(ComponentOwnership ownership) => ownership switch
        {
            ComponentOwnership.PARUX_MANAGED => "PaRuX-managed",
            ComponentOwnership.USER_MANAGED => "User-managed",
            ComponentOwnership.SYSTEM_DETECTED => "System-detected",
            _ => "Unknown"
        };

        public static string? BackendForComponent(string? componentId) => componentId switch
        {
            "llama-server-cuda" => "text-cuda",
            "llama-server-vulkan" => "text-vulkan",
            "comfyui" => "media-comfyui",
            _ => null
        };

        public static ComponentDisplayItem FormatComponent(ComponentStatus component)
        {
            ArgumentNullException.ThrowIfNull(component);

            string[] actions = component.Actions ?? Array.Empty<string>();
            bool hasLocate = actions.Contains("locate", StringComparer.OrdinalIgnoreCase);
            bool hasRelocate = actions.Contains("relocate", StringComparer.OrdinalIgnoreCase);
            // PaRuX never installs or skips user-managed software.
            bool isUserManaged = component.Ownership == ComponentOwnership.USER_MANAGED;

            return new ComponentDisplayItem
            {
                Id = component.Id ?? string.Empty,
                DisplayName = string.IsNullOrWhiteSpace(component.DisplayName) ? (component.Id ?? "Unknown") : component.DisplayName,
                Ownership = component.Ownership,
                OwnershipLabel = FormatOwnership(component.Ownership),
                IsDetected = component.Detected,
                IsSkipped = component.Skipped,
                Version = component.Version,
                Path = component.Path,
                Health = string.IsNullOrWhiteSpace(component.Health) ? "unknown" : component.Health,
                ValidationStatus = string.IsNullOrWhiteSpace(component.ValidationStatus) ? "unverified" : component.ValidationStatus,
                ValidationMessage = component.ValidationMessage,
                CanInstall = !isUserManaged && actions.Contains("install", StringComparer.OrdinalIgnoreCase),
                CanLocate = hasLocate || hasRelocate,
                CanRelocate = hasRelocate,
                CanRemove = actions.Contains("remove", StringComparer.OrdinalIgnoreCase),
                CanSkip = !isUserManaged && actions.Contains("skip", StringComparer.OrdinalIgnoreCase),
                CanUnskip = actions.Contains("unskip", StringComparer.OrdinalIgnoreCase)
            };
        }

        public static SimpleModeRuntimeHealthInfo FormatSimpleModeRuntimeHealth(
            IReadOnlyList<ComponentStatus>? components,
            LocalAiState? localAiState = null)
        {
            if (components == null || components.Count == 0)
            {
                // A null inventory means the status could not be read, so it must not suggest installing anything.
                bool unreadable = components == null;
                string emptyAction = unreadable
                    ? "Check runtime status in Settings"
                    : "Install or locate a runtime in Settings";
                return new SimpleModeRuntimeHealthInfo
                {
                    HasAnyComponents = false,
                    IsInstalled = false,
                    IsHealthy = false,
                    IsSkipped = false,
                    PrimaryRuntimeId = string.Empty,
                    PrimaryRuntimeName = "llama.cpp",
                    OwnershipLabel = "None",
                    Health = "unknown",
                    HealthState = "Needs attention",
                    NextAction = emptyAction,
                    AccessibleName = $"Runtime health: Needs attention. Next action: {emptyAction}.",
                    TooltipText = unreadable
                        ? "Local runtime status could not be read.\nOpen Settings to check runtime components."
                        : "No local runtime components are configured.\nOpen Settings to install or locate a runtime."
                };
            }

            // Find primary text runtime (prefer detected/installed CUDA or Vulkan)
            ComponentStatus? textComponent = components.FirstOrDefault(c =>
                (string.Equals(c.Id, "llama-server-cuda", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(c.Id, "llama-server-vulkan", StringComparison.OrdinalIgnoreCase))
                && c.Detected)
                ?? components.FirstOrDefault(c =>
                    string.Equals(c.Id, "llama-server-cuda", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.Id, "llama-server-vulkan", StringComparison.OrdinalIgnoreCase));

            if (textComponent == null)
            {
                // Fallback to any component
                textComponent = components.FirstOrDefault();
            }

            if (textComponent == null)
            {
                return new SimpleModeRuntimeHealthInfo
                {
                    HasAnyComponents = true,
                    IsInstalled = false,
                    IsHealthy = false,
                    PrimaryRuntimeId = string.Empty,
                    PrimaryRuntimeName = "llama.cpp",
                    OwnershipLabel = "None",
                    Health = "unknown",
                    HealthState = "Needs attention",
                    NextAction = "Install or locate a runtime in Settings",
                    AccessibleName = "Runtime health: Needs attention. Next action: Install or locate a runtime in Settings.",
                    TooltipText = "No local runtime is configured.\nOpen Settings to manage runtime components."
                };
            }

            ComponentDisplayItem item = FormatComponent(textComponent);
            bool isExplicitlyHealthy = item.IsDetected && (string.Equals(item.Health, "healthy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ValidationStatus, "validated", StringComparison.OrdinalIgnoreCase));
            bool isUnhealthy = string.Equals(item.Health, "unhealthy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Health, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ValidationStatus, "unhealthy", StringComparison.OrdinalIgnoreCase);
            bool isSkipped = item.IsSkipped;

            string name = !string.IsNullOrWhiteSpace(item.DisplayName) ? item.DisplayName : "llama.cpp";
            string versionStr = !string.IsNullOrWhiteSpace(item.Version) ? " " + item.Version : string.Empty;
            string ownershipStr = item.OwnershipLabel;

            string healthState;
            string nextAction;

            if (localAiState?.GameActive == true)
            {
                healthState = "Needs attention";
                nextAction = "Paused for gaming";
            }
            else if (localAiState?.ServerProcessPresent == true && !localAiState.Healthy)
            {
                healthState = "Starting";
                nextAction = "Loading local engine…";
            }
            else if (isSkipped)
            {
                healthState = "Needs attention";
                nextAction = "Re-enable runtime in Settings";
            }
            else if (!item.IsDetected)
            {
                healthState = "Needs attention";
                nextAction = "Install or locate a runtime in Settings";
            }
            else if (isUnhealthy)
            {
                healthState = "Needs attention";
                nextAction = "Check runtime status in Settings";
            }
            else if (localAiState?.Healthy == true || isExplicitlyHealthy)
            {
                healthState = "Ready";
                nextAction = "Start chatting in Chat";
            }
            else if (string.Equals(item.Health, "starting", StringComparison.OrdinalIgnoreCase))
            {
                healthState = "Starting";
                nextAction = "Loading local engine…";
            }
            else if (item.Ownership != ComponentOwnership.PARUX_MANAGED
                && string.Equals(item.ValidationStatus, "unverified", StringComparison.OrdinalIgnoreCase))
            {
                // A located or system binary that did not identify itself is not evidence that the runtime works.
                // PaRuX-managed runtimes that are installed but stopped are verified when they start, so they stay Ready.
                healthState = "Needs attention";
                nextAction = "Check runtime status in Settings";
            }
            else
            {
                // Detected and idle, with no contrary evidence
                healthState = "Ready";
                nextAction = "Start chatting in Chat";
            }

            string accessibleName = $"Runtime health: {healthState}. Next action: {nextAction}. " +
                $"Component: {name}, installed: {(item.IsInstalled ? "yes" : "no")}, " +
                $"version: {(string.IsNullOrWhiteSpace(item.Version) ? "unknown" : item.Version)}, " +
                $"path: {(string.IsNullOrWhiteSpace(item.Path) ? "none" : item.Path)}, " +
                $"owner: {ownershipStr}, health: {(isSkipped ? "skipped" : item.Health)}";

            var tooltipLines = new List<string>
            {
                "Local Text Runtime Health:",
                $"• Status: {healthState}",
                $"• Next action: {nextAction}",
                $"• Component: {name}",
                $"• Installed: {(item.IsInstalled ? "Yes" : "No")}",
                $"• Version: {(string.IsNullOrWhiteSpace(item.Version) ? "None" : item.Version)}",
                $"• Path: {(string.IsNullOrWhiteSpace(item.Path) ? "None" : item.Path)}",
                $"• Owner: {ownershipStr}",
                $"• Health: {(isSkipped ? "Skipped for now" : item.Health)}"
            };

            if (!string.IsNullOrWhiteSpace(item.ValidationMessage))
            {
                tooltipLines.Add($"• Details: {item.ValidationMessage}");
            }

            // Summary of other components
            var otherComponents = components.Where(c => c.Id != textComponent.Id).ToList();
            if (otherComponents.Count > 0)
            {
                tooltipLines.Add("\nOther components:");
                foreach (var oc in otherComponents)
                {
                    string ocState = oc.Detected ? oc.Health : oc.Skipped ? "skipped" : "missing";
                    string ocOwner = FormatOwnership(oc.Ownership);
                    tooltipLines.Add($"• {oc.DisplayName}: {ocState} ({ocOwner})");
                }
            }

            tooltipLines.Add("\nClick to open Settings and manage components.");

            return new SimpleModeRuntimeHealthInfo
            {
                HasAnyComponents = true,
                IsInstalled = item.IsInstalled,
                IsHealthy = string.Equals(healthState, "Ready", StringComparison.Ordinal),
                IsSkipped = isSkipped,
                PrimaryRuntimeId = item.Id,
                PrimaryRuntimeName = name,
                Version = item.Version,
                Path = item.Path,
                OwnershipLabel = ownershipStr,
                Health = item.Health,
                HealthState = healthState,
                NextAction = nextAction,
                AccessibleName = accessibleName,
                TooltipText = string.Join("\n", tooltipLines),
                PrimaryComponent = item
            };
        }

        public static RuntimeBuildOption? SelectRecommendedBuild(IReadOnlyList<RuntimeBuildOption>? builds)
        {
            if (builds == null || builds.Count == 0) return null;
            return builds.FirstOrDefault(b => b.Recommended) ?? builds.FirstOrDefault();
        }

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
            return val.ToString(unitIndex == 0 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unitIndex];
        }

        public static string FormatInstallProgress(string displayName, string stage, long bytesDownloaded, long totalBytes)
        {
            string bytes = totalBytes > 0
                ? " · " + FormatBytes(bytesDownloaded) + " / " + FormatBytes(totalBytes)
                : string.Empty;
            return displayName + ": " + stage + bytes;
        }

        public static string FormatInstallCompletion(string displayName, RuntimeInstallStatus status)
        {
            ArgumentNullException.ThrowIfNull(status);
            string variant = string.IsNullOrWhiteSpace(status.Variant) ? string.Empty : " (" + status.Variant + ")";

            if (string.Equals(status.Stage, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return $"{displayName} install failed: {status.Error ?? "Unknown error"}";
            }
            if (string.Equals(status.Stage, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return $"{displayName} install was cancelled.";
            }

            if (!string.IsNullOrWhiteSpace(status.PerformanceDecision))
            {
                string active = string.IsNullOrWhiteSpace(status.ActiveTag) ? status.Tag : status.ActiveTag;
                return $"{displayName} candidate installed: {status.Tag}{variant} · active: {active} · performance gate: {status.PerformanceDecision}";
            }

            return $"{displayName} installed successfully: {status.Tag}{variant}.";
        }

        public static bool IsActionPermitted(ComponentStatus component, ComponentSetupAction action)
        {
            ArgumentNullException.ThrowIfNull(component);
            string[] actions = component.Actions ?? Array.Empty<string>();

            switch (action)
            {
                case ComponentSetupAction.Install:
                    // USER_MANAGED software can NEVER be installed/overwritten by PaRuX
                    if (component.Ownership == ComponentOwnership.USER_MANAGED)
                        return false;
                    return actions.Contains("install", StringComparer.OrdinalIgnoreCase);

                case ComponentSetupAction.Locate:
                    return actions.Contains("locate", StringComparer.OrdinalIgnoreCase)
                        || actions.Contains("relocate", StringComparer.OrdinalIgnoreCase);

                case ComponentSetupAction.Remove:
                    // Remove is for user-managed registered paths
                    return actions.Contains("remove", StringComparer.OrdinalIgnoreCase);

                case ComponentSetupAction.Skip:
                    if (component.Ownership == ComponentOwnership.USER_MANAGED)
                        return false;
                    return actions.Contains("skip", StringComparer.OrdinalIgnoreCase);

                case ComponentSetupAction.Unskip:
                    return actions.Contains("unskip", StringComparer.OrdinalIgnoreCase);

                case ComponentSetupAction.Detect:
                    return true;

                default:
                    return false;
            }
        }
    }
}
