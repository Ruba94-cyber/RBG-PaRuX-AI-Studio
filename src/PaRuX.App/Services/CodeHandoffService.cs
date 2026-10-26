using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Models;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PaRuX.Services
{
    public sealed class CodeHandoffResult
    {
        public bool Cancelled { get; set; }
        public string WorkspacePath { get; set; } = string.Empty;
        public string RepositoryPath { get; set; } = string.Empty;
        public string HandoffPath { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public bool NativeAgentStarted { get; set; }
        public bool VsCodeStarted { get; set; }
        public bool OpenCodeStarted { get; set; }
    }

    public sealed class CodeHandoffCheckResult
    {
        public bool IsGitRepository { get; set; }
        public bool HasChanges { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string DiffStat { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }

    /// <summary>
    /// Native PaRuX coding handoff. Root task creation goes through the same Core-owned desktop
    /// coding boundary used by CLI/IDE, so repository authority, bounded self-delegation and the
    /// immutable package-restore profile cannot drift between surfaces. Worktree/checkpoint/verifier
    /// remain one durable task state; VS Code is only an optional review surface.
    /// </summary>
    public static class CodeHandoffService
    {
        public static async Task<CodeHandoffResult> PrepareAndLaunchAsync(
            Conversation conversation, string codingRunPermissionMode = CodingRunPermissionModes.Bypass)
        {
            if (GamingManager.Current.IsActive)
                throw new InvalidOperationException("Gaming Mode is active. Coding agents are suspended.");

            string objective = NormalizeObjective(
                conversation.Messages.LastOrDefault(m => m.Role == "user")?.Content,
                conversation.Title);
            string? activeProjectId = ProjectWorkspaceSession.Current.ProjectId;
            string? explicitWorkspace = CodingTaskIntent.ExtractExplicitWindowsPath(objective);
            (bool cancelled, string repository) = await ResolveRepositoryAsync(
                activeProjectId, objective, explicitWorkspace).ConfigureAwait(false);
            if (cancelled) return new CodeHandoffResult { Cancelled = true };

            // A path-bearing request is self-scoped. Carrying unrelated history from the previously
            // configured repository can poison both retrieval and edits (for example Syntima
            // evidence attached to a request that explicitly targets a Desktop test application).
            string nativeGoal = BuildNativeGoal(objective, conversation, includeConversationContext: explicitWorkspace == null);
            nativeGoal = CodingTaskIntent.AppendExecutionRequirements(nativeGoal, objective);
            string permissionProfile = CodingPermissionProfile.Normalize(SettingsStore.Current.Data.CodingPermissionProfile);

            AgentTaskDto? task = null;
            try
            {
                task = await AgentOrchestratorClient.Current.CreateCodingTaskAsync(
                    nativeGoal,
                    repository,
                    permissionProfile,
                    maxRepairAttempts: 2,
                    CancellationToken.None,
                    codingRunPermissionMode: CodingRunPermissionModes.Normalize(codingRunPermissionMode)).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(activeProjectId))
                {
                    await ProjectManagerClient.Current.AttachTaskAsync(
                        activeProjectId, task.Id, CancellationToken.None).ConfigureAwait(false);
                }

                await AgentOrchestratorClient.Current.AppendLogAsync(task.Id,
                    "Native coding handoff created from Chat through the unified Core coding boundary"
                    + (string.IsNullOrWhiteSpace(activeProjectId)
                        ? "."
                        : " and attached to active Project " + activeProjectId + ".")
                    + " Coding permission profile=" + permissionProfile
                    + "; grants=" + string.Join(",", task.GrantedCapabilities) + "."
                    + " VS Code is only an optional review surface for the same PaRuX worktree.",
                    CancellationToken.None).ConfigureAwait(false);
                await AgentOrchestratorClient.Current.ScheduleAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                if (task != null)
                {
                    try { await AgentOrchestratorClient.Current.CancelAsync(task.Id, CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                }
                throw;
            }

            string requestedRepository = ShadowWorkspaceService.TryGetOriginalPath(repository) ?? repository;
            return new CodeHandoffResult
            {
                RepositoryPath = requestedRepository,
                WorkspacePath = requestedRepository,
                HandoffPath = string.Empty,
                TaskId = task.Id,
                NativeAgentStarted = true,
                VsCodeStarted = false,
                OpenCodeStarted = false
            };
        }

        private static async Task<(bool Cancelled, string Repository)> ResolveRepositoryAsync(
            string? activeProjectId,
            string objective,
            string? explicitWorkspace)
        {
            bool preserveGit = CodingTaskIntent.PreserveOriginalGit(objective);
            if (!string.IsNullOrWhiteSpace(explicitWorkspace))
            {
                if (!Directory.Exists(explicitWorkspace))
                    throw new InvalidOperationException("The coding workspace named in the request does not exist: " + explicitWorkspace);
                return (false, await EnsureRepositoryAsync(explicitWorkspace, preserveGit).ConfigureAwait(false));
            }

            string existing = ProjectWorkspaceSession.Current.RepositoryRoot?.Trim() ?? string.Empty;
            if (existing.Length > 0)
            {
                if (!Directory.Exists(existing))
                    throw new InvalidOperationException(
                        "The selected repository no longer exists. Choose a new code folder beside the chat composer.");
                return (false, await EnsureRepositoryAsync(existing, preserveGit).ConfigureAwait(false));
            }

            // The repository selected in Settings > Agents is a one-time choice for Chat too.
            // Active Projects still require their own explicit repository assignment.
            if (string.IsNullOrWhiteSpace(activeProjectId))
            {
                string configured = SettingsStore.Current.Data.AgentCodingWorkspaceRoot?.Trim() ?? string.Empty;
                if (configured.Length > 0)
                    return (false, await EnsureRepositoryAsync(configured, preserveGit).ConfigureAwait(false));
            }

            // A first coding task starts from chat without choosing a folder, in an isolated sandbox workspace by default.
            string defaultSandbox = CodingTaskSandboxWorkspacePolicy.EnsureSandboxWorkspaceCreated();
            return (false, await EnsureRepositoryAsync(defaultSandbox, forceShadow: true).ConfigureAwait(false));


        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private struct BROWSEINFO
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public string pszDisplayName;
            public string lpszTitle;
            public uint ulFlags;
            public IntPtr lpfn;
            public IntPtr lParam;
            public int iImage;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

        [System.Runtime.InteropServices.DllImport("ole32.dll")]
        private static extern int OleInitialize(IntPtr pvReserved);

        [System.Runtime.InteropServices.DllImport("ole32.dll")]
        private static extern void OleUninitialize();

        [System.Runtime.InteropServices.DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        private static string? ShowWin32BrowseFolderDialog(IntPtr ownerHwnd)
        {
            string? result = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    OleInitialize(IntPtr.Zero);
                    try
                    {
                        var bi = new BROWSEINFO
                        {
                            hwndOwner = (ownerHwnd != IntPtr.Zero && IsWindow(ownerHwnd)) ? ownerHwnd : IntPtr.Zero,
                            lpszTitle = "Choose code folder",
                            ulFlags = 0x00000040 /* BIF_NEWDIALOGSTYLE */ | 0x00000001 /* BIF_RETURNONLYFSDIRS */
                        };
                        IntPtr pidl = SHBrowseForFolder(ref bi);
                        if (pidl != IntPtr.Zero)
                        {
                            try
                            {
                                var sb = new StringBuilder(260);
                                if (SHGetPathFromIDList(pidl, sb))
                                {
                                    result = sb.ToString();
                                }
                            }
                            finally
                            {
                                CoTaskMemFree(pidl);
                            }
                        }
                    }
                    finally
                    {
                        OleUninitialize();
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Win32 folder picker exception: " + ex);
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            return result;
        }

        public static async Task<string?> ChooseRepositoryAsync(string? activeProjectId)
        {
            IntPtr hwnd = IntPtr.Zero;
            if (App.MainWin != null)
            {
                try { hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(App.MainWin.AppWindow.Id); }
                catch { }
                if (hwnd == IntPtr.Zero)
                {
                    try { hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin); }
                    catch (Exception ex) { Log.Write("GetWindowHandle failed: " + ex); }
                }
            }
            if (hwnd == IntPtr.Zero)
            {
                hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            }
            if (hwnd != IntPtr.Zero)
            {
                IntPtr root = GetAncestor(hwnd, 3 /* GA_ROOTOWNER */);
                if (root != IntPtr.Zero) hwnd = root;
            }

            string? chosenPath = null;
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                StorageFolder? selected = await picker.PickSingleFolderAsync();
                if (selected != null) chosenPath = selected.Path;
            }
            catch (Exception ex)
            {
                Log.Write("WinRT FolderPicker unavailable, using Win32 folder dialog: " + ex.Message);
                chosenPath = await Task.Run(() => ShowWin32BrowseFolderDialog(hwnd)).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(chosenPath)) return null;

            // The repository PaRuX will actually work on, not the raw picked folder: picking a
            // subfolder used to store the subfolder, and picking <repo>\.git\hooks (live test)
            // stored the git metadata folder itself as the coding workspace.
            string chosen = await EnsureRepositoryAsync(chosenPath, forceShadow: false).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(activeProjectId)
                && string.Equals(ProjectWorkspaceSession.Current.ProjectId, activeProjectId, StringComparison.Ordinal))
                ProjectWorkspaceSession.Current.SetRepositoryRoot(chosen);
            else if (string.IsNullOrWhiteSpace(activeProjectId))
            {
                SettingsStore.Current.Data.AgentCodingWorkspaceRoot = chosen;
                SettingsStore.Current.Save();
            }
            return chosen;
        }

        public static async Task<CodeHandoffCheckResult> CheckResultAsync(string? workspacePath)
        {
            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
                return new CodeHandoffCheckResult { Error = "The task workspace folder no longer exists." };

            (int exit, string outp, string err) probe = await RunGitAsync(workspacePath, "rev-parse --is-inside-work-tree");
            if (probe.exit != 0)
            {
                return new CodeHandoffCheckResult
                {
                    IsGitRepository = false,
                    Summary = "This workspace isn't a git repository, so PaRuX can't summarize what changed.",
                    Error = probe.err.Trim()
                };
            }

            (int exit, string outp, string err) status = await RunGitAsync(workspacePath, "status --porcelain");
            (int exit, string outp, string err) diffStat = await RunGitAsync(workspacePath, "diff --stat=4096,4000 HEAD"); // never abbreviate paths: the review list feeds apply
            (int exit, string outp, string err) untracked = await RunGitAsync(workspacePath,
                "ls-files --others --exclude-standard -z");

            if (status.exit != 0 || diffStat.exit != 0 || untracked.exit != 0)
                return new CodeHandoffCheckResult
                {
                    IsGitRepository = true,
                    Error = "Could not enumerate every changed file for review. Apply is unavailable until Git inspection succeeds."
                };

            string[] newFiles = untracked.outp.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            string newFileSummary = newFiles.Length == 0 ? string.Empty
                : "New untracked files to apply (" + newFiles.Length + "):\n"
                    + string.Join("\n", newFiles.Select(path => "- " + path
                        .Replace("\r", "\\r", StringComparison.Ordinal)
                        .Replace("\n", "\\n", StringComparison.Ordinal)))
                    + "\nReview their contents in the task worktree before confirming.";
            string reviewSummary = string.Join("\n\n", new[] { diffStat.outp.Trim(), newFileSummary }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            if (reviewSummary.Length > 4_000)
                return new CodeHandoffCheckResult
                {
                    IsGitRepository = true,
                    Error = "The changed-file summary is too long to show completely. Apply is unavailable until the task worktree is narrowed for review."
                };

            bool hasChanges = !string.IsNullOrWhiteSpace(status.outp);
            int changedFiles = hasChanges
                ? status.outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
                : 0;

            return new CodeHandoffCheckResult
            {
                IsGitRepository = true,
                HasChanges = hasChanges,
                Summary = hasChanges
                    ? changedFiles + (changedFiles == 1 ? " file changed" : " files changed") + " in the PaRuX task worktree."
                    : "No uncommitted changes yet in the PaRuX task worktree.",
                DiffStat = reviewSummary
            };
        }

        private static async Task<string> EnsureRepositoryAsync(string selectedPath, bool forceShadow)
        {
            if (string.IsNullOrWhiteSpace(selectedPath) || !Directory.Exists(selectedPath))
                throw new InvalidOperationException("The selected coding workspace does not exist.");

            string selected = Path.GetFullPath(CodingWorkspacePath.Normalize(selectedPath));
            if (forceShadow)
                return await ShadowWorkspaceService.EnsureShadowRepositoryAsync(selected).ConfigureAwait(false);

            (int exit, string outp, string err) probe = await RunGitAsync(selected, "rev-parse --show-toplevel").ConfigureAwait(false);
            if (probe.exit == 0 && !string.IsNullOrWhiteSpace(probe.outp))
                return Path.GetFullPath(probe.outp.Trim());

            return await ShadowWorkspaceService.EnsureShadowRepositoryAsync(selected).ConfigureAwait(false);
        }

        private static string NormalizeObjective(string? lastUserContent, string? conversationTitle)
        {
            string objective = (lastUserContent ?? string.Empty).Trim();
            if (objective.Equals("/code", StringComparison.OrdinalIgnoreCase)) objective = string.Empty;
            else if (objective.StartsWith("/code ", StringComparison.OrdinalIgnoreCase)
                || objective.StartsWith("/code\t", StringComparison.OrdinalIgnoreCase)
                || objective.StartsWith("/code\n", StringComparison.OrdinalIgnoreCase))
                objective = objective[5..].Trim();

            if (objective.Length == 0) objective = (conversationTitle ?? string.Empty).Trim();
            return objective.Length == 0 ? "Implement the requested project." : objective;
        }

        private static string BuildNativeGoal(string objective, Conversation conversation, bool includeConversationContext)
        {
            const int MaxContextCharacters = 4_000;
            var context = new StringBuilder();
            foreach (ChatMessage message in (includeConversationContext
                ? conversation.Messages
                .Where(message => !message.IsError && !string.Equals(message.ActionKind, "code-handoff", StringComparison.Ordinal))
                .SkipLast(1)
                .TakeLast(6)
                : Enumerable.Empty<ChatMessage>()))
            {
                string role = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "User" : "PaRuX";
                string content = (message.Content ?? string.Empty).Trim();
                if (content.Length == 0) continue;
                if (content.Length > 3000) content = content[..3000] + "…";
                context.Append("\n\n[").Append(role).Append("]\n").Append(content);
                if (context.Length >= MaxContextCharacters) break;
            }
            if (context.Length > MaxContextCharacters) context.Length = MaxContextCharacters;

            return objective.Trim()
                + "\n\nCHAT HANDOFF CONTEXT (supporting evidence; the objective and repository state are authoritative):"
                + context
                + "\n\nUse the existing repository conventions, preserve unrelated user changes, run the discovered verification after the final code mutation, and report concrete blockers rather than fabricating success.";
        }

        private static async Task<(int exit, string outp, string err)> RunGitAsync(string workingDirectory, string arguments)
        {
            // Agent worktrees are sandbox-writable: never run host Git there once its Git link was altered.
            if (!CodingAgentTools.IsTrustedGitWorkingDirectory(workingDirectory, out string untrusted))
                return (-1, string.Empty, "git refused: " + untrusted);
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = new Process { StartInfo = info };
                process.Start();
                Task<string> outTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(false);
                return (process.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
            }
            catch (Exception ex) { return (-1, string.Empty, ex.Message); }
        }

        private static string Trim(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max) + "\n… (truncated)";
        }
    }
}
