using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SkillPermissionPolicyTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "parux-skill-policy-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Theory]
    [InlineData("run_command", nameof(SkillPermissions.Shell))]
    [InlineData("delete_file", nameof(SkillPermissions.DeleteFiles))]
    [InlineData("write_file", nameof(SkillPermissions.WriteFiles))]
    [InlineData("read_file", nameof(SkillPermissions.ReadFiles))]
    [InlineData("browser_open", nameof(SkillPermissions.Browser))]
    [InlineData("web_search", nameof(SkillPermissions.Network))]
    [InlineData("computer_use_click", nameof(SkillPermissions.ComputerUse))]
    [InlineData("launch_process", nameof(SkillPermissions.ExternalProcesses))]
    public void Privileged_tools_are_rejected_without_their_permission(string tool, string expectedPermission)
    {
        var manifest = Manifest(tool);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => SkillPermissionPolicy.Validate(manifest));
        Assert.Contains(expectedPermission, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Browser_requires_both_browser_and_network_permissions()
    {
        var manifest = Manifest("browser_open");
        manifest.Permissions.Browser = true;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => SkillPermissionPolicy.Validate(manifest));
        Assert.Contains(nameof(SkillPermissions.Network), error.Message, StringComparison.Ordinal);
        manifest.Permissions.Network = true;
        SkillPermissionPolicy.Validate(manifest);
    }

    [Fact]
    public void Wildcard_requires_the_complete_privileged_permission_set()
    {
        var manifest = Manifest("*");
        Assert.Throws<InvalidDataException>(() => SkillPermissionPolicy.Validate(manifest));
        manifest.Permissions = AllPermissions();
        SkillPermissionPolicy.Validate(manifest);
    }

    [Fact]
    public void Required_internet_policy_requires_network_permission()
    {
        var manifest = Manifest();
        manifest.InternetPolicy = "required";
        Assert.Throws<InvalidDataException>(() => SkillPermissionPolicy.Validate(manifest));
        manifest.Permissions.Network = true;
        SkillPermissionPolicy.Validate(manifest);
    }

    [Fact]
    public void Generic_nonprivileged_capability_labels_are_not_misclassified()
    {
        SkillPermissionPolicy.Validate(Manifest("git"));
        SkillPermissionPolicy.Validate(Manifest("memory"));
    }

    [Fact]
    public void Registry_rejects_an_invalid_import_before_persisting_it()
    {
        Directory.CreateDirectory(_tempDir);
        var registry = new SkillRegistryService(_tempDir);
        var manifest = Manifest("run_command");
        manifest.Id = "malicious-import";
        manifest.DisplayName = "Malicious import";
        Assert.Throws<InvalidDataException>(() => registry.CreateOrUpdateUserSkill(manifest, isImport: true));
        Assert.DoesNotContain(registry.List(), s => s.Id == "malicious-import");
    }

    [Fact]
    public void Registry_persists_a_valid_import_with_explicit_permissions()
    {
        Directory.CreateDirectory(_tempDir);
        var registry = new SkillRegistryService(_tempDir);
        var manifest = Manifest("run_command");
        manifest.Id = "valid-shell-import";
        manifest.DisplayName = "Valid shell import";
        manifest.Permissions.Shell = true;
        SkillManifest saved = registry.CreateOrUpdateUserSkill(manifest, isImport: true);
        Assert.Equal("imported", saved.Source);
        Assert.Contains(registry.List(), s => s.Id == saved.Id && s.Permissions.Shell);
    }

    [Fact]
    public void SetPermissions_updates_a_builtin_skill_without_colliding_with_its_own_id()
    {
        // Regression: the Tools & Automations "Permissions..." editor used to call
        // CreateOrUpdateUserSkill for every skill, including built-ins. That method
        // unconditionally rejects any id already used by a built-in skill - so saving a
        // built-in skill's OWN permissions always failed with "A built-in skill already uses
        // id '<id>'." (confirmed live via the real UI: editing Grill Me's permissions and
        // clicking Save failed with exactly that 409). SetPermissions must succeed instead.
        Directory.CreateDirectory(_tempDir);
        var registry = new SkillRegistryService(_tempDir);
        SkillManifest builtin = registry.List().First(s => s.Source == "builtin");

        SkillManifest updated = registry.SetPermissions(builtin.Id, new SkillPermissions { ReadFiles = true });

        Assert.True(updated.Permissions.ReadFiles);
        Assert.True(registry.List().Single(s => s.Id == builtin.Id).Permissions.ReadFiles);
    }

    [Fact]
    public void SetPermissions_rejects_a_grant_that_violates_the_skills_own_allowed_tools_policy()
    {
        Directory.CreateDirectory(_tempDir);
        var registry = new SkillRegistryService(_tempDir);
        var manifest = Manifest("run_command");
        manifest.Id = "shell-user-skill";
        manifest.DisplayName = "Shell user skill";
        manifest.Permissions.Shell = true;
        registry.CreateOrUpdateUserSkill(manifest, isImport: true);

        Assert.Throws<InvalidDataException>(() => registry.SetPermissions("shell-user-skill", new SkillPermissions()));
        Assert.True(registry.List().Single(s => s.Id == "shell-user-skill").Permissions.Shell);
    }

    [Fact]
    public void SetPermissions_throws_for_an_unknown_skill_id()
    {
        Directory.CreateDirectory(_tempDir);
        var registry = new SkillRegistryService(_tempDir);
        Assert.Throws<InvalidDataException>(() => registry.SetPermissions("does-not-exist", new SkillPermissions()));
    }

    [Fact]
    public void Every_builtin_manifest_matches_the_permission_matrix()
    {
        Directory.CreateDirectory(_tempDir);
        var registry = new SkillRegistryService(_tempDir);
        List<SkillManifest> builtins = registry.List().Where(s => s.Source == "builtin").ToList();
        Assert.NotEmpty(builtins);
        foreach (SkillManifest skill in builtins)
        {
            Exception? error = Record.Exception(() => SkillPermissionPolicy.Validate(skill));
            Assert.True(error == null, "Built-in skill '" + skill.Id + "' violates the permission matrix: " + error?.Message);
        }
    }

    [Fact]
    public void Restore_rejects_an_invalid_manifest_without_replacing_live_registry()
    {
        string liveDir = Path.Combine(_tempDir, "live");
        string stagedDir = Path.Combine(_tempDir, "staged");
        Directory.CreateDirectory(liveDir);
        Directory.CreateDirectory(stagedDir);
        var registry = new SkillRegistryService(liveDir);
        var safe = Manifest("run_command");
        safe.Id = "safe";
        safe.DisplayName = "Safe";
        safe.Permissions.Shell = true;
        registry.CreateOrUpdateUserSkill(safe, isImport: true);

        string stagedDb = Path.Combine(stagedDir, "skills.db");
        CreateSkillsDatabase(stagedDb, new SkillManifest
        {
            Id = "unsafe", DisplayName = "Unsafe", Source = "imported",
            AllowedTools = new List<string> { "run_command" }, Permissions = new SkillPermissions()
        });

        Assert.Throws<InvalidDataException>(() => registry.ReplaceDatabaseFile(stagedDb));
        Assert.Contains(registry.List(), s => s.Id == "safe");
        Assert.DoesNotContain(registry.List(), s => s.Id == "unsafe");
    }

    [Fact]
    public void Restore_accepts_permission_valid_manifests()
    {
        string liveDir = Path.Combine(_tempDir, "live-valid");
        string stagedDir = Path.Combine(_tempDir, "staged-valid");
        Directory.CreateDirectory(liveDir);
        Directory.CreateDirectory(stagedDir);
        var registry = new SkillRegistryService(liveDir);
        _ = registry.List(); // initializes the live schema

        string stagedDb = Path.Combine(stagedDir, "skills.db");
        var valid = new SkillManifest
        {
            Id = "restored", DisplayName = "Restored", Source = "imported",
            AllowedTools = new List<string> { "run_command" },
            Permissions = new SkillPermissions { Shell = true }
        };
        CreateSkillsDatabase(stagedDb, valid);

        registry.ReplaceDatabaseFile(stagedDb);
        Assert.Contains(registry.List(), s => s.Id == "restored" && s.Permissions.Shell);
    }

    private static void CreateSkillsDatabase(string path, SkillManifest manifest)
    {
        using var connection = new SqliteConnection("Data Source=" + path);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE user_skills (id TEXT PRIMARY KEY, manifest_json TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
CREATE TABLE skill_overrides (id TEXT PRIMARY KEY, enabled INTEGER NOT NULL);
INSERT INTO user_skills (id, manifest_json, created_utc, updated_utc) VALUES ($id, $json, $now, $now);";
        command.Parameters.AddWithValue("$id", manifest.Id);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static SkillManifest Manifest(params string[] tools) => new()
    {
        DisplayName = "Policy test",
        AllowedTools = tools.ToList(),
        Permissions = new SkillPermissions()
    };

    private static SkillPermissions AllPermissions() => new()
    {
        ReadFiles = true,
        WriteFiles = true,
        DeleteFiles = true,
        Shell = true,
        Network = true,
        Browser = true,
        ComputerUse = true,
        ExternalProcesses = true
    };
}
