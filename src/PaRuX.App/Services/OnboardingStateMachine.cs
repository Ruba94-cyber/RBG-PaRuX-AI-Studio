using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    public enum OnboardingStep
    {
        RuntimeLocation,
        Components,
        FirstModel,
        ModelHubHandoff,
        ReadyCheck,
        Completed
    }

    public enum ComponentSetupAction
    {
        Detect,
        Locate,
        Install,
        Skip,
        Unskip,
        Remove
    }

    public sealed class ComponentState
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Ownership { get; set; } = string.Empty;
        public bool Detected { get; set; }
        public bool Skipped { get; set; }
        public string? Version { get; set; }
        public string? Path { get; set; }
        public string? Health { get; set; }

        public bool IsTextRuntime =>
            string.Equals(Id, "llama-server-cuda", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Id, "llama-server-vulkan", StringComparison.OrdinalIgnoreCase);

        public bool IsMediaRuntime =>
            string.Equals(Id, "comfyui", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class ChatReadinessEvidence
    {
        public bool RuntimeHealthy { get; set; }
        public string? RuntimeSummary { get; set; }
        public bool ModelAvailable { get; set; }
        public string? ModelSummary { get; set; }
        public bool ProbeSucceeded { get; set; }
        public string? ProbeSummary { get; set; }
        public string? ErrorMessage { get; set; }
        public bool ChatAnswerVerified { get; set; }

        public static ChatReadinessEvidence Create(
            bool runtimeHealthy,
            string? runtimeSummary,
            bool modelAvailable,
            string? modelSummary,
            bool probeSucceeded,
            string? probeSummary,
            string? errorMessage = null,
            bool? chatAnswerVerified = null)
        {
            return new ChatReadinessEvidence
            {
                RuntimeHealthy = runtimeHealthy,
                RuntimeSummary = runtimeSummary,
                ModelAvailable = modelAvailable,
                ModelSummary = modelSummary,
                ProbeSucceeded = probeSucceeded,
                ProbeSummary = probeSummary,
                ErrorMessage = errorMessage,
                ChatAnswerVerified = chatAnswerVerified ?? false
            };
        }
    }

    public sealed class ReadinessAssessment
    {
        public bool IsReady { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string? FailureReason { get; set; }
        public string? NextAction { get; set; }
        public OnboardingStep FailedStep { get; set; } = OnboardingStep.ReadyCheck;

        public static ReadinessAssessment Ready(string summary) =>
            new ReadinessAssessment
            {
                IsReady = true,
                Summary = summary,
                FailureReason = null,
                NextAction = "Press Get Started to enter Chat."
            };

        public static ReadinessAssessment NotReady(OnboardingStep step, string reason, string nextAction) =>
            new ReadinessAssessment
            {
                IsReady = false,
                Summary = "Setup is incomplete: " + reason,
                FailureReason = reason,
                NextAction = nextAction,
                FailedStep = step
            };
    }

    /// <summary>
    /// Pure state machine governing the first-run onboarding golden path to a working chat.
    /// Manages linear flow, component actions (detect, locate, install, skip), first-model handoff
    /// and return, and validates readiness with verified chat evidence (runtime healthy + model loaded + probe result + verified answer).
    /// </summary>
    public sealed class OnboardingStateMachine
    {
        private readonly List<ComponentState> _components = new();
        private string? _observedTextModelConfiguration;

        public OnboardingStep CurrentStep { get; private set; } = OnboardingStep.RuntimeLocation;
        public string? LocalAiRootPath { get; private set; }
        public IReadOnlyList<ComponentState> Components => _components.AsReadOnly();
        public string? SelectedModelRepoId { get; private set; }
        public bool IsInModelHubHandoff { get; private set; }
        public bool ChatAnswerVerified { get; private set; }
        /// <summary>Increments when the runtime, model or storage setup changes meaningfully.</summary>
        public int ConfigurationRevision { get; private set; }
        public ChatReadinessEvidence? LastEvidence { get; private set; }
        public ReadinessAssessment CurrentReadiness { get; private set; } =
            ReadinessAssessment.NotReady(
                OnboardingStep.RuntimeLocation,
                "Runtime and model setup not yet evaluated.",
                "Review local runtime folder and components.");
        public bool IsCompleted { get; private set; }
        public IReadOnlyList<string> UnprovenItems { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// True if the bundled baseline Vulkan text runtime is detected, providing a working text path with 0 B runtime download.
        /// </summary>
        public bool HasBundledTextRuntime =>
            _components.Any(c => string.Equals(c.Id, "llama-server-vulkan", StringComparison.OrdinalIgnoreCase) && c.Detected);

        public void ConfigureRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be empty.", nameof(path));

            string nextRoot = path.Trim();
            if (LocalAiRootPath != null
                && !string.Equals(LocalAiRootPath, nextRoot, StringComparison.OrdinalIgnoreCase))
                InvalidateReadiness("Local AI storage location changed; chat must be verified again.");
            LocalAiRootPath = nextRoot;
            if (CurrentStep == OnboardingStep.RuntimeLocation)
            {
                CurrentStep = OnboardingStep.Components;
            }
        }

        public void InvalidateReadiness(string reason = "Component configuration changed; readiness must be re-evaluated.")
        {
            CurrentReadiness = ReadinessAssessment.NotReady(
                CurrentStep < OnboardingStep.ReadyCheck ? CurrentStep : OnboardingStep.ReadyCheck,
                reason,
                "Run the ready check to verify chat readiness.");
            ChatAnswerVerified = false;
            LastEvidence = null;
            ConfigurationRevision++;
        }

        /// <summary>
        /// A status refresh must not erase a successfully verified chat answer. Only a meaningful
        /// component change invalidates readiness. Copy the incoming objects so later mutation by
        /// a discovery caller cannot silently change our saved comparison baseline.
        /// </summary>
        public void SetComponents(IEnumerable<ComponentState> components)
        {
            if (components == null) throw new ArgumentNullException(nameof(components));

            List<ComponentState> latest = components.Select(component =>
            {
                if (component == null) throw new ArgumentException("Component entries cannot be null.", nameof(components));
                return new ComponentState
                {
                    Id = component.Id,
                    DisplayName = component.DisplayName,
                    Ownership = component.Ownership,
                    Detected = component.Detected,
                    Skipped = component.Skipped,
                    Version = component.Version,
                    Path = component.Path,
                    Health = component.Health
                };
            }).ToList();

            bool unchanged = latest.Count == _components.Count
                && latest.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                    .Zip(_components.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase),
                        (a, b) => SameComponentState(a, b))
                    .All(equal => equal);

            _components.Clear();
            _components.AddRange(latest);
            if (!unchanged)
                InvalidateReadiness("Component configuration changed; readiness must be re-evaluated.");
            if (CurrentStep == OnboardingStep.Components)
                CurrentStep = OnboardingStep.FirstModel;
        }

        private static bool SameComponentState(ComponentState a, ComponentState b) =>
            string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Ownership, b.Ownership, StringComparison.Ordinal)
            && a.Detected == b.Detected
            && a.Skipped == b.Skipped
            && string.Equals(a.Version, b.Version, StringComparison.Ordinal)
            && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Health, b.Health, StringComparison.Ordinal);

        public void ApplyComponentAction(string componentId, ComponentSetupAction action, string? path = null)
        {
            ComponentState? existing = _components.FirstOrDefault(c => string.Equals(c.Id, componentId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new ComponentState { Id = componentId, DisplayName = componentId };
                _components.Add(existing);
            }

            switch (action)
            {
                case ComponentSetupAction.Detect:
                    // Discovery refresh preserves existing state unless updated via SetComponents
                    break;

                case ComponentSetupAction.Locate:
                    existing.Detected = !string.IsNullOrWhiteSpace(path);
                    existing.Path = path;
                    existing.Ownership = "USER_MANAGED";
                    existing.Skipped = false;
                    existing.Health = existing.Detected ? "healthy" : "missing";
                    break;

                case ComponentSetupAction.Install:
                    existing.Detected = true;
                    existing.Ownership = "PARUX_MANAGED";
                    existing.Skipped = false;
                    existing.Health = "healthy";
                    break;

                case ComponentSetupAction.Skip:
                    existing.Skipped = true;
                    existing.Health = "skipped for now";
                    break;

                case ComponentSetupAction.Unskip:
                    existing.Skipped = false;
                    existing.Health = existing.Detected ? "healthy" : "not configured";
                    break;

                case ComponentSetupAction.Remove:
                    existing.Detected = false;
                    existing.Path = null;
                    existing.Ownership = string.Empty;
                    existing.Health = "not configured";
                    break;
            }

            if (action != ComponentSetupAction.Detect)
            {
                InvalidateReadiness($"Component '{componentId}' changed; readiness must be re-evaluated.");
            }
        }

        /// <summary>
        /// The configured Core text roles are stable across a harmless readiness refresh. A
        /// reassignment, missing model file, or failed role query invalidates a verified answer.
        /// Deliberately exclude the loaded/active runtime model: cold-start verification itself
        /// may load the configured model without changing the user's selected role.
        /// </summary>
        public void ObserveTextModelConfiguration(
            IEnumerable<(string Role, string ModelPath, bool FileExists, bool IsOverridden)>? textRoles)
        {
            string? snapshot = textRoles == null ? null : string.Join("\n", textRoles
                .Select(role => (
                    Role: (role.Role ?? string.Empty).Trim(),
                    ModelPath: (role.ModelPath ?? string.Empty).Trim(),
                    role.FileExists,
                    role.IsOverridden))
                .OrderBy(role => role.Role, StringComparer.OrdinalIgnoreCase)
                .ThenBy(role => role.ModelPath, StringComparer.OrdinalIgnoreCase)
                .Select(role => role.Role + "|" + role.ModelPath + "|"
                    + (role.FileExists ? "present" : "missing") + "|"
                    + (role.IsOverridden ? "override" : "automatic")));

            if (string.Equals(_observedTextModelConfiguration, snapshot, StringComparison.OrdinalIgnoreCase))
                return;

            _observedTextModelConfiguration = snapshot;
            InvalidateReadiness("Chat model configuration changed or became unavailable; verify local chat again.");
        }

        public void SelectFirstModel(string repoId)
        {
            if (string.IsNullOrWhiteSpace(repoId))
                throw new ArgumentException("Repo ID cannot be empty.", nameof(repoId));

            string nextRepo = repoId.Trim();
            if (SelectedModelRepoId != null
                && !string.Equals(SelectedModelRepoId, nextRepo, StringComparison.Ordinal))
                InvalidateReadiness("Selected chat model changed; chat must be verified again.");
            SelectedModelRepoId = nextRepo;
            if (CurrentStep < OnboardingStep.FirstModel)
            {
                CurrentStep = OnboardingStep.FirstModel;
            }
        }

        /// <summary>
        /// Initiates handoff to Model Hub to search and download the chosen first model.
        /// NOTE: Does NOT complete onboarding. Onboarding remains active so the user can return.
        /// </summary>
        public void HandoffToModelHub(string repoId)
        {
            SelectFirstModel(repoId);
            IsInModelHubHandoff = true;
            CurrentStep = OnboardingStep.ModelHubHandoff;
        }

        /// <summary>
        /// Starts a Model Hub handoff when a repository ID is available. Missing IDs leave onboarding unchanged.
        /// </summary>
        public bool TryHandoffToModelHub(string? repoId)
        {
            if (string.IsNullOrWhiteSpace(repoId))
            {
                return false;
            }

            HandoffToModelHub(repoId);
            return true;
        }

        /// <summary>
        /// Returns to onboarding after browsing or downloading in Model Hub.
        /// </summary>
        public void ReturnFromModelHub()
        {
            IsInModelHubHandoff = false;
            CurrentStep = OnboardingStep.ReadyCheck;
            // The model on disk or its active selection may have changed while Model Hub was open.
            InvalidateReadiness("Model Hub selection may have changed; verify chat again.");
        }

        /// <summary>
        /// Evaluates the model download state during onboarding handoff using OnboardingReturnPolicy.
        /// If download completed, automatically transitions the state machine back from Model Hub to ReadyCheck.
        /// </summary>
        public OnboardingReturnAction HandleDownloadState(OnboardingDownloadState downloadState)
        {
            bool answerVerified = ChatAnswerVerified || (LastEvidence?.ChatAnswerVerified ?? false);
            OnboardingReturnAction action = OnboardingReturnPolicy.Evaluate(downloadState, answerVerified);

            if (action == OnboardingReturnAction.ReturnToSetup && IsInModelHubHandoff)
            {
                ReturnFromModelHub();
            }

            return action;
        }

        public OnboardingReturnAction HandleDownloadState(string? downloadStatus) =>
            HandleDownloadState(OnboardingReturnPolicy.ParseDownloadState(downloadStatus));

        public void RecordVerifiedChatAnswer()
        {
            ChatAnswerVerified = true;
            if (LastEvidence != null)
            {
                LastEvidence.ChatAnswerVerified = true;
            }
        }

        /// <summary>
        /// Evaluates whether chat is really working based on verified evidence:
        /// 1. Text runtime is healthy (detected or healthy).
        /// 2. Model is downloaded and available/selected.
        /// 3. Round-trip or health probe succeeded.
        /// 4. Chat turn has produced a verified answer.
        /// </summary>
        public ReadinessAssessment EvaluateReadiness(ChatReadinessEvidence evidence)
        {
            LastEvidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
            // A verified answer is durable across harmless UI refreshes, but is cleared when the
            // runtime, model selection or local-AI root materially changes.
            evidence.ChatAnswerVerified = evidence.ChatAnswerVerified || ChatAnswerVerified;

            // Factor 1: Runtime
            bool hasTextRuntime = _components.Any(c => c.IsTextRuntime && c.Detected);
            bool hasTextComponents = _components.Any(c => c.IsTextRuntime);
            bool textRuntimeSkipped = hasTextComponents && _components.Where(c => c.IsTextRuntime).All(c => c.Detected || c.Skipped);

            if (!evidence.RuntimeHealthy && !hasTextRuntime)
            {
                if (textRuntimeSkipped)
                {
                    CurrentReadiness = ReadinessAssessment.NotReady(
                        OnboardingStep.Components,
                        "PaRuX text runtime was skipped, and no external provider is verified.",
                        "Install a local llama.cpp text runtime or configure an external provider.");
                }
                else
                {
                    CurrentReadiness = ReadinessAssessment.NotReady(
                        OnboardingStep.Components,
                        "No text runtime is installed or detected.",
                        "PaRuX bundles the Vulkan runtime in the installer — choose 'Install with PaRuX' or 'Locate existing' for llama.cpp in Components.");
                }
                return CurrentReadiness;
            }

            // Factor 2: Model
            if (!evidence.ModelAvailable)
            {
                CurrentReadiness = ReadinessAssessment.NotReady(
                    OnboardingStep.FirstModel,
                    "No chat model is installed or loaded.",
                    "Select a recommended model and download it in Model Hub.");
                return CurrentReadiness;
            }

            // Factor 3: Round-trip or health probe
            if (!evidence.ProbeSucceeded)
            {
                string detail = !string.IsNullOrWhiteSpace(evidence.ErrorMessage)
                    ? " (" + evidence.ErrorMessage + ")"
                    : string.Empty;
                CurrentReadiness = ReadinessAssessment.NotReady(
                    OnboardingStep.ReadyCheck,
                    "Local chat health probe failed" + detail + ".",
                    "Verify the local runtime service is running or retry the ready check.");
                return CurrentReadiness;
            }

            // Factor 4: Verified chat answer
            if (!evidence.ChatAnswerVerified)
            {
                CurrentReadiness = ReadinessAssessment.NotReady(
                    OnboardingStep.ReadyCheck,
                    "Model is downloaded on disk, but chat readiness requires a verified chat answer.",
                    "Verify chat produces an answer before completing onboarding.");
                return CurrentReadiness;
            }

            string runtimeDesc = HasBundledTextRuntime
                ? "bundled local text runtime (Vulkan) is healthy and ready without extra download"
                : "local text runtime is healthy";
            string summary = "Chat is fully configured: " + runtimeDesc + ", model is available ("
                + (evidence.ModelSummary ?? "active") + "), and chat health probe passed.";
            CurrentReadiness = ReadinessAssessment.Ready(summary);
            CurrentStep = OnboardingStep.ReadyCheck;
            return CurrentReadiness;
        }

        public IReadOnlyList<string> GetUnprovenItems()
        {
            if (CanComplete())
            {
                return Array.Empty<string>();
            }

            var list = new List<string>();

            bool hasTextRuntime = (LastEvidence?.RuntimeHealthy == true) || _components.Any(c => c.IsTextRuntime && c.Detected);
            bool hasTextComponents = _components.Any(c => c.IsTextRuntime);
            bool textRuntimeSkipped = hasTextComponents && _components.Where(c => c.IsTextRuntime).All(c => c.Detected || c.Skipped);

            if (!hasTextRuntime)
            {
                if (textRuntimeSkipped)
                {
                    list.Add("PaRuX text runtime was skipped, and no external provider is verified.");
                }
                else
                {
                    list.Add("No text runtime is installed or detected.");
                }
            }

            if (!(LastEvidence?.ModelAvailable ?? false))
            {
                list.Add("No chat model is installed or loaded.");
            }

            if (!(LastEvidence?.ProbeSucceeded ?? false))
            {
                string detail = !string.IsNullOrWhiteSpace(LastEvidence?.ErrorMessage)
                    ? " (" + LastEvidence.ErrorMessage + ")"
                    : string.Empty;
                list.Add("Local chat health probe has not succeeded" + detail + ".");
            }

            if (!ChatAnswerVerified && !(LastEvidence?.ChatAnswerVerified ?? false))
            {
                list.Add("Chat answer has not been verified.");
            }

            if (list.Count == 0 && !string.IsNullOrWhiteSpace(CurrentReadiness.FailureReason))
            {
                list.Add(CurrentReadiness.FailureReason!);
            }

            if (list.Count == 0)
            {
                list.Add("Setup is incomplete; unproven readiness accepted by user.");
            }

            return list;
        }

        public bool CanComplete() =>
            CurrentReadiness.IsReady && (ChatAnswerVerified || (LastEvidence?.ChatAnswerVerified ?? false));

        public bool Complete()
        {
            if (!CanComplete())
            {
                return false;
            }

            UnprovenItems = Array.Empty<string>();
            IsCompleted = true;
            CurrentStep = OnboardingStep.Completed;
            return true;
        }

        public void ForceCompleteWithWarning(IEnumerable<string>? unprovenItems = null)
        {
            // Allowed for user override if they explicitly proceed without full local stack
            IReadOnlyList<string> items = unprovenItems != null
                ? unprovenItems.ToList()
                : GetUnprovenItems();

            if (items.Count == 0)
            {
                items = new[] { CurrentReadiness.FailureReason ?? "Setup is incomplete; unproven readiness accepted by user." };
            }

            UnprovenItems = items;
            IsCompleted = true;
            CurrentStep = OnboardingStep.Completed;
        }
    }
}
