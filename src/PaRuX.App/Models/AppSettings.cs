using System.Collections.Generic;
using PaRuX.Contracts;

namespace PaRuX.Models
{
    public class AppSettings
    {
        public string Theme { get; set; } = "System";
        public bool StartWithWindows { get; set; }
        public bool StudioModeEnabled { get; set; }
        public bool OnboardingCompleted { get; set; }
        /// <summary>Model Hub suggestion lane the user last picked: "standard" or "uncensored". Empty means not chosen (Standard is shown).</summary>
        public string ModelHubSuggestionLane { get; set; } = string.Empty;
        public bool GuidedTourCompleted { get; set; }
        public string BubbleMode { get; set; } = "AlwaysVisible";
        public int BubbleX { get; set; } = -1;
        public int BubbleY { get; set; } = -1;
        public bool BubbleManuallyPositioned { get; set; }
        public string Hotkey { get; set; } = "Ctrl+Alt+Space";
        public string DefaultProfile { get; set; } = "AUTO";

        public int ChatRoutingMirrorVersion { get; set; }
        public int CapabilityRoutingMirrorVersion { get; set; }
        public List<ScopedRoutingOverrideDto> CapabilityRoutingOverrides { get; set; } = new List<ScopedRoutingOverrideDto>();
        public string ChatModelSelection { get; set; } = "parux-auto:balanced";
        public string ChatRoutingAutoProfile { get; set; } = "balanced";
        public double ChatAutoMinimumCapabilityRatio { get; set; } = 0.68;
        public double ChatAutoComplexityFloorBoost { get; set; } = 0.24;
        public double ChatAutoBaseCapabilityWeight { get; set; } = 0.46;
        public double ChatAutoBasePerformanceWeight { get; set; } = 0.31;
        public double ChatAutoCompatibilityWeight { get; set; } = 0.16;
        public double ChatAutoLoadedReuseWeight { get; set; } = 0.07;
        public double ChatAutoComplexityCapabilityBoost { get; set; } = 0.26;
        public double ChatAutoComplexityPerformancePenalty { get; set; } = 0.18;

        public string LocalAiRootPath { get; set; } = string.Empty;
        public int MaxImagesPerMessage { get; set; } = 4;
        public int MaxImageMegabytes { get; set; } = 20;
        public int GeneratedImageWidth { get; set; } = 1024;
        public int GeneratedImageHeight { get; set; } = 1024;
        public string GeneratedImagesFolder { get; set; } = string.Empty;
        public int GeneratedVideoWidth { get; set; } = 1280;
        public int GeneratedVideoHeight { get; set; } = 720;
        public double GeneratedVideoDurationSeconds { get; set; } = 5;
        public int GeneratedVideoFps { get; set; } = 24;
        public int GeneratedVideoSteps { get; set; } = 30;
        public string GeneratedVideosFolder { get; set; } = string.Empty;
        public int MaxVideoMegabytes { get; set; } = 500;
        public int MaxDocumentsPerMessage { get; set; } = 4;
        public int MaxDocumentMegabytes { get; set; } = 100;
        public int StorageQuotaGigabytes { get; set; } = 350;
        public int AiStorageBudgetGigabytes { get; set; } = 600;
        public int AiStorageWarningGigabytes { get; set; } = 700;

        /// <summary>Optional editor command used only to open a PaRuX-owned task worktree for review.</summary>
        public string VsCodeCommand { get; set; } = "code";

        /// <summary>Coding agent may batch 2-6 independent read-only actions into one model step (default on).</summary>
        public bool CodingBatchReadEnabled { get; set; } = true;

        /// <summary>
        /// Coding agent may keep working while read-only advisory child tasks run, instead of pausing
        /// behind them (default on). It never lets a child write, run commands or use the network.
        /// </summary>
        public bool CodingParentContinuationEnabled { get; set; } = true;

        /// <summary>
        /// Permission profile copied into NEW native coding tasks. `offline` is deliberately the
        /// default. `package-fetch` permits only PaRuX-discovered fetch/no-lifecycle-script restore;
        /// `trusted-restore` also permits restore paths that can execute repository/plugin/build
        /// backend code inside the AppContainer. Existing running tasks never inherit later setting
        /// changes.
        /// </summary>
        public string CodingPermissionProfile { get; set; } = "offline";

        public string OpenCodeCommand { get; set; } = "opencode";
        private string _agentCodingWorkspaceRoot = string.Empty;
        /// <summary>Normalized on every write (settings load, Settings text box, folder picker): never a path inside .git.</summary>
        public string AgentCodingWorkspaceRoot
        {
            get => _agentCodingWorkspaceRoot;
            set => _agentCodingWorkspaceRoot = PaRuX.Services.CodingWorkspacePath.Normalize(value);
        }
        public string BinaryAnalysisWorkspaceRoot { get; set; } = string.Empty;
        public List<ComputerAppShortcut> ComputerApps { get; set; } = new List<ComputerAppShortcut>();
        public bool ComputerUseFullAutomationEnabled { get; set; }

        public bool NotifyOnGeneration { get; set; } = true;
        public bool NotifyOnDownloads { get; set; } = true;
        public bool NotifyOnInstalls { get; set; } = true;
    }
}
