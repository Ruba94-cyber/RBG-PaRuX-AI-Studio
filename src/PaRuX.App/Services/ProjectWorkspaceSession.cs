using System;
using System.IO;

namespace PaRuX.Services
{
    /// <summary>
    /// Process-local selection of the currently active durable Project plus the explicit desktop
    /// repository chosen for filesystem-capable tasks in that Project. Project identity/tasks stay
    /// authoritative in Core; repository authority is persisted per task by the desktop-only
    /// workspace endpoint, so this session value is only a safe assignment source, not a second DB.
    ///
    /// Switching projects clears RepositoryRoot deliberately: a path selected for Project A must
    /// never be silently reused as authority for Project B. Already-assigned task authorities remain
    /// durable in Core and are not affected by clearing this process-local selection.
    /// </summary>
    public sealed class ProjectWorkspaceSession
    {
        private const string PromptMarkerPrefix = "[[PARUX_PROJECT_SCOPE:";
        private const string PromptMarkerSuffix = "]]";

        public static ProjectWorkspaceSession Current { get; } = new ProjectWorkspaceSession();
        private ProjectWorkspaceSession() { }

        public string? ProjectId { get; private set; }
        public string? Goal { get; private set; }
        public string? RepositoryRoot { get; private set; }
        public event EventHandler? Changed;

        public void Select(string projectId, string goal)
        {
            string? nextId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
            bool projectChanged = !string.Equals(ProjectId, nextId, StringComparison.Ordinal);
            ProjectId = nextId;
            Goal = ProjectId == null ? null : goal;
            if (projectChanged) RepositoryRoot = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetRepositoryRoot(string? repositoryRoot)
        {
            string? normalized = null;
            if (!string.IsNullOrWhiteSpace(repositoryRoot))
            {
                string full = Path.GetFullPath(repositoryRoot.Trim());
                if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Project repository does not exist: " + full);
                normalized = full;
            }

            if (string.Equals(RepositoryRoot, normalized, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
            RepositoryRoot = normalized;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            if (ProjectId == null && RepositoryRoot == null) return;
            ProjectId = null;
            Goal = null;
            RepositoryRoot = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        internal string? AppendPromptScopeMarker(string? prompt)
        {
            if (string.IsNullOrWhiteSpace(ProjectId)) return prompt;
            string prefix = string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt.TrimEnd() + "\n\n";
            return prefix + PromptMarkerPrefix + ProjectId + PromptMarkerSuffix;
        }

        internal static bool TryStripPromptScopeMarker(string? prompt, out string? cleanPrompt, out string? projectId)
        {
            cleanPrompt = prompt;
            projectId = null;
            if (string.IsNullOrEmpty(prompt)) return false;

            int start = prompt.IndexOf(PromptMarkerPrefix, StringComparison.Ordinal);
            if (start < 0) return false;

            if (prompt.IndexOf(PromptMarkerPrefix, start + PromptMarkerPrefix.Length, StringComparison.Ordinal) >= 0)
                return false;

            int valueStart = start + PromptMarkerPrefix.Length;
            int end = prompt.IndexOf(PromptMarkerSuffix, valueStart, StringComparison.Ordinal);
            if (end < 0 || end + PromptMarkerSuffix.Length != prompt.Length) return false;

            string value = prompt[valueStart..end].Trim();
            if (value.Length == 0 || value.Length > 128
                || value.IndexOfAny(new[] { '[', ']', '\r', '\n', '\t', ' ' }) >= 0)
                return false;

            projectId = value;
            cleanPrompt = prompt[..start].TrimEnd();
            return true;
        }
    }
}
