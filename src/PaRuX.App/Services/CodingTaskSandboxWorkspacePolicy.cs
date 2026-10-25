using System;
using System.IO;
using System.Linq;
using PaRuX.Models;

namespace PaRuX.Services
{
    public sealed class CodingWorkspaceBindingResult
    {
        public bool Success { get; init; }
        public string? PreviousWorkspace { get; init; }
        public string? BoundRepository { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// Pure policy and helpers for the coding agent golden path:
    /// - Starts coding tasks without requiring a folder upfront, in an isolated sandbox workspace by default.
    /// - Enables binding a chosen repository to the task later.
    /// - Provides deterministic display names and path validation.
    /// </summary>
    public static class CodingTaskSandboxWorkspacePolicy
    {
        public const string DefaultSandboxFolderName = "sandbox-workspace";
        public const string DefaultSandboxDisplayName = "Sandbox Workspace";

        public static string GetDefaultSandboxWorkspaceRoot(string? stateDirectory = null)
        {
            string baseDir = !string.IsNullOrWhiteSpace(stateDirectory)
                ? stateDirectory
                : AppPaths.CodingAgentStateDir;
            return Path.Combine(baseDir, DefaultSandboxFolderName);
        }

        public static bool IsDefaultSandboxWorkspace(string? path, string? stateDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string defaultRoot = GetDefaultSandboxWorkspaceRoot(stateDirectory);
            try
            {
                return string.Equals(
                    Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(defaultRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        public static string GetWorkspaceDisplayName(string? workspacePath, string? stateDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
                return "Autonomous Coding Agent";

            if (IsDefaultSandboxWorkspace(workspacePath, stateDirectory))
                return DefaultSandboxDisplayName;

            return Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        public static string ResolveEffectiveCodingRoot(
            string? explicitPath,
            string? projectWorkspaceRoot,
            string? configuredSettingsRoot,
            bool fallbackToDefaultSandbox = true,
            string? stateDirectory = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath) && (Directory.Exists(explicitPath) || !Path.IsPathRooted(explicitPath)))
                return explicitPath;

            if (!string.IsNullOrWhiteSpace(projectWorkspaceRoot) && Directory.Exists(projectWorkspaceRoot))
                return projectWorkspaceRoot;

            if (!string.IsNullOrWhiteSpace(configuredSettingsRoot) && Directory.Exists(configuredSettingsRoot))
                return configuredSettingsRoot;

            if (fallbackToDefaultSandbox)
                return EnsureSandboxWorkspaceCreated(stateDirectory);

            return string.Empty;
        }

        public static string EnsureSandboxWorkspaceCreated(string? stateDirectory = null)
        {
            string root = GetDefaultSandboxWorkspaceRoot(stateDirectory);
            try
            {
                Directory.CreateDirectory(root);
            }
            catch
            {
                // In pure test harnesses without disk permissions, return the deterministic path.
            }
            return root;
        }

        public static CodingWorkspaceBindingResult BindSandboxTaskToRepository(ChatMessage message, string newRepositoryRoot)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (string.IsNullOrWhiteSpace(newRepositoryRoot) || !Directory.Exists(newRepositoryRoot))
            {
                return new CodingWorkspaceBindingResult
                {
                    Success = false,
                    Message = "Target repository folder does not exist."
                };
            }

            string previous = message.CodeWorkspacePath ?? string.Empty;
            string canonical = Path.GetFullPath(newRepositoryRoot);

            message.CodeWorkspacePath = canonical;

            return new CodingWorkspaceBindingResult
            {
                Success = true,
                PreviousWorkspace = previous,
                BoundRepository = canonical,
                Message = $"Task bound to repository '{Path.GetFileName(canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}'."
            };
        }

        public static int TryBindConversationSandboxTasks(Conversation? conversation, string newRepositoryRoot)
        {
            if (conversation == null || string.IsNullOrWhiteSpace(newRepositoryRoot) || !Directory.Exists(newRepositoryRoot))
                return 0;

            int boundCount = 0;
            foreach (ChatMessage message in conversation.Messages)
            {
                if (string.Equals(message.ActionKind, "code-handoff", StringComparison.Ordinal)
                    && (string.IsNullOrWhiteSpace(message.CodeWorkspacePath) || IsDefaultSandboxWorkspace(message.CodeWorkspacePath)))
                {
                    CodingWorkspaceBindingResult result = BindSandboxTaskToRepository(message, newRepositoryRoot);
                    if (result.Success) boundCount++;
                }
            }

            return boundCount;
        }
    }
}
