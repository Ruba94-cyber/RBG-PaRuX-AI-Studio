"use strict";
const assert = require("node:assert");
const path = require("node:path");
const SRC = path.join(__dirname, "..", "src");
const load = (n) => require(path.join(SRC, n));
let failed = 0;
function t(name, fn) { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
async function ta(name, fn) { try { await fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
function done() { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
const { flatten } = load("flatten.js");
t("default depth 1", () => assert.deepStrictEqual(flatten([1, [2, [3]]]), [1, 2, [3]]));
t("depth 0", () => assert.deepStrictEqual(flatten([1, [2]], 0), [1, [2]]));
t("depth 2", () => assert.deepStrictEqual(flatten([1, [2, [3, [4]]]], 2), [1, 2, 3, [4]]));
t("infinity", () => assert.deepStrictEqual(flatten([1, [2, [3, [4]]]], Infinity), [1, 2, 3, 4]));
t("no mutation", () => { const a = [1, [2]]; flatten(a); assert.deepStrictEqual(a, [1, [2]]); });
t("empty", () => assert.deepStrictEqual(flatten([]), []));
t("does not use Array.prototype.flat", () => {
  const src = require("node:fs").readFileSync(require("node:path").join(SRC, "flatten.js"), "utf8");
  assert(!/\.flat\s*\(/.test(src) && !/flatMap/.test(src));
});
done();
