namespace PaRuX.Server;

/// <summary>
/// Desktop-owner-only Core endpoint payload for a bounded, non-executing
/// documentation search. Search results are external untrusted data.
/// </summary>
public sealed class CodingWebSearchRequest
{
    public string Query { get; set; } = string.Empty;
    public int MaxResults { get; set; } = 5;
}

public static class CodingWebSearchPolicy
{
    public const int MaximumQueryCharacters = 512;
    public const int MaximumResultCount = 5;

    public static bool IsValidQuery(string? query) =>
        !string.IsNullOrWhiteSpace(query)
        && query.Length <= MaximumQueryCharacters
        && !query.Any(char.IsControl);

    /// <summary>
    /// Source URLs are references, not trusted fetch targets. Never truncate
    /// an address (which would fabricate its identity), and never pass local
    /// endpoint references into a coding-agent evidence packet.
    /// </summary>
    public static bool IsSafeSourceUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2_048
            || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.UserInfo)) return false;

        string host = uri.Host.TrimEnd('.');
        return host.Length > 0
            && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            && !System.Net.IPAddress.TryParse(host, out _);
    }

    public static int ResultLimit(int requested) =>
        Math.Clamp(requested <= 0 ? MaximumResultCount : requested, 1, MaximumResultCount);
}
