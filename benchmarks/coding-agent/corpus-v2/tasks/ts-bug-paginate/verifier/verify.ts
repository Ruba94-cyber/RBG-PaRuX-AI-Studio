import assert from "node:assert";
let failed = 0;
function t(name: string, fn: () => void): void { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + (e as Error).message); } }
function done(): void { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
import { paginate } from "../src/paginate.ts";
t("middle", () => assert.deepStrictEqual(paginate(95, 2, 10), { page: 2, pageSize: 10, totalPages: 10, offset: 10, limit: 10, hasPrev: true, hasNext: true }));
t("last short", () => assert.deepStrictEqual(paginate(95, 10, 10), { page: 10, pageSize: 10, totalPages: 10, offset: 90, limit: 5, hasPrev: true, hasNext: false }));
t("exact multiple", () => { const p = paginate(100, 10, 10); assert.strictEqual(p.totalPages, 10); assert.strictEqual(p.limit, 10); });
t("clamp high", () => assert.strictEqual(paginate(25, 99, 10).page, 3));
t("clamp low", () => { const p = paginate(25, 0, 10); assert.strictEqual(p.page, 1); assert.strictEqual(p.offset, 0); assert.strictEqual(p.hasPrev, false); });
t("empty", () => assert.deepStrictEqual(paginate(0, 1, 10), { page: 1, pageSize: 10, totalPages: 1, offset: 0, limit: 0, hasPrev: false, hasNext: false }));
t("bad page size", () => { assert.throws(() => paginate(5, 1, 0), RangeError); assert.throws(() => paginate(5, 1, 2.5), RangeError); });
done();
