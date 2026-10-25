using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserDomActionScriptBuilderTests
{
    [Fact]
    public void Typing_serializes_selector_and_value_as_literals_without_reflecting_field_content()
    {
        const string selector = "#search\"\\\\field";
        const string value = "text with \"quotes\" and newline\nPASSWORD_SHOULD_NOT_BE_IN_EVIDENCE";
        string script = BrowserDomActionScriptBuilder.BuildType(selector, value);

        Assert.Contains(JsonSerializer.Serialize(selector), script);
        Assert.Contains(JsonSerializer.Serialize(value), script);
        Assert.DoesNotContain("return {ok:true,value:", script);
        Assert.DoesNotContain("el.value??", script);
        Assert.Contains("return {ok:true,tag:tag}", script);
    }

    [Fact]
    public void Click_never_reflects_user_page_text_in_its_result()
    {
        string script = BrowserDomActionScriptBuilder.BuildClick("button[data-secret=\"x\"]");

        Assert.Contains("document.querySelector", script);
        Assert.Contains("el.click()", script);
        Assert.DoesNotContain("innerText", script);
        Assert.DoesNotContain("textContent||", script);
        Assert.Contains("return {ok:true,tag:el.tagName}", script);
    }

    [Fact]
    public void Typing_refuses_password_and_credential_fields_and_non_editable_targets()
    {
        string script = BrowserDomActionScriptBuilder.BuildType("#input", "abc");

        Assert.Contains("kind==='password'", script);
        Assert.Contains("sensitive field requires user-owned input", script);
        Assert.Contains("target is not an editable form control", script);
        Assert.Contains("unsupported input type", script);
        Assert.Contains("browser rejected input value", script);
        Assert.Contains("page did not retain input value", script);
        Assert.Contains("el.readOnly", script);
    }

    [Fact]
    public void Typing_handles_only_supported_native_setters_select_and_content_editable()
    {
        string script = BrowserDomActionScriptBuilder.BuildType("#value", "alpha");

        Assert.Contains("HTMLInputElement.prototype", script);
        Assert.Contains("HTMLTextAreaElement.prototype", script);
        Assert.Contains("tag==='SELECT'", script);
        Assert.Contains("el.isContentEditable", script);
        Assert.Contains("new Event('input',{bubbles:true})", script);
        Assert.Contains("new Event('change',{bubbles:true})", script);
    }
}

public sealed class BrowserDomActionResultPolicyTests
{
    [Theory]
    [InlineData("{\"result\":{\"value\":{\"ok\":true,\"tag\":\"INPUT\",\"value\":\"never echo\"}}}", true)]
    [InlineData("{\"result\":{\"value\":{\"ok\":false,\"error\":\"selector not found\"}}}", false)]
    [InlineData("{\"result\":{\"value\":{\"ok\":false,\"error\":\"malicious page <script>\"}}}", false)]
    [InlineData("{\"result\":{\"value\":{\"ok\":\"true\"}}}", false)]
    [InlineData("{\"result\":{\"value\":{\"answer\":42}}}", false)]
    [InlineData("{\"result\":{\"type\":\"string\",\"value\":\"user-secret\"}}", false)]
    [InlineData("{\"exceptionDetails\":{\"text\":\"api_key=abc\"},\"result\":{\"value\":{\"ok\":true}}}", false)]
    [InlineData("{}", false)]
    public void Only_explicit_success_is_accepted_and_private_values_never_leave_the_result_policy(
        string json, bool expected)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        BrowserDomActionResultPolicy.Decision outcome = BrowserDomActionResultPolicy.Classify(doc.RootElement);

        Assert.Equal(expected, outcome.Success);
        string displayed = outcome.Observation + outcome.Error;
        Assert.DoesNotContain("never echo", displayed);
        Assert.DoesNotContain("malicious page", displayed);
        Assert.DoesNotContain("user-secret", displayed);
        Assert.DoesNotContain("api_key", displayed);
        if (expected) Assert.Contains("Inspect", displayed);
    }

    [Theory]
    [InlineData("selector not found", "selector not found")]
    [InlineData("target disabled or inert", "target disabled or inert")]
    [InlineData("page did not retain input value", "page did not retain input value")]
    public void Known_UI_action_denials_are_reported_without_reflecting_values(
        string reason, string expected)
    {
        using JsonDocument doc = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                result = new
                {
                    value = new { ok = false, error = reason, value = "PRIVATE_FORM_VALUE" }
                }
            }));
        BrowserDomActionResultPolicy.Decision decision =
            BrowserDomActionResultPolicy.Classify(doc.RootElement);
        Assert.False(decision.Success);
        Assert.Contains(expected, decision.Error);
        Assert.DoesNotContain("PRIVATE_FORM_VALUE", decision.Error);
    }

    [Fact]
    public void Denied_credential_input_does_not_reveal_its_value()
    {
        using JsonDocument doc = JsonDocument.Parse(
            "{\"result\":{\"value\":{\"ok\":false,\"error\":\"sensitive field requires user-owned input\",\"value\":\"secret-token\"}}}");
        BrowserDomActionResultPolicy.Decision decision = BrowserDomActionResultPolicy.Classify(doc.RootElement);
        Assert.False(decision.Success);
        Assert.Contains("sensitive field requires user-owned input", decision.Error);
        Assert.DoesNotContain("secret-token", decision.Error);
    }
}
