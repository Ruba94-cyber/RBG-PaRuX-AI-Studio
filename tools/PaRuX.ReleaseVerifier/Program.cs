using PaRuX.Services;

namespace PaRuX.ReleaseVerifier;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            Dictionary<string, string> options = Parse(args);
            string msi = Path.GetFullPath(Require(options, "msi"));
            string envelope = Path.GetFullPath(Require(options, "envelope"));
            string version = Require(options, "version").Trim().TrimStart('v', 'V');
            string sourceCommit = Require(options, "source-commit").Trim().ToLowerInvariant();

            if (!File.Exists(msi)) throw new FileNotFoundException("MSI not found.", msi);
            if (!File.Exists(envelope)) throw new FileNotFoundException("Signed update envelope not found.", envelope);
            if (new FileInfo(envelope).Length <= 0 || new FileInfo(envelope).Length > SignedAppUpdateVerifier.MaximumEnvelopeBytes)
                throw new InvalidDataException("Signed update envelope size is outside the allowed bound.");

            string envelopeJson = File.ReadAllText(envelope);
            SignedAppUpdateVerification verification = SignedAppUpdateVerifier.Verify(
                envelopeJson, ReleaseTrustAnchor.PublicKeyPem);
            if (!verification.Valid || verification.Manifest == null)
                throw new InvalidDataException("Signed update envelope rejected: " + verification.Message);

            SignedAppUpdateManifest manifest = verification.Manifest;
            if (!string.Equals(manifest.Version, version, StringComparison.Ordinal))
                throw new InvalidDataException($"Envelope version {manifest.Version} does not match requested release {version}.");
            if (!string.Equals(manifest.SourceCommit, sourceCommit, StringComparison.Ordinal))
                throw new InvalidDataException($"Envelope source commit {manifest.SourceCommit} does not match requested source {sourceCommit}.");

            MsiAppUpdatePackage.VerifyPackageAsync(msi, manifest, CancellationToken.None)
                .GetAwaiter().GetResult();

            Console.WriteLine("PaRuX signed release verification: PASS");
            Console.WriteLine("Version:       " + manifest.Version);
            Console.WriteLine("Source commit: " + manifest.SourceCommit);
            Console.WriteLine("MSI SHA-256:   " + manifest.PackageSha256);
            Console.WriteLine("MSI size:      " + manifest.PackageSizeBytes);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PaRuX signed release verification: FAIL - " + ex.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
                throw new ArgumentException("Options must use --name value syntax.");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(token + " requires a value.");
            options[token[2..]] = args[++i];
        }
        return options;
    }

    private static string Require(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Missing required option --" + name + ".");
}