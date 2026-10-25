using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal static class ModelDiscoveryIdentity
{
    internal const string Provider = "huggingface";
    internal const int AdapterVersion = 1;

    internal static bool IsSafeKey(string? key) => key is { Length: 64 }
        && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsSafeRepoId(string? repoId)
    {
        if (repoId is null || repoId.Length > 193) return false;
        string[] parts = repoId.Split('/');
        return parts.Length == 2 && parts.All(IsSafeRepoPart);
    }

    private static bool IsSafeRepoPart(string part) => part.Length is >= 1 and <= 96
        && char.IsAsciiLetterOrDigit(part[0]) && char.IsAsciiLetterOrDigit(part[^1])
        && !part.Contains("..", StringComparison.Ordinal)
        && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static bool IsSafeRepoFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Contains('\\')) return false;
        string[] parts = path.Split('/');
        return parts.Length is >= 1 and <= 16 && parts.All(part =>
            part.Length is >= 1 and <= 240 && part is not "." and not ".."
            && part == part.Trim() && part[^1] != '.'
            && part.All(c => !char.IsControl(c) && c is not ':' and not '<' and not '>'
                and not '"' and not '|' and not '?' and not '*' and not '#' and not '%'));
    }

    internal static string? NormalizeSha256(string? oid)
    {
        if (oid?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true) oid = oid[7..];
        return oid is { Length: 64 } && oid.All(Uri.IsHexDigit) ? oid.ToLowerInvariant() : null;
    }

    internal static string Key(string repoId, string revision, string path)
    {
        if (!IsSafeRepoId(repoId) || ModelRevisionIdentity.Normalize(revision) is not { } normalized
            || !IsSafeRepoFilePath(path)) throw new ArgumentException("Unsafe model discovery identity.");
        byte[] input = Encoding.UTF8.GetBytes(Provider + "\n" + repoId + "\n" + normalized + "\n" + path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    internal static string PinnedUrl(string repoId, string revision, string path)
    {
        _ = Key(repoId, revision, path);
        string escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return $"https://huggingface.co/{repoId}/resolve/{revision.ToLowerInvariant()}/{escaped}";
    }
}
