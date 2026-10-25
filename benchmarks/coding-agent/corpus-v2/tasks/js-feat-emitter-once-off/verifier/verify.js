"use strict";
const assert = require("node:assert");
const path = require("node:path");
const SRC = path.join(__dirname, "..", "src");
const load = (n) => require(path.join(SRC, n));
let failed = 0;
function t(name, fn) { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
async function ta(name, fn) { try { await fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
function done() { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
const { Emitter } = load("emitter.js");
t("on/emit", () => { const e = new Emitter(); const got = []; e.on("x", (v) => got.push(v)); assert.strictEqual(e.emit("x", 1), true); assert.deepStrictEqual(got, [1]); });
t("emit false when none", () => assert.strictEqual(new Emitter().emit("nope"), false));
t("once", () => { const e = new Emitter(); let n = 0; e.once("x", () => n++); e.emit("x"); e.emit("x"); assert.strictEqual(n, 1); });
t("off", () => { const e = new Emitter(); let n = 0; const f = () => n++; e.on("x", f); e.off("x", f); assert.strictEqual(e.emit("x"), false); assert.strictEqual(n, 0); });
t("off unknown", () => { const e = new Emitter(); e.off("x", () => {}); e.on("x", () => {}); e.off("x", () => {}); assert.strictEqual(e.emit("x"), true); });
t("self removal during emit", () => { const e = new Emitter(); const order = []; const a = () => { order.push("a"); e.off("x", a); }; e.on("x", a); e.on("x", () => order.push("b")); e.emit("x"); e.emit("x"); assert.deepStrictEqual(order, ["a", "b", "b"]); });
t("off removes once wrapper", () => { const e = new Emitter(); let n = 0; const f = () => n++; e.once("x", f); e.off("x", f); e.emit("x"); assert.strictEqual(n, 0); });
t("chaining", () => { const e = new Emitter(); assert.strictEqual(e.on("a", () => {}), e); assert.strictEqual(e.once("a", () => {}), e); assert.strictEqual(e.off("a", () => {}), e); });
done();
