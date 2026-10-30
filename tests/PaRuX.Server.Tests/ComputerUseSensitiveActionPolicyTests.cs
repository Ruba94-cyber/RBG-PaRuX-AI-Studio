using System.Collections.Generic;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ComputerUseSensitiveActionPolicyTests
{
    public static IEnumerable<object[]> OrdinaryActions()
    {
        yield return new object[] { "click", "button: Open menu", "", "Notes" };
        yield return new object[] { "type", "text box: Comment", "Please review this", "Notes" };
        yield return new object[] { "ui_invoke", "button: Save draft", "", "Notes" };
        yield return new object[] { "type", "text box: Comment", "Please submit this later", "Notes" };
        yield return new object[] { "ui_invoke", "btnSaveDraft", "", "Notes" };
    }

    public static IEnumerable<object[]> ConfirmationActions()
    {
        yield return new object[] { "click", "button: Send", "", "Mail" };
        yield return new object[] { "ui_invoke", "Submit form", "", "Forms" };
        yield return new object[] { "click", "Buy now", "", "Shop" };
        yield return new object[] { "click", "Pay invoice", "", "Billing" };
        yield return new object[] { "ui_invoke", "Delete account", "", "Settings" };
        yield return new object[] { "ui_invoke", "Change permissions", "", "Settings" };
        yield return new object[] { "click", "Confirm purchase", "", "Checkout" };
        yield return new object[] { "click", "Place order", "", "Checkout" };
        yield return new object[] { "click", "Continue", "", "Checkout - Payment" };
        yield return new object[] { "confirm_purchase", "", "", "Checkout" };
        yield return new object[] { "send", "", "", "Mail" };
    }

    public static IEnumerable<object[]> PasswordLikeTargets()
    {
        yield return new object[] { "type", "password text box", "sample-value", "Notes" };
        yield return new object[] { "click", "Password text box", "", "Notes" };
        yield return new object[] { "ui_set_value", "Passcode edit", "sample-value", "Authenticator" };
        yield return new object[] { "type", "verification code", "123456", "Authenticator" };
        yield return new object[] { "type", "search box", "sample-value", "Password Manager" };
    }

    [Theory]
    [MemberData(nameof(OrdinaryActions))]
    public void Ordinary_actions_are_allowed(string kind, string target, string typedText, string appName)
    {
        ComputerUseSensitiveActionPolicy.Decision decision =
            ComputerUseSensitiveActionPolicy.Evaluate(kind, target, typedText, appName);

        Assert.Equal(ComputerUseSensitiveActionPolicy.Disposition.Allow, decision.Disposition);
        Assert.True(decision.MayExecute);
        Assert.Null(decision.Reason);
    }

    [Theory]
    [MemberData(nameof(ConfirmationActions))]
    public void Sensitive_target_labels_require_confirmation_and_cannot_execute(
        string kind, string target, string typedText, string appName)
    {
        ComputerUseSensitiveActionPolicy.Decision decision =
            ComputerUseSensitiveActionPolicy.Evaluate(kind, target, typedText, appName);

        Assert.Equal(ComputerUseSensitiveActionPolicy.Disposition.ConfirmRequired, decision.Disposition);
        Assert.False(decision.MayExecute);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
        Assert.Equal(decision, ComputerUseSensitiveActionPolicy.Evaluate(kind, target, typedText, appName));
    }

    [Theory]
    [MemberData(nameof(PasswordLikeTargets))]
    public void Password_like_targets_are_denied_before_interaction(
        string kind, string target, string typedText, string appName)
    {
        ComputerUseSensitiveActionPolicy.Decision decision =
            ComputerUseSensitiveActionPolicy.Evaluate(kind, target, typedText, appName);

        Assert.Equal(ComputerUseSensitiveActionPolicy.Disposition.Deny, decision.Disposition);
        Assert.False(decision.MayExecute);
        Assert.NotNull(decision.Reason);
        Assert.Contains("does not interact", decision.Reason);
        if (typedText.Length > 0)
            Assert.DoesNotContain(typedText, decision.Reason ?? string.Empty);
    }

    [Fact]
    public void Confirmation_refusal_says_the_action_was_not_executed_when_no_per_action_path_exists()
    {
        ComputerUseSensitiveActionPolicy.Decision decision =
            ComputerUseSensitiveActionPolicy.Evaluate("click", "Send", "", "Mail");

        string message = ComputerUseSensitiveActionPolicy.RefusalMessage(decision);

        Assert.Contains("Per-action confirmation is unavailable", message);
        Assert.Contains("nothing was executed", message);
        Assert.DoesNotContain("Mail", message);
    }

    [Fact]
    public void Reasons_do_not_echo_typed_content()
    {
        const string typedText = "sample-private-value";
        ComputerUseSensitiveActionPolicy.Decision decision =
            ComputerUseSensitiveActionPolicy.Evaluate("type", "Password field", typedText, "Notes");

        Assert.DoesNotContain(typedText, decision.Reason ?? string.Empty);
        Assert.DoesNotContain(typedText, ComputerUseSensitiveActionPolicy.RefusalMessage(decision));
    }

    [Fact]
    public void Missing_action_kind_is_denied_deterministically()
    {
        ComputerUseSensitiveActionPolicy.Decision first =
            ComputerUseSensitiveActionPolicy.Evaluate(null, "button: Save", "", "Notes");
        ComputerUseSensitiveActionPolicy.Decision second =
            ComputerUseSensitiveActionPolicy.Evaluate(null, "button: Save", "", "Notes");

        Assert.Equal(ComputerUseSensitiveActionPolicy.Disposition.Deny, first.Disposition);
        Assert.False(first.MayExecute);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("type")]
    [InlineData("key")]
    [InlineData("ui_invoke")]
    [InlineData("ui_set_value")]
    public void Activating_actions_without_a_verified_target_are_denied(string kind)
    {
        ComputerUseSensitiveActionPolicy.Decision decision =
            ComputerUseSensitiveActionPolicy.Evaluate(kind, null, "sample text", null);

        Assert.Equal(ComputerUseSensitiveActionPolicy.Disposition.Deny, decision.Disposition);
        Assert.False(decision.MayExecute);
        Assert.Contains("verified UI Automation label or role", decision.Reason);
    }

    [Fact]
    public void Observed_ui_automation_semantics_classify_a_sensitive_button()
    {
        const string tree = """
            {"windows":[{"hwnd":17,"elements":[{"selector":"btn-send","name":"Send","controlType":"Button"}]}]}
            """;

        string? target = ComputerUseSensitiveActionPolicy.ResolveObservedTargetLabelOrRole(tree, "btn-send");
        ComputerUseSensitiveActionPolicy.Decision decision =
            ComputerUseSensitiveActionPolicy.Evaluate("ui_invoke", target, null, null);

        Assert.Equal("Send Button", target);
        Assert.Equal(ComputerUseSensitiveActionPolicy.Disposition.ConfirmRequired, decision.Disposition);
        Assert.False(decision.MayExecute);
    }

    [Fact]
    public void Target_resolution_requires_one_exact_semantic_match()
    {
        const string nestedTree = """
            {"windows":[{"hwnd":17,"elements":[{"selector":"panel-a","name":"Account","children":[{"selector":"txt-email","name":"Email address","role":"text box"}]}]}]}
            """;
        const string duplicateTree = """
            {"windows":[{"hwnd":17,"elements":[{"selector":"same","name":"First"},{"selector":"same","name":"Second"}]}]}
            """;
        const string unlabeledTree = """
            {"windows":[{"hwnd":17,"elements":[{"selector":"btn-unknown"}]}]}
            """;

        Assert.Equal("Email address text box",
            ComputerUseSensitiveActionPolicy.ResolveObservedTargetLabelOrRole(nestedTree, "txt-email"));
        Assert.Null(ComputerUseSensitiveActionPolicy.ResolveObservedTargetLabelOrRole(nestedTree, "email"));
        Assert.Null(ComputerUseSensitiveActionPolicy.ResolveObservedTargetLabelOrRole(duplicateTree, "same"));
        Assert.Null(ComputerUseSensitiveActionPolicy.ResolveObservedTargetLabelOrRole(unlabeledTree, "btn-unknown"));
        Assert.Null(ComputerUseSensitiveActionPolicy.ResolveObservedTargetLabelOrRole("not json", "btn-send"));
    }
    [Fact]
    public void Sensitive_target_labels_must_come_from_the_authorized_foreground_window()
    {
        const string tree = """
        {"windows":[
          {"hwnd":1234,"elements":[{"selector":"btn-submit","name":"Save draft","controlType":"Button"}]},
          {"hwnd":5678,"elements":[{"selector":"btn-submit","name":"Send payment","controlType":"Button"}]}
        ]}
        """;

        string? foreground = ComputerUseSensitiveActionPolicy.ResolveObservedTargetLabelOrRole(
            tree, "btn-submit", 1234);
        Assert.Equal("Save draft Button", foreground);
        string? anotherWindow = ComputerUseSensitiveActionPolicy.ResolveObservedTargetLabelOrRole(
            tree, "btn-submit", 5678);
        Assert.Equal("Send payment Button", anotherWindow);
        Assert.Null(ComputerUseSensitiveActionPolicy.ResolveObservedTargetLabelOrRole(
            tree, "btn-submit", 9999));
        Assert.Equal(ComputerUseSensitiveActionPolicy.Disposition.ConfirmRequired,
            ComputerUseSensitiveActionPolicy.Evaluate("ui_invoke", anotherWindow, null, null).Disposition);
    }

}
