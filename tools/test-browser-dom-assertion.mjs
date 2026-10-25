// Dependency-free behavior test for the exact JavaScript assembled by the
// C# BrowserDomAssertionPolicy.BuildScript implementation. This does not
// imitate the C# policy by rewriting JS: it extracts the literal segments
// from the authoritative source and substitutes only the three serialized
// parameter literals. It is a hermetic DOM-behavior smoke, not real CDP QA.
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { runInNewContext } from "node:vm";
import assert from "node:assert/strict";
import test from "node:test";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const path = resolve(repo, "src/PaRuX.App/Services/BrowserDomAssertionPolicy.cs");
const source = readFileSync(path, "utf8");

function buildScript(selector, target, expected = "") {
  const method = source.indexOf("public static string BuildScript(");
  const start = source.indexOf('return "(() => {"', method);
  const endMarker = '+ "})()";';
  const end = source.indexOf(endMarker, start);
  assert.ok(method >= 0 && start > method && end > start, "builder must retain its script contract");
  const expression = source.slice(start + "return ".length, end + endMarker.length);
  const fragments = expression.match(/"(?:\\.|[^"\\])*"|\b(?:selectorLiteral|targetLiteral|expectedLiteral)\b/g);
  assert.ok(fragments && fragments.length > 12, "builder must contain serialized JS segments");
  const values = {
    selectorLiteral: JSON.stringify(selector),
    targetLiteral: JSON.stringify(target),
    expectedLiteral: JSON.stringify(expected)
  };
  return fragments.map(value => value.startsWith('"') ? JSON.parse(value) : values[value]).join("");
}

function element(overrides = {}) {
  return {
    tagName: "DIV",
    innerText: "Ready",
    textContent: "Ready",
    value: "",
    name: "",
    id: "status",
    autocomplete: "",
    type: "text",
    isContentEditable: false,
    getBoundingClientRect: () => ({width: 150, height: 20}),
    getClientRects: () => [{}],
    closest: () => null,
    ...overrides
  };
}

function assertDom(selector, target, expected, el, options = {}) {
  const sandbox = {
    document: {
      querySelector: key => {
        assert.equal(key, selector);
        if (options.throwSelector) throw Error("selector syntax invalid");
        return el;
      }
    },
    getComputedStyle: () => ({
      visibility: options.visibility ?? "visible",
      display: options.display ?? "block",
      opacity: options.opacity ?? "1"
    })
  };
  const state = el == null ? null : { value: el.value, textContent: el.textContent };
  const result = runInNewContext(buildScript(selector, target, expected), sandbox, { timeout: 1000 });
  assert.equal("pwned" in sandbox, false, "selector/text injection must not execute");
  assert.equal(result.kind, "parux-dom-assertion");
  assert.equal(typeof result.ok, "boolean");
  assert.ok(["passed","mismatch","missing","restricted","unsupported","unavailable"].includes(result.status));
  if (el) {
    assert.equal(el.value, state.value, "assertion must not modify values");
    assert.equal(el.textContent, state.textContent, "assertion must not modify text");
  }
  return JSON.parse(JSON.stringify(result));
}

test("exists and absent reflect real presence", () => {
  assert.deepEqual(assertDom("#x", "exists", "", element()), {kind:"parux-dom-assertion",ok:true,status:"passed"});
  assert.equal(assertDom("#x", "exists", "", null).status, "missing");
  assert.equal(assertDom("#x", "absent", "", null).ok, true);
  assert.equal(assertDom("#x", "absent", "", element()).ok, false);
});
test("exact text and value comparisons return boolean status only", () => {
  const el = element({tagName:"TEXTAREA",value:"typed private text"});
  assert.equal(assertDom("#x", "value_equals", "typed private text", el).ok, true);
  const mismatch = assertDom("#x", "value_equals", "not correct", el);
  assert.deepEqual(mismatch, {kind:"parux-dom-assertion",ok:false,status:"mismatch"});
  const txt = assertDom("#x", "text_equals", "Ready", element());
  assert.equal(txt.ok,true);
  assert.equal(JSON.stringify(txt).includes("Ready"), false);
});
test("hidden and visible use geometry and computed style", () => {
  assert.equal(assertDom("#x", "visible", "", element()).ok, true);
  assert.equal(assertDom("#x", "hidden", "", element()).ok, false);
  assert.equal(assertDom("#x", "hidden", "", element(), {display:"none"}).ok, true);
  assert.equal(assertDom("#x", "visible", "", element({getClientRects:()=>[]})).ok, false);
});
test("credential and noneditable controls are denied", () => {
  assert.equal(assertDom("#x", "value_equals", "test", element({tagName:"INPUT",type:"password",value:"test"})).status,"restricted");
  assert.equal(assertDom("#x", "value_equals", "secret", element({tagName:"INPUT",type:"hidden",value:"secret"})).status,"restricted");
  assert.equal(assertDom("#x", "value_equals", "test", element({tagName:"INPUT",name:"api_key",value:"test"})).status,"restricted");
  assert.equal(assertDom("#x", "value_equals", "test", element()).status,"unsupported");
  assert.equal(assertDom("#x", "text_equals", "", element({tagName:"INPUT"})).status,"unsupported");
});
test("invalid selectors fail closed and never return page exception details", () => {
  const outcome = assertDom("<<<private-selector>>>", "exists", "", null, {throwSelector:true});
  assert.deepEqual(outcome, {kind:"parux-dom-assertion",ok:false,status:"unavailable"});
});
test("selectors and expected text cannot inject JavaScript into assertions", () => {
  const maliciousSelector = 'x");globalThis.pwned=true;//';
  const maliciousExpected = '");globalThis.pwned=true;//';
  const result = assertDom(maliciousSelector, "text_equals", maliciousExpected, element());
  assert.equal(result.ok, false);
  assert.equal("pwned" in globalThis, false);
});
