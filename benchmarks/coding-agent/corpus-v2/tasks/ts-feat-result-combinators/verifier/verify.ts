import assert from "node:assert";
let failed = 0;
function t(name: string, fn: () => void): void { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + (e as Error).message); } }
function done(): void { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
import { ok, err, map, mapErr, andThen, unwrapOr } from "../src/result.ts";
t("map ok", () => assert.deepStrictEqual(map(ok(2), (x: number) => x * 2), ok(4)));
t("map err passthrough", () => { const e = err("bad"); assert.strictEqual(map(e, (x: number) => x * 2), e); });
t("mapErr", () => assert.deepStrictEqual(mapErr(err("a"), (s: string) => s + "!"), err("a!")));
t("mapErr ok passthrough", () => { const o = ok(1); assert.strictEqual(mapErr(o, (s: string) => s + "!"), o); });
t("andThen chain", () => assert.deepStrictEqual(andThen(ok(3), (x: number) => ok(x + 1)), ok(4)));
t("andThen can fail", () => assert.deepStrictEqual(andThen(ok(3), (x: number) => err("no" + x)), err("no3")));
t("andThen short-circuits", () => { let called = false; const r = andThen(err("first"), () => { called = true; return ok(1); }); assert.deepStrictEqual(r, err("first")); assert.strictEqual(called, false); });
t("unwrapOr", () => { assert.strictEqual(unwrapOr(ok(5), 0), 5); assert.strictEqual(unwrapOr(err("x"), 9), 9); });
t("map not called on err", () => { let n = 0; map(err("e"), () => { n++; return 1; }); assert.strictEqual(n, 0); });
done();
