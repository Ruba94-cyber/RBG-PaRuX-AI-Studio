using System;
using System.IO;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>
    /// Shadow workspaces copy Git changes into an ordinary non-Git folder.
    /// Never let a malicious path or existing junction/symlink turn that
    /// user-authorized folder into arbitrary filesystem write authority.
    /// This is a fail-closed preflight; an independent external process
    /// replacing directories DURING copy still requires OS-level fencing.
    /// </summary>
    public static class ShadowApplyPathPolicy
    {
        public static bool IsSafe(string? sourceRoot, string? destinationRoot,
            string? relative)
        {
            if (string.IsNullOrWhiteSpace(sourceRoot)
                || string.IsNullOrWhiteSpace(destinationRoot)
                || string.IsNullOrWhiteSpace(relative)
                || relative.Length > 4096
                || Path.IsPathRooted(relative)
                || relative.Contains(':')
                || relative.Any(char.IsControl))
                return false;
            try
            {
                string[] segments = relative.Replace('\\', '/').Split('/');
                if (segments.Length == 0 || segments.Any(segment =>
                    string.IsNullOrWhiteSpace(segment)
                    || segment is "." or ".."
                    || segment.EndsWith(" ", StringComparison.Ordinal)
                    || segment.EndsWith(".", StringComparison.Ordinal)
                    || PaRuX.Contracts.AgentRepositoryAliasValidation
                        .IsWindowsReservedDeviceName(segment)))
                    return false;

                string source = Path.GetFullPath(sourceRoot);
                string destination = Path.GetFullPath(destinationRoot);
                string sourcePath = Path.GetFullPath(Path.Combine(
                    source, Path.Combine(segments)));
                string destPath = Path.GetFullPath(Path.Combine(
                    destination, Path.Combine(segments)));
                StringComparison comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!sourcePath.StartsWith(
                    source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    comparison)
                    || !destPath.StartsWith(
                        destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        comparison))
                    return false;

                return !HasReparseSegment(source, segments)
                    && !HasReparseSegment(destination, segments);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException
                or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }

        private static bool HasReparseSegment(string root, string[] parts)
        {
            string current = Path.GetFullPath(root);
            if (!Directory.Exists(current) || IsReparse(current)) return true;
            foreach (string part in parts)
            {
                current = Path.Combine(current, part);
                if (IsReparse(current)) return true;
            }
            return false;
        }

        private static bool IsReparse(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            // Unknown errors must not become permission to follow a link.
        }
    }
}
