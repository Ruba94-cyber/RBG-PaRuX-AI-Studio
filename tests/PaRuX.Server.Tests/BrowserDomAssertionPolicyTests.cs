using System;
using System.Text.Json;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class BrowserDomAssertionPolicyTests
{
    [Theory]
    [InlineData("exists", null, true)]
    [InlineData("exists", "", true)]
    [InlineData("exists", "unnecessary-secret", false)]
    [InlineData("visible", "unnecessary-secret", false)]
    [InlineData("absent", null, true)]
    [InlineData("visible", null, true)]
    [InlineData("hidden", null, true)]
    [InlineData("text_equals", "", true)]
    [InlineData("value_equals", "hello", true)]
    [InlineData("contains", "hello", false)]
    [InlineData("javascript", "hello", false)]
    [InlineData("", null, false)]
    [InlineData("text_equals", null, false)]
    [InlineData("value_equals", null, false)]
    public void Supported_conditions_require_an_exact_known_target(
        string mode, string? expected, bool valid)
    {
        Assert.Equal(valid, BrowserDomAssertionPolicy.IsValid("#status", mode, expected));
    }

    [Fact]
    public void Selector_and_expected_value_are_escaped_as_JavaScript_literals()
    {
        const string selector = "#x\"; globalThis.hacked=true; //";
        const string expected = "quote \" and newline\n with </script> and token=XYZ";
        string js = BrowserDomAssertionPolicy.BuildScript(selector, "text_equals", expected);
        Assert.Contains(JsonSerializer.Serialize(selector), js);
        Assert.Contains(JsonSerializer.Serialize(expected), js);
        Assert.Contains("document.querySelector(selector)", js);
        Assert.Contains("actual===expected", js);
        Assert.DoesNotContain("return {value:", js);
        Assert.DoesNotContain("console.log", js);
        Assert.Contains("return report(ok,ok?'passed':'mismatch')", js);
    }

    [Fact]
    public void Credentials_are_never_read_or_reflected_by_equality_assertions()
    {
        string js = BrowserDomAssertionPolicy.BuildScript("#password", "value_equals", "something");
        Assert.Contains("kind==='password'", js);
        Assert.Contains("!['text','search','email','url','tel','number'].includes(kind)", js);
        Assert.Contains("return report(false,'restricted')", js);
        Assert.Contains("return report(false,'unsupported')", js);
        Assert.DoesNotContain("return report(ok,actual)", js);
        Assert.DoesNotContain("return actual", js);
    }

    [Fact]
    public void Invalid_parameters_and_oversized_expectations_fail_before_browser_execution()
    {
        Assert.False(BrowserDomAssertionPolicy.IsValid(" ", "exists", null));
        Assert.False(BrowserDomAssertionPolicy.IsValid("#x", "value_equals",
            new string('A', BrowserDomAssertionPolicy.MaximumExpectedCharacters + 1)));
        Assert.Throws<ArgumentException>(() =>
            BrowserDomAssertionPolicy.BuildScript("#x", "untrusted", "expected"));
    }

    [Fact]
    public void Visibility_is_computed_from_actual_layout_rather_than_only_element_presence()
    {
        string js = BrowserDomAssertionPolicy.BuildScript("#menu", "visible", null);
        Assert.Contains("getBoundingClientRect()", js);
        Assert.Contains("getClientRects().length>0", js);
        Assert.Contains("getComputedStyle(el)", js);
        Assert.Contains("el.closest('[hidden],[inert]')", js);
    }
}

public sealed class BrowserDomAssertionResultPolicyTests
{
    [Theory]
    [InlineData("{\"result\":{\"value\":{\"kind\":\"parux-dom-assertion\",\"ok\":true,\"status\":\"passed\"}}}", true, false)]
    [InlineData("{\"result\":{\"value\":{\"kind\":\"parux-dom-assertion\",\"ok\":false,\"status\":\"missing\"}}}", false, true)]
    [InlineData("{\"result\":{\"value\":{\"kind\":\"parux-dom-assertion\",\"ok\":false,\"status\":\"mismatch\"}}}", false, true)]
    [InlineData("{\"result\":{\"value\":{\"kind\":\"parux-dom-assertion\",\"ok\":false,\"status\":\"restricted\"}}}", false, false)]
    [InlineData("{\"result\":{\"value\":{\"kind\":\"parux-dom-assertion\",\"ok\":false,\"status\":\"unsupported\"}}}", false, false)]
    [InlineData("{\"result\":{\"value\":{\"kind\":\"parux-dom-assertion\",\"ok\":false,\"status\":\"unavailable\"}}}", false, false)]
    [InlineData("{\"result\":{\"value\":{\"kind\":\"parux-dom-assertion\",\"ok\":true,\"status\":\"mismatch\"}}}", false, false)]
    [InlineData("{\"result\":{\"value\":{\"kind\":\"other\",\"ok\":true,\"status\":\"passed\"}}}", false, false)]
    [InlineData("{\"result\":{\"value\":{\"kind\":\"parux-dom-assertion\",\"ok\":\"true\",\"status\":\"passed\"}}}", false, false)]
    [InlineData("{\"result\":{\"value\":{\"kind\":\"parux-dom-assertion\",\"ok\":false,\"status\":\"token=SECRET\"}}}", false, false)]
    [InlineData("{\"exceptionDetails\":{\"text\":\"password=SECRET\"},\"result\":{\"value\":{\"kind\":\"parux-dom-assertion\",\"ok\":true,\"status\":\"passed\"}}}", false, false)]
    [InlineData("{}", false, false)]
    public void Only_valid_assertion_envelopes_produce_success_and_only_transient_mismatches_retry(
        string json, bool success, bool retryable)
    {
        using var doc = JsonDocument.Parse(json);
        var decision = BrowserDomAssertionResultPolicy.Classify(doc.RootElement);
        Assert.Equal(success, decision.Success);
        Assert.Equal(retryable, decision.Retryable);
        string observation = decision.Observation + decision.Error;
        Assert.DoesNotContain("SECRET", observation);
        Assert.DoesNotContain("token=", observation);
        Assert.DoesNotContain("password=", observation);
        if (success) Assert.Contains("PASS", observation);
    }

    [Fact]
    public void Missing_or_malformed_response_never_claims_verified_state()
    {
        using var doc = JsonDocument.Parse("{\"result\":{\"type\":\"undefined\"}}");
        var decision = BrowserDomAssertionResultPolicy.Classify(doc.RootElement);
        Assert.False(decision.Success);
        Assert.False(decision.Retryable);
    }
}
