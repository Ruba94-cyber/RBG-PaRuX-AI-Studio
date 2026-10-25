import assert from "node:assert";
let failed = 0;
function t(name: string, fn: () => void): void { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + (e as Error).message); } }
function done(): void { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
import { readFileSync } from "node:fs";
import { shippingCost } from "../src/shipping.ts";
t("eu", () => assert.strictEqual(shippingCost("eu", 2), 8));
t("us", () => assert.strictEqual(shippingCost("us", 3), 14));
t("uk", () => assert.strictEqual(shippingCost("uk", 4), 13));
t("apac", () => assert.strictEqual(shippingCost("apac", 1), 15));
t("latam", () => assert.strictEqual(shippingCost("latam", 2), 21));
t("zero weight", () => assert.strictEqual(shippingCost("eu", 0), 5));
t("unknown", () => assert.throws(() => shippingCost("mars", 1), { message: "unknown region: mars" }));
t("no prototype keys", () => assert.throws(() => shippingCost("constructor", 1), { message: "unknown region: constructor" }));
t("no switch left", () => {
  const src = readFileSync(new URL("../src/shipping.ts", import.meta.url), "utf8");
  assert(!/\bswitch\s*\(/.test(src), "switch statement still present");
});
done();
