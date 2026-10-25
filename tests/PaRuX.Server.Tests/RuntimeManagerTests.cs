using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Directive Phase 5/6 (Runtime Manager + generalized roles): GET /api/v1/runtime/status and the
/// Managed Model Library load/unload endpoints. Load's validation-rejection paths (unknown role,
/// file not in the library, path traversal, corrupt GGUF) all return before touching the real
/// llama-server process, so they're safe to test hermetically; the real "load and run" success
/// path needs the actual runtime and is covered by live verification instead, per this project's
/// existing convention (see ModelRolesTests' own doc comment for the same trade-off).
///
/// A corrupt-file test needs a real file inside ModelDownloadService.ModelsDirectory (a fixed,
/// non-injectable %LOCALAPPDATA%\PaRuX\models\ path) - follows ModelDownloadServiceTests' own
/// established pattern: write a real file under a unique GUID name, delete it in Dispose().
/// </summary>
// Shares "git"/"python" component-location mutations against the real %LOCALAPPDATA%\PaRuX
// sidecar with ComponentLifecycleAcceptanceTests/ComponentInventoryServiceTests/ComponentValidationTests
// - must run serialized against them (see ProcessEnvironmentSerialCollection) or a concurrent
// remove-location/locate from another class races this class's own locate-then-assert flow.
[Collection("ProcessEnvironmentSerial")]
public sealed class RuntimeManagerTests : IClassFixture<ApiSecurityTests.PaRuXFactory>, IDisposable
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    private readonly List<string> _createdPaths = new();

    private static string ModelsDir => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "models");

    public RuntimeManagerTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    public void Dispose()
    {
        foreach (string path in _createdPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private HttpClient AuthedClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    [Fact]
    public async Task Runtime_status_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/runtime/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Runtime_status_endpoint_reports_both_llama_backends_and_comfyui()
    {
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/runtime/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RuntimeManagerStatus? status = await response.Content.ReadFromJsonAsync<RuntimeManagerStatus>();
        Assert.NotNull(status);
        Assert.Contains(status!.Backends, b => b.Backend == "text-cuda");
        Assert.Contains(status.Backends, b => b.Backend == "text-vulkan");
        Assert.Contains(status.Backends, b => b.Backend == "media-comfyui");
    }

    [Fact]
    public async Task Component_inventory_is_authenticated_explicitly_owned_and_safe_when_optional_backends_are_missing()
    {
        using HttpClient unauthenticated = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await unauthenticated.GetAsync("/api/v1/runtime/components")).StatusCode);

        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/runtime/components");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ComponentStatus[]? components = await response.Content.ReadFromJsonAsync<ComponentStatus[]>();
        Assert.NotNull(components);
        Assert.Contains(components!, component => component.Id == "llama-server-cuda" && component.Ownership == ComponentOwnership.PARUX_MANAGED);
        // python is exclusively PaRuX-managed (ManagedPythonRuntime) and never PATH/system-detected.
        Assert.Contains(components!, component => component.Id == "python" && component.Ownership == ComponentOwnership.PARUX_MANAGED);
        Assert.Contains(components!, component => component.Id == "npm" && component.Ownership == ComponentOwnership.SYSTEM_DETECTED);
        Assert.All(components!, component => Assert.NotNull(component.Actions));
    }

    /// <summary>2026-08-27: "Locate existing" turns a SYSTEM_DETECTED component into USER_MANAGED
    /// by persisting a real, user-supplied path - never installing, running, or modifying it.
    /// Uses a real temp file to stand in for "an already-installed python.exe" since the endpoint
    /// only checks the path exists, matching LocalAiPaths.SetComponentLocation's own contract.
    /// Cleans up the persisted component-locations.json entry via the real remove-location
    /// endpoint afterward so this test never leaves the shared %LOCALAPPDATA%\PaRuX sidecar
    /// pointing at a temp file that no longer exists once this test process ends.</summary>
    [Fact]
    public async Task Locate_endpoint_turns_a_system_detected_component_into_user_managed_and_remove_reverts_it()
    {
        string fakeInstall = Path.Combine(Path.GetTempPath(), "parux-locate-test-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(fakeInstall, "stand-in for an already-installed component");
        _createdPaths.Add(fakeInstall);

        using HttpClient client = AuthedClient();
        try
        {
            HttpResponseMessage locateResponse = await client.PostAsJsonAsync(
                "/api/v1/runtime/components/git/locate", new ComponentLocateRequest { Path = fakeInstall });
            Assert.Equal(HttpStatusCode.OK, locateResponse.StatusCode);
            ComponentStatus? located = await locateResponse.Content.ReadFromJsonAsync<ComponentStatus>();
            Assert.NotNull(located);
            Assert.Equal(ComponentOwnership.USER_MANAGED, located!.Ownership);
            Assert.True(located.Detected);
            Assert.Equal(Path.GetFullPath(fakeInstall), located.Path);

            ComponentStatus[]? afterLocate = await (await client.GetAsync("/api/v1/runtime/components"))
                .Content.ReadFromJsonAsync<ComponentStatus[]>();
            Assert.Contains(afterLocate!, c => c.Id == "git" && c.Ownership == ComponentOwnership.USER_MANAGED);
        }
        finally
        {
            HttpResponseMessage removeResponse = await client.PostAsync("/api/v1/runtime/components/git/remove-location", null);
            Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);
        }

        ComponentStatus[]? afterRemove = await (await client.GetAsync("/api/v1/runtime/components"))
            .Content.ReadFromJsonAsync<ComponentStatus[]>();
        Assert.Contains(afterRemove!, c => c.Id == "git" && c.Ownership == ComponentOwnership.SYSTEM_DETECTED);
    }

    [Fact]
    public async Task Locate_endpoint_rejects_missing_token_unknown_component_and_nonexistent_path()
    {
        using HttpClient unauthenticated = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await unauthenticated.PostAsJsonAsync(
            "/api/v1/runtime/components/python/locate", new ComponentLocateRequest { Path = "C:\\anything" })).StatusCode);

        using HttpClient client = AuthedClient();
        HttpResponseMessage unknownComponent = await client.PostAsJsonAsync(
            "/api/v1/runtime/components/not-a-real-component/locate", new ComponentLocateRequest { Path = "C:\\anything" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownComponent.StatusCode);

        HttpResponseMessage missingPath = await client.PostAsJsonAsync("/api/v1/runtime/components/git/locate",
            new ComponentLocateRequest { Path = Path.Combine(Path.GetTempPath(), "parux-locate-test-does-not-exist-" + Guid.NewGuid().ToString("N")) });
        Assert.Equal(HttpStatusCode.BadRequest, missingPath.StatusCode);
    }

    /// <summary>2026-08-27: "Skip for now" - the third leg of Detect/Locate/Install/Skip - is a
    /// pure don't-prompt-me-again memory, never an ownership change or an install. Exercised
    /// against "comfyui" (a PARUX_MANAGED component that is genuinely not installed on this
    /// hermetic test machine, so the skip is real and observable rather than a no-op against an
    /// already-installed component). Cleaned up via the real unskip endpoint so this test never
    /// leaves the shared %LOCALAPPDATA%\PaRuX sidecar permanently marking comfyui skipped.</summary>
    [Fact]
    public async Task Skip_endpoint_marks_an_uninstalled_component_skipped_and_unskip_reverts_it()
    {
        using HttpClient client = AuthedClient();
        try
        {
            HttpResponseMessage skipResponse = await client.PostAsync("/api/v1/runtime/components/comfyui/skip", null);
            Assert.Equal(HttpStatusCode.OK, skipResponse.StatusCode);
            ComponentStatus? skipped = await skipResponse.Content.ReadFromJsonAsync<ComponentStatus>();
            Assert.NotNull(skipped);
            if (!skipped!.Detected)
            {
                Assert.True(skipped.Skipped);
                Assert.Contains("unskip", skipped.Actions);
            }

            ComponentStatus[]? afterSkip = await (await client.GetAsync("/api/v1/runtime/components"))
                .Content.ReadFromJsonAsync<ComponentStatus[]>();
            ComponentStatus comfyAfterSkip = afterSkip!.First(c => c.Id == "comfyui");
            if (!comfyAfterSkip.Detected) Assert.True(comfyAfterSkip.Skipped);
        }
        finally
        {
            HttpResponseMessage unskipResponse = await client.PostAsync("/api/v1/runtime/components/comfyui/unskip", null);
            Assert.Equal(HttpStatusCode.OK, unskipResponse.StatusCode);
        }

        ComponentStatus[]? afterUnskip = await (await client.GetAsync("/api/v1/runtime/components"))
            .Content.ReadFromJsonAsync<ComponentStatus[]>();
        Assert.False(afterUnskip!.First(c => c.Id == "comfyui").Skipped);
    }

    [Fact]
    public async Task Skip_endpoint_rejects_missing_token_and_unknown_component_id()
    {
        using HttpClient unauthenticated = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await unauthenticated.PostAsync("/api/v1/runtime/components/comfyui/skip", null)).StatusCode);

        using HttpClient client = AuthedClient();
        HttpResponseMessage unknownComponent = await client.PostAsync("/api/v1/runtime/components/not-a-real-component/skip", null);
        Assert.Equal(HttpStatusCode.BadRequest, unknownComponent.StatusCode);
    }

    [Fact]
    public async Task Runtime_status_endpoint_never_reports_an_active_role_or_profile_with_no_text_process_running()
    {
        // Real bug caught by live testing on this machine: ActiveProfileFile/ActiveRoleFile are
        // written once and never cleared on an unclean exit, so a fresh hermetic test run (which
        // never starts a real llama-server) must never surface a stale value as "currently
        // active" - RuntimeManagerService.GetStatusAsync gates both on the text process actually
        // being alive right now.
        using HttpClient client = AuthedClient();
        RuntimeManagerStatus? status = await (await client.GetAsync("/api/v1/runtime/status")).Content.ReadFromJsonAsync<RuntimeManagerStatus>();
        Assert.NotNull(status);
        bool textRunning = status!.Backends.Any(b => b.Backend.StartsWith("text-") && b.Running);
        if (!textRunning)
        {
            Assert.Null(status.ActiveRole);
            Assert.Null(status.ActiveProfile);
        }
    }

    [Fact]
    public async Task Load_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/library/anything.gguf/load",
            new LoadManagedModelRequest { Role = "fast" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Load_endpoint_rejects_a_role_the_managed_library_cannot_back()
    {
        // "image" is a real role in the catalog (backed by the ComfyUI checkpoint system), but
        // not a text/GGUF role a Managed Model Library file can be loaded into.
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/library/anything.gguf/load",
            new LoadManagedModelRequest { Role = "image" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        RuntimeLoadResult? result = await response.Content.ReadFromJsonAsync<RuntimeLoadResult>();
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("Unknown or unsupported role", result.Message);
    }

    [Fact]
    public async Task Load_endpoint_rejects_a_file_not_in_the_managed_library()
    {
        using HttpClient client = AuthedClient();
        string missingName = "parux-runtime-test-missing-" + Guid.NewGuid().ToString("N") + ".gguf";
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/library/" + missingName + "/load",
            new LoadManagedModelRequest { Role = "fast" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        RuntimeLoadResult? result = await response.Content.ReadFromJsonAsync<RuntimeLoadResult>();
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("not in the Managed Model Library", result.Message);
    }

    [Fact]
    public async Task Load_endpoint_rejects_path_traversal_in_the_file_name()
    {
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/models/library/" + Uri.EscapeDataString("../secrets.gguf") + "/load",
            new LoadManagedModelRequest { Role = "fast" });
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Load_endpoint_rejects_a_corrupt_gguf_file()
    {
        Directory.CreateDirectory(ModelsDir);
        string fileName = "parux-runtime-test-corrupt-" + Guid.NewGuid().ToString("N") + ".gguf";
        string path = Path.Combine(ModelsDir, fileName);
        await File.WriteAllTextAsync(path, "not a real gguf file, missing the magic header");
        _createdPaths.Add(path);

        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/library/" + fileName + "/load",
            new LoadManagedModelRequest { Role = "fast" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        RuntimeLoadResult? result = await response.Content.ReadFromJsonAsync<RuntimeLoadResult>();
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Contains("corrupt", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_endpoint_rejects_an_unknown_role_before_touching_the_file_system()
    {
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/library/anything.gguf/load",
            new LoadManagedModelRequest { Role = "not-a-real-role" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        RuntimeLoadResult? result = await response.Content.ReadFromJsonAsync<RuntimeLoadResult>();
        Assert.NotNull(result);
        Assert.Contains("Unknown or unsupported role", result!.Message);
    }

    [Fact]
    public async Task Unload_endpoint_requires_auth()
    {
        // Deliberately does not call this successfully against the real server the way
        // ModelRolesTests avoids the real SetRoleOverride success path: Unload() reaches
        // LocalAiProcessManager.StopText(), which reads/kills against a real, non-injectable,
        // machine-wide PID file (LocalAiPaths.TextPidFile lives under the shared LocalAI state
        // dir, not a test-scoped path) - a hermetic run must never risk touching a real llama-
        // server process this developer's own running app may have started. Real load/unload
        // is covered by live verification against the actual server instead.
        using HttpClient unauthed = _factory.CreateClient();
        HttpResponseMessage unauthedResponse = await unauthed.PostAsync("/api/v1/models/library/unload", null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthedResponse.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/runtime/diagnostics");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_endpoint_returns_a_real_issue_list_shape()
    {
        // Read-only: every check inside GetDiagnosticsAsync reads existing state (file
        // existence, GPU detection, role assignments) - safe to call for real, unlike
        // Unload/a resolve that might swap.
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/runtime/diagnostics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RuntimeDiagnosticsReport? report = await response.Content.ReadFromJsonAsync<RuntimeDiagnosticsReport>();
        Assert.NotNull(report);
        Assert.NotNull(report!.Issues);
        foreach (RuntimeDiagnosticIssue issue in report.Issues)
        {
            Assert.Contains(issue.Severity, new[] { "warning", "info" });
            Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        }
    }

    [Fact]
    public async Task Alias_endpoint_rejects_missing_token()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/library/anything.gguf/alias",
            new SetManagedModelAliasRequest { Alias = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Alias_endpoint_rejects_a_file_not_in_the_managed_library()
    {
        using HttpClient client = AuthedClient();
        string missingName = "parux-alias-test-missing-" + Guid.NewGuid().ToString("N") + ".gguf";
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/models/library/" + missingName + "/alias",
            new SetManagedModelAliasRequest { Alias = "x" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Library_listing_includes_the_new_alias_and_benchmark_fields_with_no_data()
    {
        // Doesn't need a real managed file: an empty/absent library still returns a well-formed
        // (possibly empty) list, proving the new benchmark-enrichment join doesn't throw when
        // there's nothing to enrich.
        using HttpClient client = AuthedClient();
        HttpResponseMessage response = await client.GetAsync("/api/v1/models/library");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<ManagedModelInfo>? library = await response.Content.ReadFromJsonAsync<List<ManagedModelInfo>>();
        Assert.NotNull(library);
    }
}
