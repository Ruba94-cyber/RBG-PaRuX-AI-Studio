using System.Security.Cryptography;

namespace PaRuX.Server;

/// <summary>
/// Stores the one optional Hugging Face access token that gated and private repositories require. The
/// token is protected with the same per-user secret protector as the other Core credentials (DPAPI on
/// Windows, owner-only key elsewhere) and lives in the state directory. It is never written to the
/// models directory, a URL, a log, a provenance record or a backup, and it has no read endpoint.
/// </summary>
internal static class ModelHubTokenStore
{
    private const string Purpose = "parux.model-hub.hf-token";
    private const string FileName = "model-hub-token.secret";

    /// <summary>Hugging Face tokens are ASCII letters, digits and a few separators.</summary>
    public static bool IsValidToken(string? token) =>
        !string.IsNullOrEmpty(token)
        && token.Length <= 512
        && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    public static bool Exists(string stateDirectory) => File.Exists(Path.Combine(stateDirectory, FileName));

    public static string Protection(string stateDirectory) => SecretProtection.DescribeFor(stateDirectory);

    public static void Save(string stateDirectory, string token)
    {
        if (!IsValidToken(token))
            throw new InvalidDataException("The access token format is not recognized. Paste the token exactly as Hugging Face shows it.");
        Directory.CreateDirectory(stateDirectory);
        string path = Path.Combine(stateDirectory, FileName);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, SecretProtection.For(stateDirectory).Protect(Purpose, token));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Null when no token is saved, or when the saved blob cannot be read by this account.</summary>
    public static string? Load(string stateDirectory)
    {
        try
        {
            string path = Path.Combine(stateDirectory, FileName);
            if (!File.Exists(path)) return null;
            return SecretProtection.For(stateDirectory).Unprotect(Purpose, File.ReadAllText(path).Trim());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    public static bool Clear(string stateDirectory)
    {
        string path = Path.Combine(stateDirectory, FileName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}
