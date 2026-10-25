using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Automation;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class PluginBundleServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-plugin-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SkillRegistryService _skills;
    private readonly AutomationStore _automations;
    private readonly PluginBundleService _service;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public PluginBundleServiceTests()
    {
        Directory.CreateDirectory(_root);
        _skills = new SkillRegistryService(_root);
        _automations = new AutomationStore(_root);
        _service = new PluginBundleService(_root, _skills, _automations);
    }

    [Fact]
    public void Permission_mismatch_is_visible_and_blocks_installation()
    {
        string bundle = BuildBundle("perm-mismatch", includeSkill: true, declaredShell: false, skillShell: true);
        PluginBundleInspection inspection = _service.Inspect(bundle);

        Assert.False(inspection.Valid);
        Assert.Contains("shell", inspection.PermissionDiff);
        Assert.Throws<InvalidDataException>(() => _service.Install(new PluginInstallRequest
        {
            BundlePath = bundle,
            AllowUnsigned = true
        }));
    }

    [Fact]
    public void Unsigned_bundle_requires_explicit_allow_and_installs_components_disabled()
    {
        string bundle = BuildBundle("safe-plugin", includeSkill: true, includeAutomation: true, includeMcp: true,
            declaredAutomations: true, declaredExternalProcesses: true);

        PluginBundleInspection inspection = _service.Inspect(bundle);
        Assert.True(inspection.Valid);
        Assert.False(inspection.SignaturePresent);
        Assert.Throws<InvalidDataException>(() => _service.Install(new PluginInstallRequest { BundlePath = bundle }));

        PluginInstallResult result = _service.Install(new PluginInstallRequest
        {
            BundlePath = bundle,
            AllowUnsigned = true
        });

        Assert.True(result.Installed);
        Assert.Single(result.SkillIds);
        Assert.Single(result.AutomationIds);
        Assert.Equal(1, result.StagedMcpDefinitions);
        Assert.False(_skills.Get(result.SkillIds[0])!.Enabled);
        Assert.False(_automations.Get(result.AutomationIds[0])!.Enabled);
        string staged = File.ReadAllText(Path.Combine(result.InstallDirectory, "staged-mcp.json"));
        Assert.Contains("filesystem tool", staged, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Signed_bundle_requires_publisher_trust_then_is_remembered()
    {
        string unsigned = BuildBundle("signed-plugin", includeSkill: true);
        string signed = SignBundle(unsigned);
        PluginBundleInspection inspection = _service.Inspect(signed);

        Assert.True(inspection.Valid);
        Assert.True(inspection.SignaturePresent);
        Assert.True(inspection.SignatureValid);
        Assert.False(inspection.PublisherTrusted);
        Assert.Equal(64, inspection.PublisherKeyFingerprint.Length);

        Assert.Throws<InvalidDataException>(() => _service.Install(new PluginInstallRequest { BundlePath = signed }));
        PluginInstallResult installed = _service.Install(new PluginInstallRequest
        {
            BundlePath = signed,
            TrustPublisher = true
        });
        Assert.True(installed.Installed);
        Assert.Contains(_service.ListTrustedPublishers(), p => p.Fingerprint == inspection.PublisherKeyFingerprint);
    }

    [Fact]
    public void Traversal_and_non_json_payload_are_rejected()
    {
        string path = Path.Combine(_root, "bad.parux-plugin");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "plugin.json", JsonSerializer.Serialize(Manifest("bad-plugin"), JsonOptions));
            WriteEntry(archive, "../escape.json", "{}");
        }
        Assert.False(_service.Inspect(path).Valid);

        string binary = Path.Combine(_root, "binary.parux-plugin");
        using (var archive = ZipFile.Open(binary, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "plugin.json", JsonSerializer.Serialize(Manifest("binary-plugin"), JsonOptions));
            WriteEntry(archive, "payload.ps1", "Write-Host nope");
        }
        Assert.False(_service.Inspect(binary).Valid);
    }

    [Fact]
    public void Component_failure_rolls_back_prior_skill_and_install_directory()
    {
        string path = Path.Combine(_root, "rollback.parux-plugin");
        PaRuXPluginManifest manifest = Manifest("rollback-plugin");
        manifest.SkillFiles.AddRange(new[] { "skills/good.json", "skills/bad.json" });
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "plugin.json", JsonSerializer.Serialize(manifest, JsonOptions));
            WriteEntry(archive, "skills/good.json", JsonSerializer.Serialize(Skill("rollback-plugin.good", shell: false), JsonOptions));
            WriteEntry(archive, "skills/bad.json", "{ invalid");
        }

        Assert.False(_service.Inspect(path).Valid);
        Assert.DoesNotContain(_skills.List(), s => s.Id == "rollback-plugin.good");
    }

    private string BuildBundle(
        string id,
        bool includeSkill = false,
        bool includeAutomation = false,
        bool includeMcp = false,
        bool declaredShell = false,
        bool skillShell = false,
        bool declaredAutomations = false,
        bool declaredExternalProcesses = false)
    {
        string path = Path.Combine(_root, id + ".parux-plugin");
        PaRuXPluginManifest manifest = Manifest(id);
        manifest.Permissions.Shell = declaredShell;
        manifest.Permissions.Automations = declaredAutomations;
        manifest.Permissions.ExternalProcesses = declaredExternalProcesses;
        if (includeSkill) manifest.SkillFiles.Add("skills/main.json");
        if (includeAutomation) manifest.AutomationFiles.Add("automations/hourly.json");
        if (includeMcp) manifest.McpFiles.Add("mcp/fs.json");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "plugin.json", JsonSerializer.Serialize(manifest, JsonOptions));
        if (includeSkill)
            WriteEntry(archive, "skills/main.json", JsonSerializer.Serialize(Skill(id + ".main", skillShell), JsonOptions));
        if (includeAutomation)
        {
            WriteEntry(archive, "automations/hourly.json", JsonSerializer.Serialize(new CreateAutomationRequest
            {
                Name = "Plugin hourly",
                TriggerType = AutomationTriggerType.Schedule,
                TriggerConfigJson = "{\"intervalSeconds\":3600}",
                ActionType = AutomationActionType.CreateAgentTask,
                ActionConfigJson = "{\"goalTemplate\":\"Review plugin status\",\"role\":\"review\"}"
            }, JsonOptions));
        }
        if (includeMcp)
        {
            WriteEntry(archive, "mcp/fs.json", JsonSerializer.Serialize(new AddMcpServerRequest
            {
                Name = "filesystem tool",
                Transport = McpTransports.Stdio,
                Command = "npx",
                Args = "-y @modelcontextprotocol/server-filesystem C:\\safe"
            }, JsonOptions));
        }
        return path;
    }

    private string SignBundle(string source)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (ZipArchive archive = ZipFile.OpenRead(source))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                using Stream input = entry.Open();
                using var memory = new MemoryStream();
                input.CopyTo(memory);
                files[entry.FullName] = memory.ToArray();
            }
        }

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] hash = PluginBundleService.ComputeDeclarativeHash(files);
        byte[] signature = key.SignHash(hash);
        string signedPath = Path.Combine(_root, "signed-copy.parux-plugin");
        using (var output = ZipFile.Open(signedPath, ZipArchiveMode.Create))
        {
            foreach ((string name, byte[] data) in files)
                WriteEntry(output, name, data);
            WriteEntry(output, "signature.json", JsonSerializer.Serialize(new
            {
                algorithm = "ECDSA-SHA256",
                publicKeyPem = key.ExportSubjectPublicKeyInfoPem(),
                signatureBase64 = Convert.ToBase64String(signature)
            }, JsonOptions));
        }
        return signedPath;
    }

    private static PaRuXPluginManifest Manifest(string id) => new()
    {
        Id = id,
        DisplayName = id,
        Version = "1.0.0",
        Publisher = "PaRuX Tests",
        Description = "fixture"
    };

    private static SkillManifest Skill(string id, bool shell) => new()
    {
        Id = id,
        Name = id,
        DisplayName = id,
        Description = "plugin fixture",
        Category = "test",
        AllowedTools = shell ? new List<string> { "shell" } : new List<string>(),
        Permissions = new SkillPermissions { Shell = shell },
        Enabled = true
    };

    private static void WriteEntry(ZipArchive archive, string name, string text) =>
        WriteEntry(archive, name, Encoding.UTF8.GetBytes(text));

    private static void WriteEntry(ZipArchive archive, string name, byte[] data)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using Stream output = entry.Open();
        output.Write(data, 0, data.Length);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
