"use strict";
const assert = require("node:assert");
const path = require("node:path");
const SRC = path.join(__dirname, "..", "src");
const load = (n) => require(path.join(SRC, n));
let failed = 0;
function t(name, fn) { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
async function ta(name, fn) { try { await fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
function done() { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
const { parseQuery } = load("query.js");
t("basic", () => assert.deepStrictEqual(parseQuery("a=1&b=2"), { a: "1", b: "2" }));
t("leading ?", () => assert.deepStrictEqual(parseQuery("?a=1"), { a: "1" }));
t("decode", () => assert.deepStrictEqual(parseQuery("q=h%C3%A9llo%20w"), { q: "héllo w" }));
t("plus is space", () => assert.deepStrictEqual(parseQuery("q=a+b"), { q: "a b" }));
t("repeat", () => assert.deepStrictEqual(parseQuery("t=1&t=2&t=3"), { t: ["1", "2", "3"] }));
t("no equals", () => assert.deepStrictEqual(parseQuery("flag&x=1"), { flag: "", x: "1" }));
t("equals in value", () => assert.deepStrictEqual(parseQuery("e=a=b"), { e: "a=b" }));
t("empty", () => assert.deepStrictEqual(parseQuery(""), {}));
t("encoded key", () => assert.deepStrictEqual(parseQuery("a%20b=1"), { "a b": "1" }));
done();
