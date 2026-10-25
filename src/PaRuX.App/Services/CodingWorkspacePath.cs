using System;
using System.IO;

namespace PaRuX.Services
{
    /// <summary>
    /// A folder inside a repository's .git directory (hooks, refs, objects) is git metadata, never
    /// a coding workspace: an agent editing .git\hooks would write scripts git runs on the next
    /// commit. Found in a live test where the code-folder picker accepted "&lt;repo&gt;\.git\hooks".
    /// Such a path resolves to the working tree that owns that .git; anything else is unchanged.
    /// </summary>
    public static class CodingWorkspacePath
    {
        public static string Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string trimmed = path.Trim();
            char[] separators = { '\\', '/' };
            string[] parts = trimmed.TrimEnd(separators).Split(separators);
            int gitIndex = Array.FindIndex(parts, p => string.Equals(p, ".git", StringComparison.OrdinalIgnoreCase));
            if (gitIndex <= 0) return trimmed;
            string root = string.Join(Path.DirectorySeparatorChar.ToString(), parts, 0, gitIndex);
            if (root.EndsWith(":", StringComparison.Ordinal)) root += Path.DirectorySeparatorChar;
            return root;
        }
    }
}
