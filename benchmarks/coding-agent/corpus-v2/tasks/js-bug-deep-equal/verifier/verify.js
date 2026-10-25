"use strict";
const assert = require("node:assert");
const path = require("node:path");
const SRC = path.join(__dirname, "..", "src");
const load = (n) => require(path.join(SRC, n));
let failed = 0;
function t(name, fn) { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
async function ta(name, fn) { try { await fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
function done() { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
const { deepEqual } = load("deepequal.js");
t("primitives", () => { assert(deepEqual(1, 1)); assert(!deepEqual(1, 2)); assert(!deepEqual(1, "1")); });
t("nan", () => assert(deepEqual(NaN, NaN)));
t("nested", () => assert(deepEqual({ a: [1, { b: 2 }] }, { a: [1, { b: 2 }] })));
t("extra key in b", () => assert(!deepEqual({ a: 1 }, { a: 1, b: 2 })));
t("extra key in a", () => assert(!deepEqual({ a: 1, b: 2 }, { a: 1 })));
t("array vs object", () => assert(!deepEqual([1, 2], { 0: 1, 1: 2 })));
t("array length", () => assert(!deepEqual([1, 2], [1, 2, 3])));
t("dates", () => { assert(deepEqual(new Date(5), new Date(5))); assert(!deepEqual(new Date(5), new Date(6))); });
t("null", () => { assert(!deepEqual(null, {})); assert(deepEqual(null, null)); });
t("undefined key", () => assert(!deepEqual({ a: undefined }, { b: undefined })));
done();
