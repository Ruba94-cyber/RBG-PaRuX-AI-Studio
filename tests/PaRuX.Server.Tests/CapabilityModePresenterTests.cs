using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CapabilityModePresenterTests
{
    [Fact]
    public void Automation_ids_use_the_exact_routing_cap_names()
    {
        Assert.Equal("routing.cap.image.generate.mode", CapabilityModePresenter.ModeAutomationId("image.generate"));
        Assert.Equal("routing.cap.image.edit.model", CapabilityModePresenter.ModelAutomationId("image.edit"));
        Assert.Equal("routing.cap.video.generate.trace", CapabilityModePresenter.TraceAutomationId("video.generate"));
    }

    [Fact]
    public void Lists_all_seven_capabilities_and_three_modes()
    {
        Assert.Equal(7, CapabilityModePresenter.Capabilities.Select(c => c.Capability).Distinct().Count());
        Assert.Equal(new[] { "automatic", "prefer", "force" }, CapabilityModePresenter.ModeOptions.Select(m => m.Mode));
    }

    [Fact]
    public void Model_picker_is_enabled_only_for_prefer_and_force()
    {
        Assert.False(CapabilityModePresenter.Present("coding", "automatic", "x.gguf", null).ModelEnabled);
        Assert.True(CapabilityModePresenter.Present("coding", "prefer", null, null).ModelEnabled);
        Assert.True(CapabilityModePresenter.Present("coding", "force", null, null).ModelEnabled);
        Assert.Null(CapabilityModePresenter.Present("coding", "automatic", "x.gguf", null).ModelId);
    }

    [Fact]
    public void Prefer_and_force_cannot_be_saved_without_a_model()
    {
        Assert.False(CapabilityModePresenter.Present("coding", "force", null, null).CanSave);
        Assert.Throws<InvalidOperationException>(() => CapabilityModePresenter.BuildRequest("prefer", " "));
        Assert.True(CapabilityModePresenter.Present("coding", "force", "m.gguf", null).CanSave);
    }

    [Fact]
    public void Request_for_automatic_carries_no_model_and_unknown_modes_normalize_to_automatic()
    {
        SetCapabilityModeRequest r = CapabilityModePresenter.BuildRequest("weird", "m.gguf");
        Assert.Equal("automatic", r.Mode);
        Assert.Null(r.ModelId);
        Assert.Equal("force", CapabilityModePresenter.BuildRequest("FORCE", " m.gguf ").Mode);
    }

    [Fact]
    public void Trace_shows_the_server_explanation_refusal_and_last_job()
    {
        var status = new CapabilityRoutingStatusDto
        {
            Capability = "image.generate", Mode = "force", ModelId = "a.safetensors", State = "refused",
            RefusalCode = "wrong_family", Explanation = "Forced checkpoint cannot be used.",
            Trace = { "force_refused:wrong_family:no_substitution" },
            LastJobExplanation = "Prefer fell back.", LastJobTrace = { "fallback:automatic" }
        };

        CapabilityModeRowPresentation p = CapabilityModePresenter.Present("image.generate", "force", "a.safetensors", status);

        Assert.Equal("refused", p.State);
        Assert.Contains("Forced checkpoint cannot be used.", p.TraceText);
        Assert.Contains("force_refused:wrong_family:no_substitution", p.TraceText);
        Assert.Contains("Last job: Prefer fell back.", p.TraceText);
    }

    [Fact]
    public void Unsaved_selection_is_labelled_pending_not_applied()
    {
        var stale = new CapabilityRoutingStatusDto { Capability = "coding", Mode = "automatic", Explanation = "Automatic." };
        string text = CapabilityModePresenter.Present("coding", "force", "m.gguf", stale).TraceText;
        Assert.StartsWith("Pending save", text);
    }
}
