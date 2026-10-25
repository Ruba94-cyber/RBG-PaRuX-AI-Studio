using System;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// Builds fixed, bounded browser UI interaction scripts. Only the selector
    /// and proposed value are serialized as JavaScript string literals. The
    /// returned result never echoes page text or typed values into task evidence.
    /// These actions remain untrusted until a separate DOM/UI assertion verifies
    /// the requested postcondition; they never grant browser navigation authority.
    /// </summary>
    public static class BrowserDomActionScriptBuilder
    {
        public static string BuildClick(string? selector)
        {
            string target = JsonSerializer.Serialize(selector ?? string.Empty);
            return "(() => {"
                + "const el=document.querySelector(" + target + ");"
                + "if(!el) return {ok:false,error:'selector not found'};"
                + "if(el.matches(':disabled') || el.closest('[inert]')) return {ok:false,error:'target disabled or inert'};"
                + "el.scrollIntoView({block:'center',inline:'center'});"
                + "el.click();"
                + "return {ok:true,tag:el.tagName};"
                + "})()";
        }

        public static string BuildType(string? selector, string? value)
        {
            string target = JsonSerializer.Serialize(selector ?? string.Empty);
            string payload = JsonSerializer.Serialize(value ?? string.Empty);
            return "(() => {"
                + "const el=document.querySelector(" + target + ");"
                + "if(!el) return {ok:false,error:'selector not found'};"
                + "if(el.matches(':disabled') || el.closest('[inert]') || el.readOnly)"
                + " return {ok:false,error:'target disabled, inert or read-only'};"
                + "const tag=el.tagName;"
                + "const kind=tag==='INPUT'?(el.type||'text').toLowerCase():'';"
                + "const hint=[el.name||'',el.id||'',el.autocomplete||''].join(' ').toLowerCase();"
                + "if(kind==='password' || /(?:password|passwd|passcode|otp|one-time-code|secret|api[-_]?key|access[-_]?token|cvv|cvc|cc-|credit[-_ ]?card|ssn)/i.test(hint))"
                + " return {ok:false,error:'sensitive field requires user-owned input'};"
                + "const value=" + payload + ";"
                + "if(tag==='INPUT'){"
                + "if(!['text','search','email','url','tel','number'].includes(kind))"
                + " return {ok:false,error:'unsupported input type'};"
                + "const setter=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value')?.set;"
                + "if(!setter) return {ok:false,error:'input value setter unavailable'};"
                + "setter.call(el,value);"
                + "if(el.value!==value) return {ok:false,error:'browser rejected input value'};"
                + "}else if(tag==='TEXTAREA'){"
                + "const setter=Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value')?.set;"
                + "if(!setter) return {ok:false,error:'textarea value setter unavailable'};"
                + "setter.call(el,value);"
                + "}else if(tag==='SELECT'){"
                + "const option=Array.from(el.options).find(o=>o.value===value && !o.disabled);"
                + "if(!option) return {ok:false,error:'select option unavailable'};"
                + "el.value=option.value;"
                + "}else if(el.isContentEditable){"
                + "el.textContent=value;"
                + "}else return {ok:false,error:'target is not an editable form control'};"
                + "el.focus();"
                + "el.dispatchEvent(new Event('input',{bubbles:true}));"
                + "el.dispatchEvent(new Event('change',{bubbles:true}));"
                + "const observed=el.isContentEditable?el.textContent:el.value;"
                + "if(observed!==value) return {ok:false,error:'page did not retain input value'};"
                + "return {ok:true,tag:tag};"
                + "})()";
        }
    }
}
