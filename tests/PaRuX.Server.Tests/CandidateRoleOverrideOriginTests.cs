using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CandidateRoleOverrideOriginTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly string _previousDataRoot;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-role-origin-tests", Guid.NewGuid().ToString("N"));

    public CandidateRoleOverrideOriginTests(ApiSecurityTests.PaRuXFactory factory)
    {
        _factory = factory;
        _previousDataRoot = PaRuXDataRoot.Path;
        Directory.CreateDirectory(_root);
        PaRuXDataRoot.UseForTests(_root);
    }

    public void Dispose()
    {
        PaRuXDataRoot.UseForTests(_previousDataRoot);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Legacy_override_without_sidecar_is_unknown_and_auto_assignment_keeps_it()
    {
        string existing = CreateModel("legacy.gguf");
        string replacement = CreateModel("replacement.gguf");
        WriteOverrides("coding", existing);

        Assert.Equal(RoleOverrideOrigin.UnknownLegacy, LocalAiPaths.GetRoleOverrideOrigin("coding"));
        Assert.False(LocalAiPaths.TrySetAutoAssignedRoleOverride("coding", replacement, out string? skippedReason));
        Assert.Equal(RoleOverrideOrigin.UnknownLegacyReason, skippedReason);
        Assert.Equal(existing, LocalAiPaths.GetRoleOverride("coding"));
    }

    [Fact]
    public void Sidecar_for_a_different_override_value_is_unknown_legacy()
    {
        string oldValue = Path.Combine(_root, "old-value.gguf");
        string currentValue = Path.Combine(_root, "current-value.gguf");
        WriteOverrides("reviewer", currentValue);
        WriteOrigins("reviewer", LocalAiPaths.OriginTag(RoleOverrideOrigin.AutoAssigned, oldValue));

        Assert.Equal(RoleOverrideOrigin.UnknownLegacy, LocalAiPaths.GetRoleOverrideOrigin("reviewer"));
    }

    [Fact]
    public void Restoring_an_older_sidecar_does_not_relabel_the_current_override()
    {
        string olderValue = Path.Combine(_root, "older-value.gguf");
        string currentValue = Path.Combine(_root, "current-value.gguf");
        WriteOverrides("planner", olderValue);
        WriteOrigins("planner", LocalAiPaths.OriginTag(RoleOverrideOrigin.User, olderValue));
        string restoredOlderSidecar = File.ReadAllText(OriginsPath);

        WriteOverrides("planner", currentValue);
        File.WriteAllText(OriginsPath, restoredOlderSidecar);

        Assert.Equal(RoleOverrideOrigin.UnknownLegacy, LocalAiPaths.GetRoleOverrideOrigin("planner"));
    }

    [Fact]
    public void Matching_sidecars_distinguish_user_and_auto_assigned_origins()
    {
        string userModel = CreateModel("user.gguf");
        string automaticModel = CreateModel("automatic.gguf");

        LocalAiPaths.SetRoleOverride("coding", userModel);
        Assert.Equal(RoleOverrideOrigin.User, LocalAiPaths.GetRoleOverrideOrigin("coding"));

        Assert.True(LocalAiPaths.TrySetAutoAssignedRoleOverride("planner", automaticModel, out string? skippedReason));
        Assert.Null(skippedReason);
        Assert.Equal(RoleOverrideOrigin.AutoAssigned, LocalAiPaths.GetRoleOverrideOrigin("planner"));
    }

    [Fact]
    public async Task Role_status_exposes_unknown_origin_and_plain_language_reason()
    {
        string currentValue = CreateModel("status-legacy.gguf");
        WriteOverrides("coding", currentValue);

        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        HttpResponseMessage response = await client.GetAsync("/api/v1/models/roles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<ModelRoleStatus>? roles = await response.Content.ReadFromJsonAsync<List<ModelRoleStatus>>();
        Assert.NotNull(roles);
        ModelRoleStatus status = Assert.Single(roles!, role => role.Role == "coding");
        Assert.True(status.IsOverridden);
        Assert.Equal(RoleOverrideOrigin.UnknownLegacy, status.OverrideOrigin);
        Assert.Equal(RoleOverrideOrigin.UnknownLegacyReason, status.OverrideOriginReason);
    }

    private string OriginsPath => Path.Combine(_root, "model-role-override-origins.json");

    private string CreateModel(string fileName)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "fixture");
        return path;
    }

    private void WriteOverrides(string role, string modelPath) =>
        File.WriteAllText(Path.Combine(_root, "model-role-overrides.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { [role] = modelPath }));

    private void WriteOrigins(string role, string tag) =>
        File.WriteAllText(OriginsPath,
            JsonSerializer.Serialize(new Dictionary<string, string> { [role] = tag }));
}
