using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class GamingProfileEditTests
{
    private static GamingProfileDto Profile(string id, string name, string policy = "auto", int grace = 30) => new()
    {
        Id = id,
        Name = name,
        QuickResumePolicy = policy,
        GracePeriodSeconds = grace,
        TriggerSettings = new Dictionary<string, string> { ["hotkey"] = "F12" },
        StartupActions = new List<string> { "start-overlay" }
    };

    [Fact]
    public void Save_target_is_the_profile_picked_in_the_selector_not_the_first_or_active_one()
    {
        var profiles = new List<GamingProfileDto> { Profile("a", "Active"), Profile("b", "Picked") };

        GamingProfileDto? target = GamingProfileEdit.SelectTarget(profiles, "b");

        Assert.NotNull(target);
        Assert.Equal("b", target!.Id);
    }

    [Fact]
    public void Save_target_is_refused_when_nothing_was_picked_or_the_picked_profile_is_gone()
    {
        var profiles = new List<GamingProfileDto> { Profile("a", "Active") };

        // No fallback to the first profile: a save must never land on a profile the user did not pick.
        Assert.Null(GamingProfileEdit.SelectTarget(profiles, null));
        Assert.Null(GamingProfileEdit.SelectTarget(profiles, string.Empty));
        Assert.Null(GamingProfileEdit.SelectTarget(profiles, "deleted-profile"));
    }

    [Fact]
    public void Untouched_grace_period_keeps_the_stored_value_instead_of_the_editor_default()
    {
        GamingProfileDto stored = Profile("a", "Competitive", policy: "always", grace: 15);

        GamingProfileDto patched = GamingProfileEdit.Apply(stored, quickResumePolicy: null, graceSeconds: null);

        Assert.Equal(15, patched.GracePeriodSeconds);
        Assert.Equal("always", patched.QuickResumePolicy);
    }

    [Fact]
    public void Edited_policy_and_grace_replace_only_those_two_fields()
    {
        GamingProfileDto stored = Profile("a", "Competitive", policy: "auto", grace: 15);

        GamingProfileDto patched = GamingProfileEdit.Apply(stored, "off", 45);

        Assert.Equal("off", patched.QuickResumePolicy);
        Assert.Equal(45, patched.GracePeriodSeconds);
        Assert.Equal("a", patched.Id);
        Assert.Equal("Competitive", patched.Name);
        Assert.Equal("F12", patched.TriggerSettings["hotkey"]);
        Assert.Equal(new[] { "start-overlay" }, patched.StartupActions);
    }

    [Fact]
    public void Unknown_policy_keeps_the_stored_policy()
    {
        GamingProfileDto stored = Profile("a", "Competitive", policy: "always");

        GamingProfileDto patched = GamingProfileEdit.Apply(stored, "sometimes", null);

        Assert.Equal("always", patched.QuickResumePolicy);
    }

    [Fact]
    public void Apply_never_changes_the_stored_profile_the_editor_was_loaded_from()
    {
        GamingProfileDto stored = Profile("a", "Competitive", policy: "auto", grace: 15);

        GamingProfileDto patched = GamingProfileEdit.Apply(stored, "off", 60);
        patched.TriggerSettings["hotkey"] = "F1";

        Assert.Equal("auto", stored.QuickResumePolicy);
        Assert.Equal(15, stored.GracePeriodSeconds);
        Assert.Equal("F12", stored.TriggerSettings["hotkey"]);
    }

    [Fact]
    public void Fields_the_editor_changed_since_it_loaded_are_the_only_ones_sent()
    {
        var changed = GamingProfileEdit.ChangedFields("always", "auto", 45, 30);
        var unchanged = GamingProfileEdit.ChangedFields("auto", "auto", 30, 30);

        Assert.Equal("always", changed.QuickResumePolicy);
        Assert.Equal(45, changed.GracePeriodSeconds);
        Assert.Null(unchanged.QuickResumePolicy);
        Assert.Null(unchanged.GracePeriodSeconds);
    }

    [Fact]
    public void A_value_stored_after_the_editor_loaded_is_kept_when_the_editor_did_not_change_it()
    {
        GamingProfileDto stored = Profile("a", "Competitive", policy: "off", grace: 60);

        var sent = GamingProfileEdit.ChangedFields("auto", "auto", 30, 30);
        GamingProfileDto saved = GamingProfileEdit.Apply(stored, sent.QuickResumePolicy, sent.GracePeriodSeconds);

        Assert.Equal("off", saved.QuickResumePolicy);
        Assert.Equal(60, saved.GracePeriodSeconds);
    }
}
