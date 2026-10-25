using System.Diagnostics;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ManualSelectionGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-manual-gate", Guid.NewGuid().ToString("N"));

    public ManualSelectionGateTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void No_pin_when_the_user_selection_is_automatic_everywhere()
    {
        Assert.False(ManualSelectionGate.HasUserPin("reviewer", "balanced", _root));
    }

    [Fact]
    public void Chat_manual_routing_preference_is_a_pin()
    {
        File.WriteAllText(Path.Combine(_root, "m.gguf"), "x");
        ChatRoutingPreferenceStore.Set(new SetChatRoutingPreferenceRequest
        { Mode = RouteMode.Manual, ManualModelFileName = "m.gguf" }, _root, _root);
        Assert.True(ManualSelectionGate.HasUserPin("reviewer", "balanced", _root));
        ChatRoutingPreferenceStore.Set(new SetChatRoutingPreferenceRequest { Mode = RouteMode.Auto, AutoProfile = "balanced" }, _root, _root);
        Assert.False(ManualSelectionGate.HasUserPin("reviewer", "balanced", _root));
    }

    [Theory]
    [InlineData("force")]
    [InlineData("prefer")]
    public void Scoped_force_or_prefer_for_the_same_scope_is_a_pin_but_other_scopes_are_not(string mode)
    {
        CapabilityRoutingPreferenceStore.Set("reviewer", "balanced", new SetScopedRoutingOverrideRequest
        {
            Mode = mode, CandidateKey = new string('a', 64), Revision = new string('b', 40), ManagedFileName = "x.gguf"
        }, _root);
        Assert.True(ManualSelectionGate.HasUserPin("reviewer", "balanced", _root));
        Assert.False(ManualSelectionGate.HasUserPin("reviewer", "responsive", _root));
        Assert.True(CapabilityRoutingPreferenceStore.Delete("reviewer", "balanced", _root));
        Assert.False(ManualSelectionGate.HasUserPin("reviewer", "balanced", _root));
    }

    [Fact]
    public void Corrupt_chat_preference_fails_closed_as_a_pin()
    {
        File.WriteAllText(Path.Combine(_root, "chat-model-routing.json"), "{ not json");
        Assert.True(ManualSelectionGate.HasUserPin("reviewer", "balanced", _root));
    }

    [Fact]
    public void Pin_reasons_are_structured_per_source()
    {
        Assert.Null(ManualSelectionGate.GetUserPinReason("reviewer", "balanced", _root));
        File.WriteAllText(Path.Combine(_root, "m.gguf"), "x");
        ChatRoutingPreferenceStore.Set(new SetChatRoutingPreferenceRequest
        { Mode = RouteMode.Manual, ManualModelFileName = "m.gguf" }, _root, _root);
        Assert.Equal("manual_pin_chat_manual", ManualSelectionGate.GetUserPinReason("reviewer", "balanced", _root));
        ChatRoutingPreferenceStore.Set(new SetChatRoutingPreferenceRequest { Mode = RouteMode.Auto, AutoProfile = "balanced" }, _root, _root);
        CapabilityRoutingPreferenceStore.Set("reviewer", "balanced", new SetScopedRoutingOverrideRequest
        { Mode = "force", CandidateKey = new string('a', 64), Revision = new string('b', 40), ManagedFileName = "x.gguf" }, _root);
        Assert.Equal("manual_pin_scoped", ManualSelectionGate.GetUserPinReason("reviewer", "balanced", _root));
        File.WriteAllText(Path.Combine(_root, "chat-model-routing.json"), "{ nope");
        Assert.Equal("manual_pin_unreadable", ManualSelectionGate.GetUserPinReason("reviewer", "balanced", _root));
        foreach (string code in new[] { "manual_pin_role_override", "manual_pin_chat_manual", "manual_pin_scoped", "manual_pin_unreadable" })
        {
            string text = CandidateReasonText.Explain(code);
            Assert.Contains("Automatic recommendation is blocked", text);
            Assert.DoesNotContain("_", text);
        }
    }

    [Fact]
    public void Corrupt_chat_preference_is_reported_unreadable_not_as_automatic()
    {
        File.WriteAllText(Path.Combine(_root, "chat-model-routing.json"), "{ not json");
        Assert.True(ChatRoutingPreferenceStore.GetDto(_root).Unreadable);
        File.Delete(Path.Combine(_root, "chat-model-routing.json"));
        Assert.False(ChatRoutingPreferenceStore.GetDto(_root).Unreadable);
    }

    [Fact]
    public void Role_override_pin_depends_on_origin_and_legacy_entries_are_user_owned()
    {
        string overrides = Path.Combine(_root, "o.json"), origins = Path.Combine(_root, "origins.json");
        File.WriteAllText(overrides, "{\"reasoning\":\"C:\\\\m.gguf\",\"coding\":\"C:\\\\c.gguf\"}");
        // No origin file at all (legacy data): user-owned, pins.
        Assert.Equal("manual_pin_role_override", LocalAiPaths.UserRoleOverridePinReason("reasoning", overrides, origins));
        // A bare legacy label is not bound to the value: still user-owned.
        File.WriteAllText(origins, "{\"reasoning\":\"auto-assigned\"}");
        Assert.Equal("manual_pin_role_override", LocalAiPaths.UserRoleOverridePinReason("reasoning", overrides, origins));
        File.WriteAllText(origins, "{\"reasoning\":\"" + LocalAiPaths.OriginTag("auto-assigned", "C:\\m.gguf") + "\"}");
        Assert.Null(LocalAiPaths.UserRoleOverridePinReason("reasoning", overrides, origins));
        // The same auto tag no longer applies once the override points at a different model (user re-pinned
        // while only the sidecar was restored): fail closed.
        File.WriteAllText(overrides, "{\"reasoning\":\"C:\\\\other.gguf\",\"coding\":\"C:\\\\c.gguf\"}");
        Assert.Equal("manual_pin_role_override", LocalAiPaths.UserRoleOverridePinReason("reasoning", overrides, origins));
        File.WriteAllText(overrides, "{\"reasoning\":\"C:\\\\m.gguf\",\"coding\":\"C:\\\\c.gguf\"}");
        // An override with no origin entry beside other entries is still user-owned.
        Assert.Equal("manual_pin_role_override", LocalAiPaths.UserRoleOverridePinReason("coding", overrides, origins));
        Assert.Null(LocalAiPaths.UserRoleOverridePinReason("planner", overrides, origins));
        File.WriteAllText(origins, "{ torn");
        Assert.Equal("manual_pin_unreadable", LocalAiPaths.UserRoleOverridePinReason("reasoning", overrides, origins));
    }

    [Fact]
    public void Role_override_written_by_another_process_is_seen_despite_the_in_process_cache()
    {
        string file = Path.Combine(_root, "model-role-overrides.json");
        string origins = Path.Combine(_root, "none.json");
        Assert.Null(LocalAiPaths.UserRoleOverridePinReason("reasoning", file, origins));
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("Set-Content -LiteralPath '" + file.Replace("'", "''") + "' -Value '{\"reasoning\":\"C:\\\\m.gguf\"}'");
        using Process child = Process.Start(start)!;
        Assert.True(child.WaitForExit(30000));
        Assert.Equal(0, child.ExitCode);
        Assert.Equal("manual_pin_role_override", LocalAiPaths.UserRoleOverridePinReason("reasoning", file, origins));
        Assert.Null(LocalAiPaths.UserRoleOverridePinReason("coding", file, origins));
        File.WriteAllText(file, "{ torn");
        Assert.Equal("manual_pin_unreadable", LocalAiPaths.UserRoleOverridePinReason("coding", file, origins));
    }

    [Fact]
    public void Transient_sharing_violation_on_disk_read_is_retried_then_reported_unreadable()
    {
        string file = Path.Combine(_root, "o.json");
        File.WriteAllText(file, "{\"reasoning\":\"C:\\\\m.gguf\"}");
        string none = Path.Combine(_root, "none.json");
        var holder = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        Task.Run(async () => { await Task.Delay(60); holder.Dispose(); });
        Assert.Equal("manual_pin_role_override", LocalAiPaths.UserRoleOverridePinReason("reasoning", file, none));
        // Held beyond the retry budget: reported unreadable (a pin), never silently "no pin".
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal("manual_pin_unreadable", LocalAiPaths.UserRoleOverridePinReason("reasoning", file, none));
    }

    [Fact]
    public void Auto_assignment_never_replaces_a_user_override_but_replaces_its_own()
    {
        string a = Path.Combine(_root, "a.gguf"), b = Path.Combine(_root, "b.gguf");
        File.WriteAllText(a, "x"); File.WriteAllText(b, "y");
        try
        {
            LocalAiPaths.SetRoleOverride("coding", a); // user-origin
            Assert.False(LocalAiPaths.TrySetAutoAssignedRoleOverride("coding", b, out string? why));
            Assert.Equal("user_override_kept", why);
            Assert.Equal(a, LocalAiPaths.GetRoleOverride("coding"));
            Assert.Equal(RoleOverrideOrigin.User, LocalAiPaths.GetRoleOverrideOrigin("coding"));

            Assert.True(LocalAiPaths.TrySetAutoAssignedRoleOverride("planner", a, out _));
            Assert.Equal(RoleOverrideOrigin.AutoAssigned, LocalAiPaths.GetRoleOverrideOrigin("planner"));
            Assert.Null(LocalAiPaths.UserRoleOverridePinReason("planner"));
            Assert.True(LocalAiPaths.TrySetAutoAssignedRoleOverride("planner", b, out _));
            Assert.Equal(b, LocalAiPaths.GetRoleOverride("planner"));
            // The user then takes the role over: origin flips to user and auto-assignment backs off.
            LocalAiPaths.SetRoleOverride("planner", a);
            Assert.Equal("manual_pin_role_override", LocalAiPaths.UserRoleOverridePinReason("planner"));
            Assert.False(LocalAiPaths.TrySetAutoAssignedRoleOverride("planner", b, out _));
            // A missing target is rejected without touching anything.
            Assert.False(LocalAiPaths.TrySetAutoAssignedRoleOverride("reviewer", Path.Combine(_root, "missing.gguf"), out string? invalid));
            Assert.Equal("model_not_assignable", invalid);
        }
        finally
        {
            LocalAiPaths.ClearRoleOverride("coding"); LocalAiPaths.ClearRoleOverride("planner"); LocalAiPaths.ClearRoleOverride("reviewer");
        }
        Assert.Null(LocalAiPaths.GetRoleOverrideOrigin("planner"));
    }

    [Fact]
    public void Override_save_survives_a_brief_sharing_violation_and_leaves_no_staging_files()
    {
        string gguf = Path.Combine(_root, "a.gguf");
        File.WriteAllText(gguf, "x");
        string target = Path.Combine(PaRuXDataRoot.Path, "model-role-overrides.json");
        LocalAiPaths.SetRoleOverride("fast", gguf);
        try
        {
            var holder = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            Task.Run(async () => { await Task.Delay(120); holder.Dispose(); });
            LocalAiPaths.SetRoleOverride("fast", gguf);
            Assert.Equal(gguf, LocalAiPaths.GetRoleOverride("fast"));
            Assert.Equal("manual_pin_role_override", LocalAiPaths.UserRoleOverridePinReason("fast"));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "model-role-overrides.json.*.tmp"));
        }
        finally { LocalAiPaths.ClearRoleOverride("fast"); }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
