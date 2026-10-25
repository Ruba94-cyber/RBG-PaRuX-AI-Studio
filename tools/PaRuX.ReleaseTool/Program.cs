using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.ReleaseTool;

internal static class Program
{
    private const string Repository = "Ruba94-cyber/RBG-PaRuX-AI-Studio";
    private const string PasswordVariable = "PARUX_RELEASE_KEY_PASSWORD";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "keygen" => Keygen(ParseOptions(args.Skip(1).ToArray())),
                "sign" => Sign(ParseOptions(args.Skip(1).ToArray())),
                _ => throw new ArgumentException("Unknown command '" + args[0] + "'.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static int Keygen(Dictionary<string, string> options)
    {
        string privatePath = Path.GetFullPath(Require(options, "private"));
        string publicPath = Path.GetFullPath(Require(options, "public"));
        bool overwrite = options.ContainsKey("overwrite");
        string password = RequirePassword();

        RefusePrivateKeyInsideRepository(privatePath);
        if (!overwrite && (File.Exists(privatePath) || File.Exists(publicPath)))
            throw new IOException("Refusing to overwrite an existing release key. Pass --overwrite only if you intentionally rotate the trust anchor.");
        if (string.Equals(privatePath, publicPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Private and public key paths must be different.");

        Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);

        using RSA rsa = RSA.Create(3072);
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200_000);
        string privatePem = rsa.ExportEncryptedPkcs8PrivateKeyPem(password, pbe);
        string publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        File.WriteAllText(privatePath, privatePem, new UTF8Encoding(false));
        File.WriteAllText(publicPath, publicPem, new UTF8Encoding(false));

        Console.WriteLine("PaRuX release-manifest key pair created.");
        Console.WriteLine("PRIVATE (AES-256 encrypted PKCS#8): " + privatePath);
        Console.WriteLine("PUBLIC:                           " + publicPath);
        Console.WriteLine();
        Console.WriteLine("Keep the private PEM outside the repository and retain the password separately.");
        Console.WriteLine("Only the public key is embedded in ReleaseTrustAnchor.cs.");
        Console.WriteLine();
        Console.WriteLine("C# trust-anchor value:");
        Console.WriteLine(ToCSharpString(publicPem));
        return 0;
    }

    private static int Sign(Dictionary<string, string> options)
    {
        string msiPath = Path.GetFullPath(Require(options, "msi"));
        string version = NormalizeVersion(Require(options, "version"));
        string privateKeyPath = Path.GetFullPath(Require(options, "private"));
        string sourceCommit = ResolveSourceCommit(options);
        string? outputArg = Get(options, "output");
        string outputPath = Path.GetFullPath(outputArg ?? Path.Combine(
            Path.GetDirectoryName(msiPath)!, "PaRuX-" + version + "-win-x64.update.json"));
        string? provenanceArg = Get(options, "provenance");
        string provenancePath = Path.GetFullPath(provenanceArg ?? Path.Combine(
            Path.GetDirectoryName(outputPath)!, "PaRuX-" + version + "-release-provenance.json"));
        string password = RequirePassword();
        string? payloadDirArg = Get(options, "payload-dir");

        RefusePrivateKeyInsideRepository(privateKeyPath);
        if (!File.Exists(msiPath)) throw new FileNotFoundException("MSI not found.", msiPath);
        if (!File.Exists(privateKeyPath)) throw new FileNotFoundException("Release private key not found.", privateKeyPath);
        string expectedName = "PaRuX-" + version + "-win-x64.msi";
        if (!string.Equals(Path.GetFileName(msiPath), expectedName, StringComparison.Ordinal))
            throw new InvalidDataException("MSI filename must be exactly " + expectedName + ".");
        if (!string.Equals(Path.GetExtension(msiPath), ".msi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release payload must be an MSI.");

        FileInfo msiInfo = new(msiPath);
        string sha256 = HashFile(msiPath);

        var manifest = new SignedAppUpdateManifest
        {
            SchemaVersion = 2,
            Version = version,
            Architecture = "win-x64",
            SourceCommit = sourceCommit,
            PackageUrl = "https://github.com/" + Repository + "/releases/download/v" + version + "/" + expectedName,
            PackageSha256 = sha256,
            PackageSizeBytes = msiInfo.Length,
            IssuedUtc = DateTime.UtcNow,
            ReleaseNotesUrl = "https://github.com/" + Repository + "/releases/tag/v" + version
        };

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        using RSA rsa = RSA.Create();
        rsa.ImportFromEncryptedPem(File.ReadAllText(privateKeyPath), password);
        byte[] signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("Generated release signature failed immediate self-verification.");
        var envelope = new SignedAppUpdateEnvelope
        {
            PayloadBase64 = Convert.ToBase64String(payload),
            SignatureBase64 = Convert.ToBase64String(signature)
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string envelopeJson = JsonSerializer.Serialize(envelope, JsonOptions);
        File.WriteAllText(outputPath, envelopeJson, new UTF8Encoding(false));

        FileInfo envelopeInfo = new(outputPath);
        var provenance = new ReleaseProvenance
        {
            SchemaVersion = 1,
            Repository = Repository,
            Version = version,
            SourceCommit = sourceCommit,
            GeneratedUtc = DateTime.UtcNow,
            SigningVerification = "rsa-sha256-self-verified",
            AndroidArtifactStatus = "separate-signed-publication",
            // ResolveSourceCommit has already refused a dirty tree, so reaching this line in a
            // repository means the tree was clean; without a repository it cannot be asserted.
            BuildMode = "release",
            SourceTreeClean = FindRepositoryRoot() != null,
            PayloadFiles = payloadDirArg == null ? new List<PayloadFileProvenance>() : HashPayloadDirectory(Path.GetFullPath(payloadDirArg)),
            Artifacts = new List<ArtifactProvenance>
            {
                new()
                {
                    Kind = "windows-msi",
                    FileName = expectedName,
                    SizeBytes = msiInfo.Length,
                    Sha256 = sha256
                },
                new()
                {
                    Kind = "signed-update-envelope",
                    FileName = Path.GetFileName(outputPath),
                    SizeBytes = envelopeInfo.Length,
                    Sha256 = HashFile(outputPath)
                }
            }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(provenancePath)!);
        File.WriteAllText(provenancePath, JsonSerializer.Serialize(provenance, JsonOptions), new UTF8Encoding(false));

        Console.WriteLine("Signed PaRuX MSI update envelope created.");
        Console.WriteLine("MSI:           " + msiPath);
        Console.WriteLine("ENVELOPE:      " + outputPath);
        Console.WriteLine("PROVENANCE:    " + provenancePath);
        Console.WriteLine("VERSION:       " + version);
        Console.WriteLine("SOURCE COMMIT: " + sourceCommit);
        Console.WriteLine("SIZE:          " + msiInfo.Length + " bytes");
        Console.WriteLine("SHA-256:       " + sha256);
        Console.WriteLine();
        Console.WriteLine("Upload the exact MSI, signed envelope and provenance file to GitHub Release tag v" + version + ".");
        return 0;
    }

    // Hash every file of the published payload (the exact tree WiX harvests into the MSI) so a
    // verifier can compare it with both the publish directory and the MSI's administrative image.
    private static List<PayloadFileProvenance> HashPayloadDirectory(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Payload directory not found: " + directory);
        var result = new List<PayloadFileProvenance>();
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            result.Add(new PayloadFileProvenance
            {
                Path = Path.GetRelativePath(directory, file).Replace('\\', '/'),
                SizeBytes = new FileInfo(file).Length,
                Sha256 = HashFile(file)
            });
        }
        if (result.Count == 0) throw new InvalidDataException("Payload directory is empty: " + directory);
        result.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return result;
    }

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ResolveSourceCommit(Dictionary<string, string> options)
    {
        string? explicitCommit = Get(options, "source-commit")?.Trim();
        string? repo = FindRepositoryRoot();
        string? head = repo == null ? null : RunGit(repo, "rev-parse", "HEAD")?.Trim().ToLowerInvariant();
        string selected = !string.IsNullOrWhiteSpace(explicitCommit)
            ? explicitCommit!.ToLowerInvariant()
            : head ?? throw new InvalidOperationException(
                "Source commit is required. Run the signer from the PaRuX repository or pass --source-commit <40-hex-commit>.");

        if (!IsCanonicalCommit(selected))
            throw new ArgumentException("Source commit must be exactly 40 lowercase hexadecimal characters.");

        if (repo != null)
        {
            if (!string.Equals(head, selected, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The declared source commit does not match the current repository HEAD. Sign from the exact checkout that produced the artifact.");
            string status = RunGit(repo, "status", "--porcelain=v1", "--untracked-files=all") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(status))
                throw new InvalidOperationException(
                    "The repository has tracked or untracked changes. Release provenance requires a clean source tree before signing.");
        }

        return selected;
    }

    private static bool IsCanonicalCommit(string value) =>
        value.Length == 40
        && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    private static string? RunGit(string repo, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repo);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git for release provenance verification.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("git " + string.Join(' ', arguments) + " failed: " + error.Trim());
        return output;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Unexpected argument '" + token + "'. Options must use --name value syntax.");
            string name = token[2..];
            if (name.Length == 0) throw new ArgumentException("Empty option name.");

            if (string.Equals(name, "overwrite", StringComparison.OrdinalIgnoreCase))
            {
                result[name] = "true";
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Option --" + name + " requires a value.");
            result[name] = args[++i];
        }
        return result;
    }

    private static string Require(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Missing required option --" + name + ".");

    private static string? Get(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) ? value : null;

    private static string RequirePassword()
    {
        string? password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrWhiteSpace(password) || password.Length < 16)
            throw new InvalidOperationException(
                PasswordVariable + " must be set to a strong password of at least 16 characters for release-key operations.");
        return password;
    }

    private static void RefusePrivateKeyInsideRepository(string privatePath)
    {
        string? repo = FindRepositoryRoot();
        if (repo == null) return;
        string root = Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(privatePath);
        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The release private key must be stored outside the PaRuX repository tree.");
    }

    private static string? FindRepositoryRoot()
    {
        DirectoryInfo? current = new(Directory.GetCurrentDirectory());
        while (current != null)
        {
            // A linked worktree has a .git FILE, not a directory; walking past it would resolve the
            // main checkout's (different) HEAD and sign/verify the wrong source tree.
            string gitEntry = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry)) return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static string NormalizeVersion(string raw)
    {
        string value = raw.Trim().TrimStart('v', 'V');
        string[] parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsDigit)))
            throw new ArgumentException("Version must be canonical major.minor.patch, for example 1.5.0.");
        if (!int.TryParse(parts[0], out int major)
            || !int.TryParse(parts[1], out int minor)
            || !int.TryParse(parts[2], out int patch))
            throw new ArgumentException("Version components are outside the supported integer range.");
        if (major > 255 || minor > 255 || patch > 65535)
            throw new ArgumentException("Windows Installer ProductVersion cannot exceed 255.255.65535.");
        string normalized = major + "." + minor + "." + patch;
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
            throw new ArgumentException("Version must be canonical major.minor.patch without leading zeroes.");
        return normalized;
    }

    private static string ToCSharpString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static bool IsHelp(string value) =>
        value is "-h" or "--help" or "help";

    private static void PrintUsage()
    {
        Console.WriteLine("PaRuX.ReleaseTool — local GitHub MSI release signing");
        Console.WriteLine();
        Console.WriteLine("Set a strong password for the encrypted private key in this shell:");
        Console.WriteLine("  $env:" + PasswordVariable + " = '<strong secret>'");
        Console.WriteLine();
        Console.WriteLine("Generate the detached-manifest RSA key pair (PRIVATE must be outside the repo):");
        Console.WriteLine("  dotnet run --project tools/PaRuX.ReleaseTool -- keygen --private <outside-repo/private.pem> --public <public.pem>");
        Console.WriteLine();
        Console.WriteLine("Sign one versioned MSI release manifest from a CLEAN repository checkout:");
        Console.WriteLine("  dotnet run --project tools/PaRuX.ReleaseTool -- sign --msi <PaRuX-x.y.z-win-x64.msi> --version x.y.z --private <outside-repo/private.pem> [--source-commit <40-hex-HEAD>] [--payload-dir <publish dir harvested into the MSI>] [--output <...update.json>] [--provenance <...release-provenance.json>]");
        Console.WriteLine();
        Console.WriteLine("The signer records and signs the exact source commit, emits hash-linked release provenance, and refuses a dirty checkout or mismatched explicit commit.");
        Console.WriteLine("The private key is never needed by the PaRuX application. Commit/embed only the public key.");
    }

    private sealed class SignedAppUpdateManifest
    {
        public int SchemaVersion { get; set; } = 2;
        public string Version { get; set; } = string.Empty;
        public string Architecture { get; set; } = "win-x64";
        public string SourceCommit { get; set; } = string.Empty;
        public string PackageUrl { get; set; } = string.Empty;
        public string PackageSha256 { get; set; } = string.Empty;
        public long PackageSizeBytes { get; set; }
        public DateTime IssuedUtc { get; set; }
        public string? ReleaseNotesUrl { get; set; }
    }

    private sealed class SignedAppUpdateEnvelope
    {
        public string PayloadBase64 { get; set; } = string.Empty;
        public string SignatureBase64 { get; set; } = string.Empty;
    }

    private sealed class ReleaseProvenance
    {
        public int SchemaVersion { get; set; }
        public string Repository { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string SourceCommit { get; set; } = string.Empty;
        public DateTime GeneratedUtc { get; set; }
        public string SigningVerification { get; set; } = string.Empty;
        public string AndroidArtifactStatus { get; set; } = string.Empty;
        public string BuildMode { get; set; } = string.Empty;
        public bool SourceTreeClean { get; set; }
        public List<PayloadFileProvenance> PayloadFiles { get; set; } = new();
        public List<ArtifactProvenance> Artifacts { get; set; } = new();
    }

    private sealed class PayloadFileProvenance
    {
        public string Path { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class ArtifactProvenance
    {
        public string Kind { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}