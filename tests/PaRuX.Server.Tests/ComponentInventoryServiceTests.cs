using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Locks down that every advertised component action has a real backing mechanism. ComfyUI has
/// an explicit managed-install surface while Locate existing remains available for tools where a
/// deliberate USER_MANAGED installation is supported. Python and uv are a stronger invariant:
/// they are PaRuX-managed only and can never be rebound to PATH/user software.
/// </summary>
// Shares "git"/"comfyui" component-location mutations against the real %LOCALAPPDATA%\PaRuX
// sidecar with ComponentLifecycleAcceptanceTests/ComponentValidationTests/RuntimeManagerTests -
// must run serialized against them (see ProcessEnvironmentSerialCollection) or a concurrent
// remove-location/locate from another class races this class's own locate-then-assert flow.
[Collection("ProcessEnvironmentSerial")]
public sealed class ComponentInventoryServiceTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;
    public ComponentInventoryServiceTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);
        return client;
    }

    // ---- TryResolveComfyMainPy: pure identity validation, no server needed ----

    [Fact]
    public void TryResolveComfyMainPy_returns_null_for_missing_or_blank_input()
    {
        Assert.Null(ComponentInventoryService.TryResolveComfyMainPy(null));
        Assert.Null(ComponentInventoryService.TryResolveComfyMainPy(""));
        Assert.Null(ComponentInventoryService.TryResolveComfyMainPy("   "));
    }

    [Fact]
    public void TryResolveComfyMainPy_returns_null_when_the_folder_has_no_main_py()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-comfy-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Assert.Null(ComponentInventoryService.TryResolveComfyMainPy(dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TryResolveComfyMainPy_returns_null_for_a_path_that_does_not_exist_at_all()
    {
        string missing = Path.Combine(Path.GetTempPath(), "parux-comfy-does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.Null(ComponentInventoryService.TryResolveComfyMainPy(missing));
    }

    [Fact]
    public void TryResolveComfyMainPy_resolves_a_folder_containing_main_py()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-comfy-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string mainPy = Path.Combine(dir, "main.py");
        File.WriteAllText(mainPy, "# stand-in for ComfyUI's real entry point");
        try
        {
            string? resolved = ComponentInventoryService.TryResolveComfyMainPy(dir);
            Assert.Equal(Path.GetFullPath(mainPy), resolved);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TryResolveComfyMainPy_resolves_main_py_passed_directly()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-comfy-direct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string mainPy = Path.Combine(dir, "main.py");
        File.WriteAllText(mainPy, "# stand-in for a real ComfyUI checkout's entry point");
        try
        {
            string? resolved = ComponentInventoryService.TryResolveComfyMainPy(mainPy);
            Assert.Equal(Path.GetFullPath(mainPy), resolved);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- Advertised-action honesty across the whole inventory ----

    [Fact]
    public async Task Component_inventory_never_advertises_an_action_without_a_real_backing_endpoint()
    {
        using HttpClient client = AuthedClient();

        // Normalize any manual ComfyUI location left by a developer's previous local run against
        // the shared %LOCALAPPDATA%\PaRuX sidecar, so the not-yet-located "locate" action below is
        // observed deterministically rather than "relocate" from stale prior state.
        await client.PostAsync("/api/v1/runtime/components/comfyui/remove-location", null);

        ComponentStatus[]? components = await (await client.GetAsync("/api/v1/runtime/components"))
            .Content.ReadFromJsonAsync<ComponentStatus[]>();
        Assert.NotNull(components);

        Assert.All(components!, c => Assert.DoesNotContain("health", c.Actions));
        Assert.All(components!, c => Assert.DoesNotContain("update", c.Actions));
        Assert.All(components!, c => Assert.DoesNotContain("repair", c.Actions));

        // "locate" is only real for the four external PATH components plus ComfyUI. Managed
        // Python/uv and llama runtimes must never advertise it.
        foreach (string id in new[] { "llama-server-cuda", "llama-server-vulkan", "python", "uv" })
        {
            ComponentStatus component = components!.First(c => c.Id == id);
            Assert.DoesNotContain("locate", component.Actions);
            Assert.DoesNotContain("remove", component.Actions);
        }

        ComponentStatus cuda = components!.First(c => c.Id == "llama-server-cuda");
        ComponentStatus vulkan = components!.First(c => c.Id == "llama-server-vulkan");
        ComponentStatus comfy = components!.First(c => c.Id == "comfyui");
        Assert.Contains("install", cuda.Actions);
        Assert.Contains("install", vulkan.Actions);
        Assert.Contains("install", comfy.Actions);
        Assert.Contains("locate", comfy.Actions);
    }

    [Fact]
    public async Task Python_and_uv_are_exclusively_parux_managed_and_reject_locate_and_skip()
    {
        using HttpClient client = AuthedClient();
        ComponentStatus[]? components = await (await client.GetAsync("/api/v1/runtime/components"))
            .Content.ReadFromJsonAsync<ComponentStatus[]>();
        Assert.NotNull(components);

        string standIn = Path.GetTempFileName();
        try
        {
            foreach (string id in new[] { "python", "uv" })
            {
                ComponentStatus component = components!.Single(c => c.Id == id);
                Assert.Equal(ComponentOwnership.PARUX_MANAGED, component.Ownership);
                Assert.False(component.Skipped);
                Assert.Empty(component.Actions);
                Assert.DoesNotContain(ComponentInventoryService.LocatableComponents, c => c.Id == id);
                Assert.DoesNotContain(id, ComponentInventoryService.AllComponentIds);

                HttpResponseMessage locate = await client.PostAsJsonAsync(
                    $"/api/v1/runtime/components/{id}/locate", new ComponentLocateRequest { Path = standIn });
                Assert.Equal(HttpStatusCode.BadRequest, locate.StatusCode);

                HttpResponseMessage skip = await client.PostAsync($"/api/v1/runtime/components/{id}/skip", null);
                Assert.Equal(HttpStatusCode.BadRequest, skip.StatusCode);
            }
        }
        finally { File.Delete(standIn); }
    }

    [Fact]
    public async Task Llama_server_backends_advertise_install_and_only_advertise_rollback_once_installed()
    {
        using HttpClient client = AuthedClient();
        ComponentStatus[]? components = await (await client.GetAsync("/api/v1/runtime/components"))
            .Content.ReadFromJsonAsync<ComponentStatus[]>();
        Assert.NotNull(components);

        foreach (string id in new[] { "llama-server-cuda", "llama-server-vulkan" })
        {
            ComponentStatus component = components!.First(c => c.Id == id);
            Assert.Contains("install", component.Actions);
            Assert.Equal(component.Detected, component.Actions.Contains("rollback"));
            if (!component.Detected) Assert.Contains(component.Skipped ? "unskip" : "skip", component.Actions);
        }
    }

    // ---- ComfyUI "Locate existing" ----

    [Fact]
    public async Task Locate_endpoint_for_comfyui_rejects_a_folder_that_is_not_a_comfyui_installation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-comfy-notreal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using HttpClient client = AuthedClient();
            HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/v1/runtime/components/comfyui/locate", new ComponentLocateRequest { Path = dir });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Locate_endpoint_for_comfyui_accepts_a_real_checkout_and_remove_location_reverts_it()
    {
        string dir = Path.Combine(Path.GetTempPath(), "parux-comfy-checkout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string mainPy = Path.Combine(dir, "main.py");
        await File.WriteAllTextAsync(mainPy, "# stand-in for a real ComfyUI checkout's entry point");

        using HttpClient client = AuthedClient();
        try
        {
            HttpResponseMessage locateResponse = await client.PostAsJsonAsync(
                "/api/v1/runtime/components/comfyui/locate", new ComponentLocateRequest { Path = dir });
            Assert.Equal(HttpStatusCode.OK, locateResponse.StatusCode);
            ComponentStatus? located = await locateResponse.Content.ReadFromJsonAsync<ComponentStatus>();
            Assert.NotNull(located);
            Assert.Equal(ComponentOwnership.USER_MANAGED, located!.Ownership);
            Assert.True(located.Detected);
            Assert.Equal(Path.GetFullPath(mainPy), located.Path);
            Assert.Contains("remove", located.Actions);
            Assert.Contains("install", located.Actions);

            ComponentStatus[]? afterLocate = await (await client.GetAsync("/api/v1/runtime/components"))
                .Content.ReadFromJsonAsync<ComponentStatus[]>();
            Assert.Contains(afterLocate!, c => c.Id == "comfyui" && c.Ownership == ComponentOwnership.USER_MANAGED);
        }
        finally
        {
            HttpResponseMessage removeResponse = await client.PostAsync("/api/v1/runtime/components/comfyui/remove-location", null);
            Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);
            Directory.Delete(dir, recursive: true);
        }

        ComponentStatus[]? afterRemove = await (await client.GetAsync("/api/v1/runtime/components"))
            .Content.ReadFromJsonAsync<ComponentStatus[]>();
        Assert.Contains(afterRemove!, c => c.Id == "comfyui" && c.Ownership == ComponentOwnership.PARUX_MANAGED);
    }

    // ---- Real --version probing for located components ----

    [Fact]
    public async Task Located_component_version_falls_back_to_running_version_when_no_file_version_resource_exists()
    {
        // The probe is the only version source for a located program without a file-version resource.
        // On Windows that is a batch wrapper: it is not a PE image, so FileVersionInfo yields nothing and
        // the probe runs it through cmd /c. Other hosts use a POSIX shell script. Each branch runs on its
        // own platform, so the fallback is exercised on the shipping platform instead of being skipped.
        bool windows = OperatingSystem.IsWindows();
        string script = Path.Combine(Path.GetTempPath(), "parux-fake-tool-" + Guid.NewGuid().ToString("N") + (windows ? ".cmd" : ".sh"));
        await File.WriteAllTextAsync(script, windows
            ? "@echo off\r\necho FakeTool version 9.9.9\r\nexit /b 0\r\n"
            : "#!/bin/sh\necho \"FakeTool version 9.9.9\"\nexit 0\n");
        if (!windows)
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        using HttpClient client = AuthedClient();
        try
        {
            HttpResponseMessage locateResponse = await client.PostAsJsonAsync(
                "/api/v1/runtime/components/git/locate", new ComponentLocateRequest { Path = script });
            Assert.Equal(HttpStatusCode.OK, locateResponse.StatusCode);

            ComponentStatus[]? components = await (await client.GetAsync("/api/v1/runtime/components"))
                .Content.ReadFromJsonAsync<ComponentStatus[]>();
            ComponentStatus git = components!.First(c => c.Id == "git");
            Assert.True(git.Detected);
            Assert.Equal("FakeTool version 9.9.9", git.Version);
        }
        finally
        {
            await client.PostAsync("/api/v1/runtime/components/git/remove-location", null);
            File.Delete(script);
        }
    }

    [Fact]
    public async Task Located_component_version_is_null_not_fabricated_when_the_file_is_not_a_runnable_program()
    {
        string fakeInstall = Path.Combine(Path.GetTempPath(), "parux-not-runnable-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(fakeInstall, "not a real executable, just a placeholder");

        using HttpClient client = AuthedClient();
        try
        {
            HttpResponseMessage locateResponse = await client.PostAsJsonAsync(
                "/api/v1/runtime/components/git/locate", new ComponentLocateRequest { Path = fakeInstall });
            Assert.Equal(HttpStatusCode.OK, locateResponse.StatusCode);

            ComponentStatus[]? components = await (await client.GetAsync("/api/v1/runtime/components"))
                .Content.ReadFromJsonAsync<ComponentStatus[]>();
            ComponentStatus git = components!.First(c => c.Id == "git");
            Assert.True(git.Detected);
            Assert.Null(git.Version);
        }
        finally
        {
            await client.PostAsync("/api/v1/runtime/components/git/remove-location", null);
            File.Delete(fakeInstall);
        }
    }

    [Fact]
    public async Task Locate_endpoint_for_comfyui_rejects_missing_token()
    {
        using HttpClient unauthenticated = _factory.CreateClient();
        HttpResponseMessage response = await unauthenticated.PostAsJsonAsync(
            "/api/v1/runtime/components/comfyui/locate", new ComponentLocateRequest { Path = "/anything" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
