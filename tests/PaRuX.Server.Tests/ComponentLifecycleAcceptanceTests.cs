using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Chained, non-hardware acceptance coverage for the first-run Detect / Locate / Install / Skip
/// contract. Point tests cover the individual endpoints elsewhere; this class protects the user
/// journey and the ownership boundary across several consecutive actions.
///
/// The test deliberately empties PATH while it runs so a normally system-detected executable can
/// represent a clean-machine missing component deterministically. Process environment mutation is
/// isolated from parallel tests by the collection below.
/// </summary>
[Collection("ProcessEnvironmentSerial")]
public sealed class ComponentLifecycleAcceptanceTests : IClassFixture<ApiSecurityTests.PaRuXFactory>
{
    private readonly ApiSecurityTests.PaRuXFactory _factory;

    public ComponentLifecycleAcceptanceTests(ApiSecurityTests.PaRuXFactory factory) => _factory = factory;

    [Fact]
    public async Task Missing_component_can_be_skipped_reenabled_located_and_forgotten_without_ownership_takeover()
    {
        // "git" (not "python"/"uv") because managed Python/uv are deliberately excluded from the
        // generic Locate/Skip lifecycle this test exercises - see ComponentInventoryService's
        // LocatableComponents/SkippableComponentIds and the PARUX_MANAGED-only invariant.
        const string id = "git";
        string originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string selected = Path.Combine(Path.GetTempPath(), "parux-user-git-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(selected, "not executable: identity probing must remain honest");

        using HttpClient client = _factory.CreateClient();
        ServerState state = _factory.Services.GetRequiredService<ServerState>();
        client.DefaultRequestHeaders.Add("X-PaRuX-Token", state.Token);

        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);

            // Normalize any state left by a developer's previous local run. Neither endpoint
            // installs/deletes software; remove-location only forgets PaRuX's stored pointer.
            await client.PostAsync($"/api/v1/runtime/components/{id}/remove-location", null);
            await client.PostAsync($"/api/v1/runtime/components/{id}/unskip", null);

            ComponentStatus clean = await GetAsync(client, id);
            Assert.False(clean.Detected);
            Assert.False(clean.Skipped);
            Assert.Equal(ComponentOwnership.SYSTEM_DETECTED, clean.Ownership);
            Assert.Equal("missing", clean.Health);
            Assert.Contains("locate", clean.Actions);
            Assert.Contains("skip", clean.Actions);

            HttpResponseMessage skipResponse = await client.PostAsync($"/api/v1/runtime/components/{id}/skip", null);
            Assert.Equal(HttpStatusCode.OK, skipResponse.StatusCode);
            ComponentStatus skipped = (await skipResponse.Content.ReadFromJsonAsync<ComponentStatus>())!;
            Assert.True(skipped.Skipped);
            Assert.False(skipped.Detected);
            Assert.Contains("unskip", skipped.Actions);
            Assert.DoesNotContain("install", skipped.Actions);

            // Re-query rather than trusting the mutation response: Skip must be persisted and
            // observable through normal inventory discovery.
            ComponentStatus skippedAfterRefresh = await GetAsync(client, id);
            Assert.True(skippedAfterRefresh.Skipped);

            HttpResponseMessage unskipResponse = await client.PostAsync($"/api/v1/runtime/components/{id}/unskip", null);
            Assert.Equal(HttpStatusCode.OK, unskipResponse.StatusCode);
            ComponentStatus unskipped = (await unskipResponse.Content.ReadFromJsonAsync<ComponentStatus>())!;
            Assert.False(unskipped.Skipped);
            Assert.Contains("skip", unskipped.Actions);

            HttpResponseMessage locateResponse = await client.PostAsJsonAsync(
                $"/api/v1/runtime/components/{id}/locate",
                new ComponentLocateRequest { Path = selected });
            Assert.Equal(HttpStatusCode.OK, locateResponse.StatusCode);
            ComponentStatus located = (await locateResponse.Content.ReadFromJsonAsync<ComponentStatus>())!;
            Assert.True(located.Detected);
            Assert.Equal(ComponentOwnership.USER_MANAGED, located.Ownership);
            Assert.Equal(Path.GetFullPath(selected), located.Path);
            Assert.Contains("relocate", located.Actions);
            Assert.Contains("remove", located.Actions);
            Assert.DoesNotContain("install", located.Actions);
            Assert.DoesNotContain("rollback", located.Actions);
            Assert.DoesNotContain("repair", located.Actions);

            // A non-runnable user-selected file is not fabricated as validated just because it
            // exists; USER_MANAGED means PaRuX records the pointer but does not claim ownership or
            // identity it could not prove.
            Assert.Equal("unverified", located.ValidationStatus);

            ComponentStatus persistedLocation = await GetAsync(client, id);
            Assert.Equal(ComponentOwnership.USER_MANAGED, persistedLocation.Ownership);
            Assert.Equal(Path.GetFullPath(selected), persistedLocation.Path);

            HttpResponseMessage removeResponse = await client.PostAsync(
                $"/api/v1/runtime/components/{id}/remove-location", null);
            Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);

            ComponentStatus afterRemove = await GetAsync(client, id);
            Assert.False(afterRemove.Detected);
            Assert.Equal(ComponentOwnership.SYSTEM_DETECTED, afterRemove.Ownership);
            Assert.Null(afterRemove.Path);
            Assert.Contains("locate", afterRemove.Actions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { await client.PostAsync($"/api/v1/runtime/components/{id}/unskip", null); } catch { }
            try { await client.PostAsync($"/api/v1/runtime/components/{id}/remove-location", null); } catch { }
            try { File.Delete(selected); } catch { }
        }
    }

    private static async Task<ComponentStatus> GetAsync(HttpClient client, string id)
    {
        HttpResponseMessage response = await client.GetAsync("/api/v1/runtime/components");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<ComponentStatus> components = await response.Content.ReadFromJsonAsync<List<ComponentStatus>>() ?? new();
        return Assert.Single(components.Where(c => c.Id == id));
    }
}

[CollectionDefinition("ProcessEnvironmentSerial", DisableParallelization = true)]
public sealed class ProcessEnvironmentSerialCollection
{
}
