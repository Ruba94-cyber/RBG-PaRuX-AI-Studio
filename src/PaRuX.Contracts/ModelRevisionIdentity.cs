namespace PaRuX.Contracts;

/// <summary>Validation shared by the Model Hub client and Core for immutable repository identity.</summary>
public static class ModelRevisionIdentity
{
    public static bool IsValid(string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision)) return false;
        string value = revision.Trim();
        return value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
    }

    public static string? Normalize(string? revision) =>
        IsValid(revision) ? revision!.Trim().ToLowerInvariant() : null;
}
