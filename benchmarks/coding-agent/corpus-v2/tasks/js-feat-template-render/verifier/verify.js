"use strict";
const assert = require("node:assert");
const path = require("node:path");
const SRC = path.join(__dirname, "..", "src");
const load = (n) => require(path.join(SRC, n));
let failed = 0;
function t(name, fn) { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
async function ta(name, fn) { try { await fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
function done() { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
const { render } = load("render.js");
t("simple", () => assert.strictEqual(render("Hi {{name}}", { name: "Ann" }), "Hi Ann"));
t("path", () => assert.strictEqual(render("{{user.name}}", { user: { name: "Bo" } }), "Bo"));
t("deep path missing", () => assert.strictEqual(render("[{{a.b.c}}]", { a: {} }), "[]"));
t("fallback", () => assert.strictEqual(render("{{nick|friend}}", {}), "friend"));
t("fallback null", () => assert.strictEqual(render("{{nick|friend}}", { nick: null }), "friend"));
t("fallback unused", () => assert.strictEqual(render("{{nick|friend}}", { nick: "Z" }), "Z"));
t("zero is a value", () => assert.strictEqual(render("{{n|none}}", { n: 0 }), "0"));
t("escape", () => assert.strictEqual(render("{{x}}", { x: '<a href="1">&</a>' }), "&lt;a href=&quot;1&quot;&gt;&amp;&lt;/a&gt;"));
t("unknown empty", () => assert.strictEqual(render("a{{zzz}}b", {}), "ab"));
t("spaces", () => assert.strictEqual(render("{{ name }}", { name: "Q" }), "Q"));
t("multiple", () => assert.strictEqual(render("{{a}}-{{b}}-{{a}}", { a: 1, b: 2 }), "1-2-1"));
done();
