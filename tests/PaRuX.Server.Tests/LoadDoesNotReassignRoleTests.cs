using System.Text.Json;
using PaRuX.Contracts;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Loading a model and choosing a role's default model are two different acts.
///
/// Observed on the physical PC (2026-09-21): a benchmark called
/// <c>POST /api/v1/models/library/Qwen3.5-4B-Q4_K_M.gguf/load</c> with <c>role: "standard"</c>
/// purely to measure that model. <c>RuntimeManagerService.LoadRoleAsync</c> persisted a role
/// override unconditionally, so from that moment a machine with an RTX 5080 and two 27B models
/// warmed a 4B as its Standard text profile on every launch - with nothing in the UI saying the
/// default had been rewritten, and no way to tell it apart from a deliberate choice.
/// </summary>
public sealed class LoadDoesNotReassignRoleTests
{
    [Fact]
    public void A_load_request_does_not_reassign_the_role_unless_it_says_so()
    {
        var request = new LoadManagedModelRequest { Role = "standard" };

        Assert.False(request.PersistRoleAssignment,
            "A bare load must stay transient; persisting is opt-in, or one benchmark silently "
            + "redefines the user's default text profile.");
    }

    /// <summary>A body that omits the field entirely - every pre-existing API caller.</summary>
    [Fact]
    public void An_omitted_flag_deserializes_as_transient()
    {
        LoadManagedModelRequest? request = JsonSerializer.Deserialize<LoadManagedModelRequest>(
            "{\"role\":\"standard\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal("standard", request!.Role);
        Assert.False(request.PersistRoleAssignment);
    }

    [Fact]
    public void An_explicit_assignment_is_still_expressible()
    {
        LoadManagedModelRequest? request = JsonSerializer.Deserialize<LoadManagedModelRequest>(
            "{\"role\":\"standard\",\"persistRoleAssignment\":true}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.True(request!.PersistRoleAssignment);
    }
}
