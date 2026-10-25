"use strict";
const assert = require("node:assert");
const path = require("node:path");
const SRC = path.join(__dirname, "..", "src");
const load = (n) => require(path.join(SRC, n));
let failed = 0;
function t(name, fn) { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
async function ta(name, fn) { try { await fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
function done() { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
const { createDebouncer } = load("debounce.js");
function fakeTimers() {
  const pending = new Map(); let id = 0; const cleared = [];
  return {
    pending, cleared,
    setTimeout(cb, ms) { id++; pending.set(id, { cb, ms }); return id; },
    clearTimeout(h) { cleared.push(h); pending.delete(h); },
    flush() { const all = [...pending.values()]; pending.clear(); all.forEach((p) => p.cb()); },
  };
}
t("last args win", () => {
  const timers = fakeTimers(); const calls = [];
  const d = createDebouncer((...a) => calls.push(a), 50, timers);
  d(1); d(2); d(3);
  timers.flush();
  assert.deepStrictEqual(calls, [[3]]);
});
t("only one timer pending", () => {
  const timers = fakeTimers(); const d = createDebouncer(() => {}, 50, timers);
  d(); d(); d();
  assert.strictEqual(timers.pending.size, 1);
  assert.strictEqual(timers.cleared.length, 2);
});
t("wait forwarded", () => {
  const timers = fakeTimers(); const d = createDebouncer(() => {}, 77, timers);
  d(); assert.strictEqual([...timers.pending.values()][0].ms, 77);
});
t("fires again after flush", () => {
  const timers = fakeTimers(); const calls = [];
  const d = createDebouncer((x) => calls.push(x), 5, timers);
  d("a"); timers.flush(); d("b"); timers.flush();
  assert.deepStrictEqual(calls, ["a", "b"]);
});
done();
