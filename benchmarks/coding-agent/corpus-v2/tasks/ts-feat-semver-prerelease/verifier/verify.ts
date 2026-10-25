import assert from "node:assert";
let failed = 0;
function t(name: string, fn: () => void): void { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + (e as Error).message); } }
function done(): void { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
import { compareSemver as c } from "../src/semver.ts";
t("core", () => { assert.strictEqual(c("1.2.3", "1.2.3"), 0); assert.strictEqual(c("1.2.3", "1.10.0"), -1); assert.strictEqual(c("2.0.0", "1.9.9"), 1); });
t("prerelease lower", () => { assert.strictEqual(c("1.0.0-alpha", "1.0.0"), -1); assert.strictEqual(c("1.0.0", "1.0.0-rc.1"), 1); });
t("spec ordering", () => {
  const order = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"];
  for (let i = 0; i < order.length - 1; i++) {
    assert.strictEqual(c(order[i], order[i + 1]), -1, order[i] + " < " + order[i + 1]);
    assert.strictEqual(c(order[i + 1], order[i]), 1);
  }
});
t("equal prerelease", () => assert.strictEqual(c("1.0.0-a.1", "1.0.0-a.1"), 0));
t("build ignored", () => { assert.strictEqual(c("1.0.0+abc", "1.0.0+xyz"), 0); assert.strictEqual(c("1.0.0-a+1", "1.0.0-a"), 0); });
t("numeric < alpha", () => assert.strictEqual(c("1.0.0-1", "1.0.0-a"), -1));
t("invalid", () => { assert.throws(() => c("1.2", "1.2.3"), TypeError); assert.throws(() => c("x.y.z", "1.0.0"), TypeError); assert.throws(() => c("1.0.0", ""), TypeError); });
done();
