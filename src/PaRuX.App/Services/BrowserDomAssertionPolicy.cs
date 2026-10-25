using System;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// Deterministic, read-only browser DOM assertions. The script returns only a
    /// status enum and never sends back actual text, form values or passwords.
    /// This is state evidence, not an independent code-quality verification gate.
    /// </summary>
    public static class BrowserDomAssertionPolicy
    {
        public const int MaximumExpectedCharacters = 4096;

        public static bool IsSupported(string? target) =>
            target is "exists" or "absent" or "visible" or "hidden"
                or "text_equals" or "value_equals";

        public static bool IsValid(string? selector, string? target, string? expected) =>
            !string.IsNullOrWhiteSpace(selector)
            && selector.Length <= 4096
            && IsSupported(target)
            // Non-equality assertions must not carry unrelated or potentially
            // secret input into the browser/CDP script as an ignored argument.
            && (target is "text_equals" or "value_equals"
                ? expected != null && expected.Length <= MaximumExpectedCharacters
                : string.IsNullOrEmpty(expected));

        public static string BuildScript(string selector, string target, string? expected)
        {
            if (!IsValid(selector, target, expected))
                throw new ArgumentException("Invalid browser DOM assertion.");

            string selectorLiteral = JsonSerializer.Serialize(selector);
            string targetLiteral = JsonSerializer.Serialize(target);
            string expectedLiteral = JsonSerializer.Serialize(expected ?? string.Empty);
            return "(() => {"
                + "const selector=" + selectorLiteral + ";"
                + "const mode=" + targetLiteral + ";"
                + "const expected=" + expectedLiteral + ";"
                + "const report=(ok,status)=>({kind:'parux-dom-assertion',ok,status});"
                + "try {"
                + "const el=document.querySelector(selector);"
                + "if(mode==='absent') return report(!el,el?'mismatch':'passed');"
                + "if(!el) return report(false,'missing');"
                + "if(mode==='exists') return report(true,'passed');"
                + "if(mode==='visible'||mode==='hidden'){"
                + "const rect=el.getBoundingClientRect();"
                + "const css=getComputedStyle(el);"
                + "const shown=el.getClientRects().length>0 && rect.width>0 && rect.height>0"
                + " && css.visibility==='visible' && css.display!=='none'"
                + " && Number(css.opacity)>0 && !el.closest('[hidden],[inert]');"
                + "const ok=mode==='visible'?shown:!shown;"
                + "return report(ok,ok?'passed':'mismatch');"
                + "}"
                + "const tag=el.tagName;"
                + "const kind=tag==='INPUT'?(el.type||'text').toLowerCase():'';"
                + "const hint=[el.name||'',el.id||'',el.autocomplete||''].join(' ').toLowerCase();"
                + "if(kind==='password' || /(?:password|passwd|passcode|otp|one-time-code|secret|api[-_]?key|access[-_]?token|cvv|cvc|cc-|credit[-_ ]?card|ssn)/i.test(hint))"
                + " return report(false,'restricted');"
                + "if(mode==='value_equals'){"
                + "if(tag==='INPUT' && !['text','search','email','url','tel','number'].includes(kind))"
                + " return report(false,'restricted');"
                + "if(!(['INPUT','TEXTAREA','SELECT'].includes(tag)||el.isContentEditable))"
                + " return report(false,'unsupported');"
                + "const actual=el.isContentEditable?el.textContent:el.value;"
                + "const ok=actual===expected;"
                + "return report(ok,ok?'passed':'mismatch');"
                + "}"
                + "if(mode==='text_equals'){"
                + "if(tag==='INPUT'||tag==='TEXTAREA') return report(false,'unsupported');"
                + "const actual=el.innerText??el.textContent??'';"
                + "const ok=actual===expected;"
                + "return report(ok,ok?'passed':'mismatch');"
                + "}"
                + "return report(false,'unsupported');"
                + "} catch {return report(false,'unavailable');}"
                + "})()";
        }
    }

    /// <summary>
    /// Browser page data is untrusted. Reject malformed CDP envelopes and pass
    /// only a bounded enum through to the local agent, never arbitrary page text.
    /// </summary>
    public static class BrowserDomAssertionResultPolicy
    {
        public sealed record Decision(bool Success, bool Retryable, string Observation, string? Error);

        public static Decision Classify(JsonElement cdp)
        {
            const string failure = "DOM assertion could not be verified. No browser state was accepted.";
            if (!BrowserDevToolsEvaluationPolicy.HasUsableResult(cdp)
                || !cdp.TryGetProperty("result", out JsonElement result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("value", out JsonElement value)
                || value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("kind", out JsonElement kind)
                || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != "parux-dom-assertion"
                || !value.TryGetProperty("ok", out JsonElement ok)
                || !value.TryGetProperty("status", out JsonElement status)
                || status.ValueKind != JsonValueKind.String)
                return new Decision(false, false, string.Empty, failure);

            string? state = status.GetString();
            if (ok.ValueKind == JsonValueKind.True && state == "passed")
                return new Decision(true, false,
                    "DOM assertion PASS: requested state was observed at inspection time.", null);

            if (ok.ValueKind != JsonValueKind.False)
                return new Decision(false, false, string.Empty, failure);

            return state switch
            {
                "missing" => new Decision(false, true, string.Empty,
                    "DOM assertion not satisfied: target element is absent."),
                "mismatch" => new Decision(false, true, string.Empty,
                    "DOM assertion not satisfied: observed state differs from expected state."),
                "restricted" => new Decision(false, false, string.Empty,
                    "DOM assertion denied for a sensitive credential field."),
                "unsupported" => new Decision(false, false, string.Empty,
                    "DOM assertion denied for this control or condition."),
                "unavailable" => new Decision(false, false, string.Empty,
                    "DOM assertion unavailable; browser could not inspect the page."),
                _ => new Decision(false, false, string.Empty, failure)
            };
        }
    }
}
