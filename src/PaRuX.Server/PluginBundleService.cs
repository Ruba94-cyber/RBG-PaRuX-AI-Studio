using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server.Automation;

namespace PaRuX.Server;

/// <summary>
/// Inspect/install boundary for .parux-plugin bundles. A bundle is declarative: JSON manifests only,
/// no installer executable or script is ever run. Skills and automations are installed disabled;
/// MCP definitions are staged as inert JSON so adding a plugin cannot launch an external process or
/// send data to a remote endpoint before a later explicit activation decision.
/// </summary>
public sealed class PluginBundleService
{
    internal const int MaximumEntries = 128;
    internal const long MaximumUncompressedBytes = 16L * 1024 * 1024;
    internal const int MaximumJsonBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _stateDirectory;
    private readonly SkillRegistryService _skills;
    private readonly AutomationStore _automations;

    private string PluginsRoot => Path.Combine(_stateDirectory, "plugins");
    private string TrustPath => Path.Combine(_stateDirectory, "plugin-publishers.json");

    private sealed class SignatureDocument
    {
        public string Algorithm { get; set; } = string.Empty;
        public string PublicKeyPem { get; set; } = string.Empty;
        public string SignatureBase64 { get; set; } = string.Empty;
    }

    private sealed class ParsedBundle : IDisposable
    {
        public required FileStream Stream { get; init; }
        public required ZipArchive Archive { get; init; }
        public required PaRuXPluginManifest Manifest { get; init; }
        public required Dictionary<string, byte[]> JsonFiles { get; init; }
        public required string BundleSha256 { get; init; }
        public string PublisherFingerprint { get; set; } = string.Empty;
        public bool SignaturePresent { get; set; }
        public bool SignatureValid { get; set; }
        public void Dispose() { Archive.Dispose(); Stream.Dispose(); }
    }

    public PluginBundleService(SkillRegistryService skills, AutomationStore automations)
        : this(ServerState.StateDirectory, skills, automations) { }

