using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Role override value + origin sidecar behave as one unit: backup/restore and two real writer processes.</summary>
[Collection("ProcessEnvironmentSerial")]
public sealed class RoleOverrideOriginUnitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-role-origin-unit", Guid.NewGuid().ToString("N"));
    private readonly string _previousDataRoot = PaRuXDataRoot.Path;
    private readonly string _modelA;
    private readonly string _modelB;

    public RoleOverrideOriginUnitTests()
    {
        Directory.CreateDirectory(_root);
        _modelA = Path.Combine(_root, "a.gguf");
        _modelB = Path.Combine(_root, "b.gguf");
        File.WriteAllText(_modelA, "a");
        File.WriteAllText(_modelB, "b");
        PaRuXDataRoot.UseForTests(_root);
    }

    [Fact]
    public void Backup_round_trip_restores_overrides_and_origins_together()
    {
        LocalAiPaths.SetRoleOverride("reasoning", _modelA, RoleOverrideOrigin.AutoAssigned);
        LocalAiPaths.SetRoleOverride("coding", _modelB, RoleOverrideOrigin.User);
        Dictionary<string, string> backup = LocalAiPaths.ExportBackupConfig(_root);
        Assert.True(backup.ContainsKey("role-overrides"));
        Assert.True(backup.ContainsKey("role-override-origins"));
        LocalAiPaths.ValidateBackupConfig(backup);

        // The user pins the auto-assigned role afterwards, then restores the old backup as one unit.
        LocalAiPaths.SetRoleOverride("reasoning", _modelB, RoleOverrideOrigin.User);
        LocalAiPaths.ImportBackupConfig(backup, _root);

        Assert.Equal(RoleOverrideOrigin.AutoAssigned, LocalAiPaths.GetRoleOverrideOrigin("reasoning"));
        Assert.Equal(RoleOverrideOrigin.User, LocalAiPaths.GetRoleOverrideOrigin("coding"));
        Assert.Null(ManualSelectionGate.GetUserPinReason("reasoning", "balanced", _root));
        Assert.Equal("manual_pin_role_override", ManualSelectionGate.GetUserPinReason("coding", "balanced", _root));
    }

    [Fact]
    public void Restoring_a_backup_without_origins_drops_the_newer_sidecar_and_fails_closed_as_a_pin()
    {
        LocalAiPaths.SetRoleOverride("reasoning", _modelA, RoleOverrideOrigin.User);
        Dictionary<string, string> legacyBackup = LocalAiPaths.ExportBackupConfig(_root);
        legacyBackup.Remove("role-override-origins");
        // Local state later gains an auto-assigned label for the same value.
        LocalAiPaths.SetRoleOverride("reasoning", _modelA, RoleOverrideOrigin.AutoAssigned);
        Assert.Null(ManualSelectionGate.GetUserPinReason("reasoning", "balanced", _root));

        LocalAiPaths.ImportBackupConfig(legacyBackup, _root);

        Assert.False(File.Exists(Path.Combine(_root, "model-role-override-origins.json")));
        Assert.Equal("manual_pin_role_override", ManualSelectionGate.GetUserPinReason("reasoning", "balanced", _root));
    }

    [Fact]
    public void A_sidecar_origin_that_does_not_match_the_override_value_is_surfaced_as_a_pin()
    {
        LocalAiPaths.SetRoleOverride("reasoning", _modelA, RoleOverrideOrigin.AutoAssigned);
        File.WriteAllText(Path.Combine(_root, "model-role-overrides.json"),
            "{\"reasoning\":" + System.Text.Json.JsonSerializer.Serialize(_modelB) + "}");

        Assert.Equal("manual_pin_role_override", ManualSelectionGate.GetUserPinReason("reasoning", "balanced", _root));
    }

    [Fact]
    public async Task Two_real_writer_processes_are_linearized_and_neither_loses_its_role_or_origin()
    {
        string childA = _modelA, childB = _modelB;
        using Process first = StartChild("reasoning", childA, RoleOverrideOrigin.User);
        using Process second = StartChild("coding", childB, RoleOverrideOrigin.AutoAssigned);
        Task<string> firstDone = Finish(first), secondDone = Finish(second);
        for (int i = 0; i < 3000 && !(File.Exists(Path.Combine(_root, "ready-reasoning")) && File.Exists(Path.Combine(_root, "ready-coding"))); i++)
            await Task.Delay(10); // both child runtimes booted; they now wait on the barrier file
        File.WriteAllText(Path.Combine(_root, "go.flag"), "go");
        string[] output = await Task.WhenAll(firstDone, secondDone);

        Assert.All(output, text => Assert.Equal("done", text.Trim()));
        Assert.Equal(childA, LocalAiPaths.GetRoleOverride("reasoning"));
        Assert.Equal(childB, LocalAiPaths.GetRoleOverride("coding"));
        Assert.Equal(RoleOverrideOrigin.User, LocalAiPaths.GetRoleOverrideOrigin("reasoning"));
        Assert.Equal(RoleOverrideOrigin.AutoAssigned, LocalAiPaths.GetRoleOverrideOrigin("coding"));
        Assert.Equal("manual_pin_role_override", ManualSelectionGate.GetUserPinReason("reasoning", "balanced", _root));
        Assert.Null(ManualSelectionGate.GetUserPinReason("coding", "balanced", _root));
    }

    [Fact]
    public async Task A_write_after_another_process_wrote_does_not_lose_that_process_s_role_or_origin()
    {
        File.WriteAllText(Path.Combine(_root, "go.flag"), "go");
        LocalAiPaths.SetRoleOverride("reasoning", _modelA, RoleOverrideOrigin.User); // fills this process's cache
        using Process other = StartChild("coding", _modelB, RoleOverrideOrigin.AutoAssigned, 1);
        Assert.Equal("done", (await Finish(other)).Trim());

        LocalAiPaths.SetRoleOverride("planner", _modelA, RoleOverrideOrigin.User); // must start from the file, not the cache

        string overrides = File.ReadAllText(Path.Combine(_root, "model-role-overrides.json"));
        string origins = File.ReadAllText(Path.Combine(_root, "model-role-override-origins.json"));
        foreach (string role in new[] { "reasoning", "coding", "planner" })
        {
            Assert.Contains("\"" + role + "\"", overrides);
            Assert.Contains("\"" + role + "\"", origins);
        }
        Assert.Null(ManualSelectionGate.GetUserPinReason("coding", "balanced", _root));
        Assert.Equal("manual_pin_role_override", ManualSelectionGate.GetUserPinReason("reasoning", "balanced", _root));
    }

    private Process StartChild(string role, string model, string origin, int count = 60)
    {
        string assembly = typeof(RoleOverrideOriginUnitTests).Assembly.Location;
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var start = new ProcessStartInfo(!string.IsNullOrWhiteSpace(host) && File.Exists(host) ? host : "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory
        };
        start.Environment["PARUX_DATA_ROOT"] = _root;
        foreach (string argument in new[] { assembly, "manual-selection-role-write-many", _root, role, model, origin, count.ToString() })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("child did not start");
    }

    private static async Task<string> Finish(Process child)
    {
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            throw new TimeoutException("writer child did not finish");
        }
        Assert.True(child.ExitCode == 0, "child failed: " + await stderr);
        return await stdout;
    }

    public void Dispose()
    {
        PaRuXDataRoot.UseForTests(_previousDataRoot);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
