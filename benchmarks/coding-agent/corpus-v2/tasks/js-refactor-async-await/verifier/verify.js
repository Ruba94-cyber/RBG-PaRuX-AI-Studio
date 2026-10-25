"use strict";
const assert = require("node:assert");
const path = require("node:path");
const SRC = path.join(__dirname, "..", "src");
const load = (n) => require(path.join(SRC, n));
let failed = 0;
function t(name, fn) { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
async function ta(name, fn) { try { await fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
function done() { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
(async () => {
const fs = require("node:fs");
const { fetchRaw, loadUser } = load("loader.js");
await ta("resolves", async () => {
  const p = loadUser(1);
  assert(p && typeof p.then === "function", "must return a promise");
  assert.deepStrictEqual(await p, { id: 1, name: "ann", plan: "pro" });
});
await ta("missing user rejects", async () => {
  await assert.rejects(() => loadUser(9), { message: "user not found: 9" });
});
await ta("unknown plan rejects", async () => {
  await assert.rejects(() => loadUser(2), { message: "unknown plan: zz" });
});
await ta("fetchRaw unchanged", async () => {
  await new Promise((res, rej) => fetchRaw(1, (e, v) => (e ? rej(e) : (assert.strictEqual(v.name, "ann"), res()))));
});
t("async function declared", () => {
  const src = fs.readFileSync(require("node:path").join(SRC, "loader.js"), "utf8");
  assert(/async function loadUser\s*\(\s*id\s*\)/.test(src), "async function loadUser(id) expected");
});
done();
})();

