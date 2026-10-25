using System.Runtime.InteropServices;

namespace PaRuX.Server;

/// <summary>
/// Path rules that mean the same thing on every platform PaRuX Core runs on.
///
/// The reason this exists rather than each guard calling <see cref="Path"/> directly: .NET's path
/// API is platform-relative by design, and PaRuX's containment guards were written on Windows where
/// that happened to be harmless. Off Windows they quietly weaken:
///
/// <list type="bullet">
/// <item><c>Path.GetFileName("..\\..\\evil.gguf")</c> returns the whole string on Linux, because a
/// backslash is an ordinary filename character there - so a <c>GetFileName(x) == x</c> guard accepts
/// a payload it was written to reject, and an import would create a file literally named
/// <c>..\..\evil.gguf</c>.</item>
/// <item><c>Path.IsPathRooted("C:/Windows/win.ini")</c> is false on Linux, so a drive-qualified
/// path passes a "must be relative" check.</item>
/// <item><c>StartsWith(root, OrdinalIgnoreCase)</c> treats <c>/srv/models</c> and <c>/srv/Models</c>
/// as the same directory on a case-sensitive filesystem, where they are two different ones.</item>
/// </list>
///
/// These helpers therefore apply the union of the platforms' rules to names, and the current
/// platform's real comparison semantics to containment. Being stricter than the host requires costs
/// nothing: no legitimate PaRuX model file, dataset entry or watch filter contains a backslash or a
/// colon on any platform.
/// </summary>
internal static class PaRuXPathSafety
{
    /// <summary>
    /// True for a bare file name with no directory component under <em>any</em> platform's rules:
    /// no <c>/</c>, no <c>\</c>, not <c>.</c> or <c>..</c>, no colon (which would be a drive
    /// designator on Windows, or an NTFS alternate data stream), and no control characters.
    /// </summary>
    internal static bool IsSimpleFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 255) return false;
        if (name is "." or "..") return false;
        foreach (char c in name)
        {
            if (c is '/' or '\\' or ':' or '\0') return false;
            if (char.IsControl(c)) return false;
        }
        // Trailing dots and spaces are silently stripped by Windows, so "evil.gguf " and
        // "evil.gguf." would name the same file as "evil.gguf" there but not here. Rejecting them
        // keeps one name meaning one file on every host.
        return name[^1] is not ('.' or ' ');
    }

    /// <summary>True when a path segment is a Windows drive designator such as <c>C:</c>, which
    /// <see cref="Path.IsPathRooted"/> does not recognise off Windows.</summary>
    internal static bool LooksLikeWindowsDrive(string segment) =>
        segment.Length >= 2 && char.IsLetter(segment[0]) && segment[1] == ':';

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> itself or lies
    /// beneath it, compared the way this operating system actually compares paths.</summary>
    internal static bool IsWithin(string root, string candidate)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(candidate)) return false;

        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        StringComparison comparison = PathComparison;

        if (string.Equals(normalizedRoot, normalizedCandidate, comparison)) return true;
        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// How this OS compares two paths for identity. Linux filesystems are case-sensitive; Windows
    /// and the default macOS volume are not. Getting this wrong in either direction is a real
    /// defect - too loose lets a differently-cased path pass a containment check into a directory
    /// it is not in, too strict makes two spellings of the same file look like a substitution.
    /// </summary>
    internal static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    /// <summary>True when both strings name the same file on this OS.</summary>
    internal static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                PathComparison);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }
}
