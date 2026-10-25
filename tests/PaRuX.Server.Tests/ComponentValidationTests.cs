using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Locks down the additive ValidationStatus contract without changing the legacy Health
/// values consumed by ComponentHealthChanged automations.</summary>
// Shares "git" component-location mutations against the real %LOCALAPPDATA%\PaRuX sidecar with
// ComponentLifecycleAcceptanceTests/ComponentInventoryServiceTests/RuntimeManagerTests - must run
// serialized against them (see ProcessEnvironmentSerialCollection) or a concurrent
// remove-location/locate from another class races this class's own locate-then-assert flow.
[Collection("ProcessEnvironmentSerial")]
public sealed class ComponentValidationTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public ComponentValidationTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private HttpClient Client()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    [Fact]
    public async Task Runnable_located_component_is_validated_while_legacy_health_stays_detected()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX executable fixture; Windows real executables are probed by the same code path.

        string script = Path.Combine(Path.GetTempPath(), "parux-validation-" + Guid.NewGuid().ToString("N") + ".sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho 'Git fixture 1.2.3'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using HttpClient client = Client();
        try
        {
            HttpResponseMessage locate = await client.PostAsJsonAsync(
                "/api/v1/runtime/components/git/locate", new ComponentLocateRequest { Path = script });
            Assert.True(locate.IsSuccessStatusCode);

            ComponentStatus[] components = (await client.GetFromJsonAsync<ComponentStatus[]>("/api/v1/runtime/components"))!;
            ComponentStatus git = components.Single(c => c.Id == "git");
            Assert.Equal("detected", git.Health); // old automation contract is unchanged
            Assert.Equal("validated", git.ValidationStatus);
            Assert.Contains("Git fixture 1.2.3", git.ValidationMessage);
        }
        finally
        {
            await client.PostAsync("/api/v1/runtime/components/git/remove-location", null);
            File.Delete(script);
        }
    }

    [Fact]
    public async Task Existing_but_non_runnable_user_path_is_unverified_not_fake_validated()
    {
        string fake = Path.Combine(Path.GetTempPath(), "parux-unverified-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(fake, "not executable");

        using HttpClient client = Client();
        try
        {
            // "git" (not "python"): managed Python is exclusively PaRuX-owned and its /locate
            // endpoint rejects binding to any user/system path - see the PARUX_MANAGED invariant.
            HttpResponseMessage locate = await client.PostAsJsonAsync(
                "/api/v1/runtime/components/git/locate", new ComponentLocateRequest { Path = fake });
            Assert.True(locate.IsSuccessStatusCode);

            ComponentStatus[] components = (await client.GetFromJsonAsync<ComponentStatus[]>("/api/v1/runtime/components"))!;
            ComponentStatus git = components.Single(c => c.Id == "git");
            Assert.True(git.Detected);
            Assert.Equal("detected", git.Health);
            Assert.Equal("unverified", git.ValidationStatus);
            Assert.Contains("could not positively identify", git.ValidationMessage);
        }
        finally
        {
            await client.PostAsync("/api/v1/runtime/components/git/remove-location", null);
            File.Delete(fake);
        }
    }

    [Fact]
    public async Task User_managed_path_deleted_after_configuration_reports_missing_without_being_replaced()
    {
        string fake = Path.Combine(Path.GetTempPath(), "parux-missing-after-locate-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(fake, "placeholder");

        using HttpClient client = Client();
        try
        {
            HttpResponseMessage locate = await client.PostAsJsonAsync(
                "/api/v1/runtime/components/node/locate", new ComponentLocateRequest { Path = fake });
            Assert.True(locate.IsSuccessStatusCode);
            File.Delete(fake);

            ComponentStatus[] components = (await client.GetFromJsonAsync<ComponentStatus[]>("/api/v1/runtime/components"))!;
            ComponentStatus node = components.Single(c => c.Id == "node");
            Assert.Equal(ComponentOwnership.USER_MANAGED, node.Ownership);
            Assert.False(node.Detected);
            Assert.Equal("missing", node.Health);
            Assert.Equal("missing", node.ValidationStatus);
            Assert.Contains("did not alter or replace", node.ValidationMessage);
        }
        finally
        {
            await client.PostAsync("/api/v1/runtime/components/node/remove-location", null);
            if (File.Exists(fake)) File.Delete(fake);
        }
    }

    [Fact]
    public async Task Located_comfyui_checkout_is_identity_validated_without_claiming_a_version()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-comfy-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "main.py"), "# marker");

        using HttpClient client = Client();
        try
        {
            HttpResponseMessage locate = await client.PostAsJsonAsync(
                "/api/v1/runtime/components/comfyui/locate", new ComponentLocateRequest { Path = dir });
            Assert.True(locate.IsSuccessStatusCode);

            ComponentStatus[] components = (await client.GetFromJsonAsync<ComponentStatus[]>("/api/v1/runtime/components"))!;
            ComponentStatus comfy = components.Single(c => c.Id == "comfyui");
            Assert.Equal("validated", comfy.ValidationStatus);
            Assert.Null(comfy.Version);
            Assert.Contains("main.py", comfy.ValidationMessage);
        }
        finally
        {
            await client.PostAsync("/api/v1/runtime/components/comfyui/remove-location", null);
            Directory.Delete(dir, true);
        }
    }
}