    public PluginBundleService(string stateDirectory, SkillRegistryService skills, AutomationStore automations)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _skills = skills;
        _automations = automations;
    }

    public PluginBundleInspection Inspect(string bundlePath)
    {
        try
        {
            using ParsedBundle parsed = Parse(bundlePath);
            return BuildInspection(parsed);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return new PluginBundleInspection { Valid = false, Error = ex.Message };
        }
    }

    public PluginInstallResult Install(PluginInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using ParsedBundle parsed = Parse(request.BundlePath);
        PluginBundleInspection inspection = BuildInspection(parsed);
        if (!inspection.Valid) throw new InvalidDataException(inspection.Error ?? "Plugin bundle is invalid.");
        if (!inspection.SignatureValid && !request.AllowUnsigned)
            throw new InvalidDataException("This plugin is not signed by a verifiable publisher. Inspect it and explicitly allow unsigned installation if you trust its source.");
        if (inspection.SignatureValid && !inspection.PublisherTrusted && !request.TrustPublisher)
            throw new InvalidDataException("The plugin signature is valid but this publisher fingerprint is not trusted yet. Review the permission diff and explicitly trust the publisher.");
        if (request.TrustPublisher && inspection.SignatureValid)
            TrustPublisher(inspection.PublisherKeyFingerprint, parsed.Manifest.Publisher);

        string pluginId = NormalizeId(parsed.Manifest.Id, "plugin id");
        string version = NormalizeVersion(parsed.Manifest.Version);
        string installDir = Path.Combine(PluginsRoot, pluginId, version);
        if (Directory.Exists(installDir))
            throw new InvalidDataException("Plugin " + pluginId + " version " + version + " is already installed.");

        var skillIds = new List<string>();
        var automationIds = new List<string>();
        try
        {
            Directory.CreateDirectory(installDir);
            File.WriteAllText(Path.Combine(installDir, "plugin.json"), JsonSerializer.Serialize(parsed.Manifest, JsonOptions));
            File.WriteAllText(Path.Combine(installDir, "inspection.json"), JsonSerializer.Serialize(inspection, JsonOptions));
            File.Copy(Path.GetFullPath(request.BundlePath), Path.Combine(installDir, pluginId + ".parux-plugin"), overwrite: false);

            foreach (string path in parsed.Manifest.SkillFiles)
            {
                SkillManifest skill = DeserializeComponent<SkillManifest>(parsed, path);
                if (!skill.Id.StartsWith(pluginId + ".", StringComparison.Ordinal))
                    throw new InvalidDataException("Plugin skill ids must be namespaced as '" + pluginId + ".*': " + skill.Id);
                skill.Enabled = false;
                SkillManifest saved = _skills.CreateOrUpdateUserSkill(skill, isImport: true);
                skillIds.Add(saved.Id);
            }

            foreach (string path in parsed.Manifest.AutomationFiles)
            {
                CreateAutomationRequest automation = DeserializeComponent<CreateAutomationRequest>(parsed, path);
                automation.Enabled = false;
                AutomationDto saved = _automations.Create(automation);
                automationIds.Add(saved.Id);
            }

            var stagedMcp = new List<AddMcpServerRequest>();
            foreach (string path in parsed.Manifest.McpFiles)
            {
                AddMcpServerRequest mcp = DeserializeComponent<AddMcpServerRequest>(parsed, path);
                if (!string.IsNullOrWhiteSpace(mcp.Credential))
                    throw new InvalidDataException("Plugin bundles may not contain MCP credentials or OAuth secrets.");
                mcp.Enabled = false;
                stagedMcp.Add(mcp);
            }
            File.WriteAllText(Path.Combine(installDir, "staged-mcp.json"), JsonSerializer.Serialize(stagedMcp, JsonOptions));

            return new PluginInstallResult
            {
                PluginId = pluginId,
                Version = version,
                Installed = true,
                SkillIds = skillIds,
                AutomationIds = automationIds,
                StagedMcpDefinitions = stagedMcp.Count,
                InstallDirectory = installDir,
                Message = "Installed declarative components disabled. Review/enable skills and automations explicitly; MCP definitions remain staged and inert until explicitly added from Tools & Automation."
            };
        }
        catch
        {
            foreach (string id in skillIds) try { _skills.DeleteUserSkill(id); } catch { }
            foreach (string id in automationIds) try { _automations.Delete(id); } catch { }
            try { if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true); } catch { }
            throw;
        }
    }

    public IReadOnlyList<PluginPublisherTrustDto> ListTrustedPublishers()
    {
        lock (JsonOptions)
        {
            try
            {
                if (!File.Exists(TrustPath)) return Array.Empty<PluginPublisherTrustDto>();
                return JsonSerializer.Deserialize<List<PluginPublisherTrustDto>>(File.ReadAllText(TrustPath), JsonOptions)
                    ?? new List<PluginPublisherTrustDto>();
            }
            catch { return Array.Empty<PluginPublisherTrustDto>(); }
        }
    }

    public bool RevokePublisher(string fingerprint)
    {
        string normalized = NormalizeFingerprint(fingerprint);
        lock (JsonOptions)
        {
            List<PluginPublisherTrustDto> all = ListTrustedPublishers().ToList();
            int removed = all.RemoveAll(x => string.Equals(x.Fingerprint, normalized, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            SaveTrust(all);
            return true;
        }
    }

    private PluginBundleInspection BuildInspection(ParsedBundle parsed)
    {
        PluginPermissionDeclaration effective = DerivePermissions(parsed);
        List<string> diff = PermissionDiff(parsed.Manifest.Permissions, effective);
        bool trusted = parsed.PublisherFingerprint.Length > 0
            && ListTrustedPublishers().Any(x => string.Equals(x.Fingerprint, parsed.PublisherFingerprint, StringComparison.OrdinalIgnoreCase));
        var warnings = new List<string>();
        if (!parsed.SignaturePresent) warnings.Add("Bundle has no publisher signature.");
        else if (!parsed.SignatureValid) warnings.Add("Bundle publisher signature is invalid.");
        else if (!trusted) warnings.Add("Publisher signature is valid but fingerprint is not trusted on this PaRuX installation.");
        if (parsed.Manifest.McpFiles.Count > 0) warnings.Add("MCP definitions install staged/disabled; no external process or remote endpoint starts during plugin install.");
        if (diff.Count > 0) warnings.Add("Manifest permission declaration is narrower than its components and installation is refused.");
        return new PluginBundleInspection
        {
            Valid = diff.Count == 0,
            Error = diff.Count == 0 ? null : "Plugin component permissions exceed the manifest declaration.",
            BundleSha256 = parsed.BundleSha256,
            Manifest = parsed.Manifest,
            PublisherKeyFingerprint = parsed.PublisherFingerprint,
            SignaturePresent = parsed.SignaturePresent,
            SignatureValid = parsed.SignatureValid,
            PublisherTrusted = trusted,
            EffectivePermissions = effective,
            PermissionDiff = diff,
            SkillCount = parsed.Manifest.SkillFiles.Count,
            AutomationCount = parsed.Manifest.AutomationFiles.Count,
            McpCount = parsed.Manifest.McpFiles.Count,
            Warnings = warnings
        };
    }

    private ParsedBundle Parse(string bundlePath)
    {
        if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
            throw new InvalidDataException("Plugin bundle path does not exist.");
        string full = Path.GetFullPath(bundlePath);
        if (!full.EndsWith(".parux-plugin", StringComparison.OrdinalIgnoreCase)
            && !full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin bundle must use .parux-plugin (ZIP container)." );

        string bundleHash;
        using (FileStream hashStream = File.OpenRead(full))
            bundleHash = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();

        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZipArchive archive;
        try { archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch { stream.Dispose(); throw; }
        try
        {
            if (archive.Entries.Count is 0 or > MaximumEntries)
                throw new InvalidDataException("Plugin bundle entry count is outside the allowed range 1-" + MaximumEntries + ".");
            long total = 0;
            var jsonFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = NormalizeEntryPath(entry.FullName);
                if (name.EndsWith('/')) continue;
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Plugin bundles are declarative; non-JSON payload is not allowed: " + name);
                if (entry.Length < 0 || entry.Length > MaximumJsonBytes || total + entry.Length > MaximumUncompressedBytes)
                    throw new InvalidDataException("Plugin bundle exceeds bounded uncompressed JSON size.");
                total += entry.Length;
                using Stream source = entry.Open();
                using var memory = new MemoryStream();
                source.CopyTo(memory);
                jsonFiles[name] = memory.ToArray();
            }
            if (!jsonFiles.TryGetValue("plugin.json", out byte[]? manifestBytes))
                throw new InvalidDataException("Plugin bundle is missing root plugin.json.");
            PaRuXPluginManifest manifest = JsonSerializer.Deserialize<PaRuXPluginManifest>(manifestBytes, JsonOptions)
                ?? throw new InvalidDataException("plugin.json is empty.");
            ValidateManifest(manifest, jsonFiles);

            var parsed = new ParsedBundle
            {
                Stream = stream,
                Archive = archive,
                Manifest = manifest,
                JsonFiles = jsonFiles,
                BundleSha256 = bundleHash
            };
            VerifySignature(parsed);
            return parsed;
        }
        catch
        {
            archive.Dispose();
            stream.Dispose();
            throw;
        }
    }

    private static void ValidateManifest(PaRuXPluginManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        _ = NormalizeId(manifest.Id, "plugin id");
        _ = NormalizeVersion(manifest.Version);
        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || manifest.DisplayName.Length > 160)
            throw new InvalidDataException("Plugin displayName is required and must be <= 160 characters.");
        if (string.IsNullOrWhiteSpace(manifest.Publisher) || manifest.Publisher.Length > 160)
            throw new InvalidDataException("Plugin publisher is required and must be <= 160 characters.");
        if (manifest.Description.Length > 4_000 || manifest.License.Length > 200)
            throw new InvalidDataException("Plugin description/license exceeds allowed length.");
        var all = manifest.SkillFiles.Concat(manifest.AutomationFiles).Concat(manifest.McpFiles).ToList();
        if (all.Count > 64 || all.Distinct(StringComparer.Ordinal).Count() != all.Count)
            throw new InvalidDataException("Plugin component list is duplicated or exceeds 64 entries.");
        foreach (string raw in all)
        {
            string path = NormalizeEntryPath(raw);
            if (path is "plugin.json" or "signature.json") throw new InvalidDataException("Reserved manifest/signature files cannot be declared as components.");
            if (!files.ContainsKey(path)) throw new InvalidDataException("Declared plugin component is missing: " + path);
        }
    }

    private static PluginPermissionDeclaration DerivePermissions(ParsedBundle parsed)
    {
        var result = new PluginPermissionDeclaration();
        foreach (string path in parsed.Manifest.SkillFiles)
        {
            SkillManifest skill = DeserializeComponent<SkillManifest>(parsed, path);
            SkillPermissionPolicy.Validate(skill);
            result.ReadFiles |= skill.Permissions.ReadFiles;
            result.WriteFiles |= skill.Permissions.WriteFiles;
            result.DeleteFiles |= skill.Permissions.DeleteFiles;
            result.Shell |= skill.Permissions.Shell;
            result.Network |= skill.Permissions.Network || string.Equals(skill.InternetPolicy, "on", StringComparison.OrdinalIgnoreCase);
            result.Browser |= skill.Permissions.Browser;
            result.ComputerUse |= skill.Permissions.ComputerUse;
            result.ExternalProcesses |= skill.Permissions.ExternalProcesses;
        }
        foreach (string path in parsed.Manifest.AutomationFiles)
        {
            CreateAutomationRequest automation = DeserializeComponent<CreateAutomationRequest>(parsed, path);
            automation.Enabled = false;
            AutomationDefinitionValidator.Validate(automation);
            result.Automations = true;
            if (automation.TriggerType is AutomationTriggerType.FileCreated or AutomationTriggerType.GitCommit)
                result.ReadFiles = true;
        }
        foreach (string path in parsed.Manifest.McpFiles)
        {
            AddMcpServerRequest mcp = DeserializeComponent<AddMcpServerRequest>(parsed, path);
            if (!string.IsNullOrWhiteSpace(mcp.Credential))
                throw new InvalidDataException("MCP component " + path + " contains a credential; plugin bundles may not package secrets.");
            if (string.Equals(mcp.Transport, McpTransports.Stdio, StringComparison.OrdinalIgnoreCase))
                result.ExternalProcesses = true;
            else
            {
                result.Network = true;
                result.RemoteMcp = true;
            }
        }
        return result;
    }

    private static List<string> PermissionDiff(PluginPermissionDeclaration declared, PluginPermissionDeclaration actual)
    {
        var diff = new List<string>();
        void Require(bool used, bool allowed, string name) { if (used && !allowed) diff.Add(name); }
        Require(actual.ReadFiles, declared.ReadFiles, "readFiles");
        Require(actual.WriteFiles, declared.WriteFiles, "writeFiles");
        Require(actual.DeleteFiles, declared.DeleteFiles, "deleteFiles");
        Require(actual.Shell, declared.Shell, "shell");
        Require(actual.Network, declared.Network, "network");
        Require(actual.Browser, declared.Browser, "browser");
        Require(actual.ComputerUse, declared.ComputerUse, "computerUse");
        Require(actual.ExternalProcesses, declared.ExternalProcesses, "externalProcesses");
        Require(actual.Automations, declared.Automations, "automations");
        Require(actual.RemoteMcp, declared.RemoteMcp, "remoteMcp");
        return diff;
    }

    private static T DeserializeComponent<T>(ParsedBundle parsed, string path)
    {
        string normalized = NormalizeEntryPath(path);
        if (!parsed.JsonFiles.TryGetValue(normalized, out byte[]? bytes))
            throw new InvalidDataException("Plugin component is missing: " + normalized);
        try { return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("Plugin component is empty: " + normalized); }
        catch (JsonException ex) { throw new InvalidDataException("Plugin component JSON is invalid: " + normalized, ex); }
    }

    private static void VerifySignature(ParsedBundle parsed)
    {
        if (!parsed.JsonFiles.TryGetValue("signature.json", out byte[]? bytes)) return;
        parsed.SignaturePresent = true;
        SignatureDocument signature = JsonSerializer.Deserialize<SignatureDocument>(bytes, JsonOptions)
            ?? throw new InvalidDataException("signature.json is empty.");
        if (string.IsNullOrWhiteSpace(signature.PublicKeyPem) || string.IsNullOrWhiteSpace(signature.SignatureBase64)) return;
        byte[] signedHash = ComputeDeclarativeHash(parsed.JsonFiles);
        byte[] rawSignature;
        try { rawSignature = Convert.FromBase64String(signature.SignatureBase64); }
        catch (FormatException) { return; }

        string algorithm = signature.Algorithm.Trim().ToUpperInvariant();
        if (algorithm == "ECDSA-SHA256")
        {
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(signature.PublicKeyPem);
            parsed.PublisherFingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
            parsed.SignatureValid = key.VerifyHash(signedHash, rawSignature);
        }
        else if (algorithm == "RSA-SHA256")
        {
            using RSA key = RSA.Create();
            key.ImportFromPem(signature.PublicKeyPem);
            parsed.PublisherFingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
            parsed.SignatureValid = key.VerifyHash(signedHash, rawSignature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
    }

    /// <summary>Stable signature payload: SHA256(path UTF8 + NUL + bytes + NUL) for every JSON entry
    /// except signature.json, sorted ordinal. ZIP timestamps/order/compression cannot change trust.</summary>
    internal static byte[] ComputeDeclarativeHash(IReadOnlyDictionary<string, byte[]> files)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string path, byte[] data) in files.Where(x => x.Key != "signature.json").OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            incremental.AppendData(Encoding.UTF8.GetBytes(path));
            incremental.AppendData(new byte[] { 0 });
            incremental.AppendData(data);
            incremental.AppendData(new byte[] { 0 });
        }
        return incremental.GetHashAndReset();
    }

    private void TrustPublisher(string fingerprint, string publisher)
    {
        string fp = NormalizeFingerprint(fingerprint);
        lock (JsonOptions)
        {
            List<PluginPublisherTrustDto> all = ListTrustedPublishers().ToList();
            if (!all.Any(x => string.Equals(x.Fingerprint, fp, StringComparison.OrdinalIgnoreCase)))
                all.Add(new PluginPublisherTrustDto { Fingerprint = fp, Publisher = publisher, TrustedUtc = DateTime.UtcNow });
            SaveTrust(all);
        }
    }

    private void SaveTrust(List<PluginPublisherTrustDto> all)
    {
        Directory.CreateDirectory(_stateDirectory);
        string temp = TrustPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(all.OrderBy(x => x.Publisher), JsonOptions));
        File.Move(temp, TrustPath, overwrite: true);
    }

    private static string NormalizeEntryPath(string raw)
    {
        string path = (raw ?? string.Empty).Replace('\\', '/').TrimStart('/');
        if (path.Length == 0 || path.Length > 240 || path.Contains("../", StringComparison.Ordinal) || path == ".." || path.Contains(':'))
            throw new InvalidDataException("Unsafe plugin entry path: " + raw);
        return path;
    }

    private static string NormalizeId(string raw, string label)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length is < 3 or > 96 || value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new InvalidDataException(label + " must be 3-96 characters using letters, digits, -, _ or .");
        return value;
    }

    private static string NormalizeVersion(string raw)
    {
        string value = (raw ?? string.Empty).Trim();
        if (value.Length is < 1 or > 64 || value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '+')))
            throw new InvalidDataException("Plugin version is invalid.");
        return value;
    }

    private static string NormalizeFingerprint(string raw)
    {
        string value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length != 64 || value.Any(ch => !Uri.IsHexDigit(ch))) throw new InvalidDataException("Publisher fingerprint must be a SHA-256 hex value.");
        return value;
    }
}
