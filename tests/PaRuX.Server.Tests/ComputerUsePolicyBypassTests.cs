using System.Collections.Generic;
using PaRuX.Services;
using Xunit;

// Bypass review of the computer-use sensitive-action guard (83781820). A failing case is a
// confirmed bypass: the guard allows an action that it must confirm or deny.
public sealed class ComputerUsePolicyBypassTests
{
    public static IEnumerable<object[]> SensitiveLabelsThatMustConfirm()
    {
        // Invisible format characters split a word for the tokenizer but are not visible on screen.
        yield return new object[] { "Sen​d" };
        yield return new object[] { "De­lete" };
        yield return new object[] { "Pay‍ment" };
        // Fullwidth Latin and Cyrillic lookalikes render like the ASCII word.
        yield return new object[] { "Ｓｅｎｄ" };
        yield return new object[] { "Ѕеnd" };
        yield return new object[] { "Dеlete" };
        // Vocabulary gaps for the classes the guard claims to cover: send, spend, delete, permissions.
        yield return new object[] { "Checkout" };
        yield return new object[] { "Publish" };
        yield return new object[] { "Share" };
        yield return new object[] { "Upload" };
        yield return new object[] { "Transfer" };
        yield return new object[] { "Allow" };
        yield return new object[] { "Approve" };
        yield return new object[] { "Erase" };
        yield return new object[] { "Sending" };
        yield return new object[] { "Deleting" };
        // Already confirmed by the existing confirm-order rule; pins that rule.
        yield return new object[] { "Confirm order" };
    }

    [Theory]
    [MemberData(nameof(SensitiveLabelsThatMustConfirm))]
    public void Sensitive_labels_must_confirm_even_when_spelled_with_invisible_or_lookalike_characters(string label)
    {
        ComputerUseSensitiveActionPolicy.Decision decision =
            ComputerUseSensitiveActionPolicy.Evaluate("ui_invoke", label, null, "Notes");

        Assert.Equal(ComputerUseSensitiveActionPolicy.Disposition.ConfirmRequired, decision.Disposition);
        Assert.False(decision.MayExecute);
    }

    public static IEnumerable<object[]> PasswordLabelsThatMustDeny()
    {
        yield return new object[] { "Pass​word" };
        yield return new object[] { "Parola chiave" };
        yield return new object[] { "Parola d'ordine" };
        yield return new object[] { "Passwort" };
        yield return new object[] { "Contraseña" };
        yield return new object[] { "Mot de passe" };
    }

    [Theory]
    [MemberData(nameof(PasswordLabelsThatMustDeny))]
    public void Password_labels_in_other_languages_or_with_invisible_characters_are_denied(string label)
    {
        ComputerUseSensitiveActionPolicy.Decision decision =
            ComputerUseSensitiveActionPolicy.Evaluate("ui_set_value", label, "sample-value", "Notes");

        Assert.Equal(ComputerUseSensitiveActionPolicy.Disposition.Deny, decision.Disposition);
        Assert.False(decision.MayExecute);
    }

    [Fact]
    public void Long_accessible_names_are_not_dropped_before_the_sensitive_word_check()
    {
        string tree = "{\"windows\":[{\"hwnd\":17,\"elements\":[{\"selector\":\"btn-long\",\"name\":\""
            + new string('a', 300) + " Send\",\"controlType\":\"Button\"}]}]}";

        string? target = ComputerUseSensitiveActionPolicy.ResolveObservedTargetLabelOrRole(tree, "btn-long");
        ComputerUseSensitiveActionPolicy.Decision decision =
            ComputerUseSensitiveActionPolicy.Evaluate("ui_invoke", target, null, "Notes");

        Assert.Equal(ComputerUseSensitiveActionPolicy.Disposition.ConfirmRequired, decision.Disposition);
    }
}
