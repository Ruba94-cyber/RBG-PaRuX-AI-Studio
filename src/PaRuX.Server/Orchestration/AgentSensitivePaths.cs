namespace PaRuX.Server.Orchestration;

/// <summary>
/// Files a server-side specialist (or continued read-only child) must never read or search, even
/// with repository.read: environment files, private keys, keystores and credential stores. The
/// model-visible output of a specialist is persisted as task evidence and handed to other agents,
/// so a secret read once would be copied around. Matching is by file or directory name only; it is
/// deliberately conservative (it may hide a harmless file, never expose a likely secret).
/// </summary>
public static class AgentSensitivePaths
{
    private static readonly HashSet<string> SensitiveFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".npmrc", ".pypirc", ".netrc", ".git-credentials", ".htpasswd", "credentials", "credentials.json",
        "secrets.json", "secrets.yml", "secrets.yaml", "appsettings.secrets.json", "id_rsa", "id_dsa", "id_ecdsa",
        "id_ed25519", "kubeconfig", "service-account.json"
    };

    private static readonly string[] SensitiveExtensions =
    {
        ".env", ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".ppk", ".kdbx", ".gpg", ".asc", ".tfstate"
    };

    private static readonly HashSet<string> SensitiveDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".aws", ".gnupg", ".azure", ".kube", ".docker", ".git"
    };

    private static readonly string[] TemplateSuffixes = { ".example", ".sample", ".template", ".dist", ".tmpl" };

    public static bool IsSensitiveFileName(string? fileName)
    {
        string name = (fileName ?? string.Empty).Trim();
        if (name.Length == 0) return false;
        if (TemplateSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))) return false;
        if (SensitiveFileNames.Contains(name)) return true;
        if (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("id_ed25519", StringComparison.OrdinalIgnoreCase)) return true;
        string extension = Path.GetExtension(name);
        return SensitiveExtensions.Any(value => string.Equals(value, extension, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSensitiveDirectoryName(string? directoryName) =>
        !string.IsNullOrWhiteSpace(directoryName) && SensitiveDirectories.Contains(directoryName.Trim());

    /// <summary>True when any segment of a root-relative path is a sensitive directory or the file is sensitive.</summary>
    public static bool IsSensitiveRelativePath(string? relativePath)
    {
        string[] segments = (relativePath ?? string.Empty).Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        // NTFS alternate data streams (".env::$DATA") name the same file as a sensitive one.
        if (segments.Any(segment => segment.Contains(':'))) return true;
        for (int i = 0; i < segments.Length - 1; i++)
            if (IsSensitiveDirectoryName(segments[i])) return true;
        return IsSensitiveFileName(segments[^1]) || IsSensitiveDirectoryName(segments[^1]);
    }

    /// <summary>Git pathspec exclusions so a whole-tree diff cannot echo a tracked secret.</summary>
    public static IReadOnlyList<string> GitExcludePathspecs { get; } = new[]
    {
        ":(exclude,glob)**/.env", ":(exclude,glob)**/*.env", ":(exclude,glob)**/.env.*", ":(exclude,glob)**/*.pem", ":(exclude,glob)**/*.key",
        ":(exclude,glob)**/*.pfx", ":(exclude,glob)**/*.p12", ":(exclude,glob)**/*.jks", ":(exclude,glob)**/*.keystore",
        ":(exclude,glob)**/*.ppk", ":(exclude,glob)**/*.kdbx", ":(exclude,glob)**/*.gpg", ":(exclude,glob)**/*.asc",
        ":(exclude,glob)**/*.tfstate", ":(exclude,glob)**/*.tfstate.*",
        ":(exclude,glob)**/.npmrc", ":(exclude,glob)**/.pypirc", ":(exclude,glob)**/.netrc",
        ":(exclude,glob)**/.git-credentials", ":(exclude,glob)**/.htpasswd",
        ":(exclude,glob)**/id_rsa*", ":(exclude,glob)**/id_dsa*", ":(exclude,glob)**/id_ecdsa*",
        ":(exclude,glob)**/id_ed25519*", ":(exclude,glob)**/kubeconfig", ":(exclude,glob)**/service-account.json",
        ":(exclude,glob)**/credentials", ":(exclude,glob)**/credentials.*", ":(exclude,glob)**/secrets.*",
        ":(exclude,glob)**/appsettings.secrets.json",
        ":(exclude,glob)**/.ssh/**", ":(exclude,glob)**/.aws/**", ":(exclude,glob)**/.gnupg/**",
        ":(exclude,glob)**/.azure/**", ":(exclude,glob)**/.kube/**", ":(exclude,glob)**/.docker/**"
    };
}
