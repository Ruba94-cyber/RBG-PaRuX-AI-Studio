import assert from "node:assert";
let failed = 0;
function t(name: string, fn: () => void): void { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + (e as Error).message); } }
function done(): void { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
import { sortBy } from "../src/sort.ts";
t("asc", () => assert.deepStrictEqual(sortBy([3, 1, 2], (x) => x), [1, 2, 3]));
t("desc", () => assert.deepStrictEqual(sortBy([3, 1, 2], (x) => x, "desc"), [3, 2, 1]));
t("no mutation", () => { const a = [3, 1, 2]; sortBy(a, (x) => x); assert.deepStrictEqual(a, [3, 1, 2]); });
t("returns new array", () => { const a = [1, 2]; assert.notStrictEqual(sortBy(a, (x) => x), a); });
t("stable asc", () => {
  const rows = [{ k: 1, n: "a" }, { k: 0, n: "b" }, { k: 1, n: "c" }, { k: 0, n: "d" }];
  assert.deepStrictEqual(sortBy(rows, (r) => r.k).map((r) => r.n), ["b", "d", "a", "c"]);
});
t("stable desc", () => {
  const rows = [{ k: 1, n: "a" }, { k: 0, n: "b" }, { k: 1, n: "c" }, { k: 0, n: "d" }];
  assert.deepStrictEqual(sortBy(rows, (r) => r.k, "desc").map((r) => r.n), ["a", "c", "b", "d"]);
});
done();
