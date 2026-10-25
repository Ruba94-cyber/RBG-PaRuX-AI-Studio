from common import task, ts_verify, mut_verify, PKG_MODULE

S = ['src/']
TASKS = []
A = TASKS.append

PK = {'package.json': PKG_MODULE}
NOTE = ' Write erasable TypeScript only (no enums or namespaces); it is executed by Node type stripping, import siblings with the .ts extension.'

# ---- 1 sortBy (bugfix, optimization)
A(task('ts-bug-sortby-stable', 'typescript', 'bugfix', 'optimization', 'easy',
 'sortBy<T>(items, key, order = "asc") in src/sort.ts must return a NEW array sorted by key(item), stable for equal keys, and support order "desc" (also stable). It currently mutates its input and ignores the order argument.' + NOTE,
 dict(PK, **{'src/sort.ts': '''export function sortBy<T>(items: T[], key: (t: T) => number, order: "asc" | "desc" = "asc"): T[] {
  return items.sort((a, b) => key(a) - key(b));
}
'''}),
 ts_verify('''
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
'''),
 {'src/sort.ts': '''export function sortBy<T>(items: T[], key: (t: T) => number, order: "asc" | "desc" = "asc"): T[] {
  const sign = order === "desc" ? -1 : 1;
  return items
    .map((item, index) => ({ item, index, k: key(item) }))
    .sort((a, b) => (a.k === b.k ? a.index - b.index : sign * (a.k - b.k)))
    .map((entry) => entry.item);
}
'''}, S, 420, 30))

# ---- 2 Result type (feature, held-out)
A(task('ts-feat-result-combinators', 'typescript', 'feature', 'held-out', 'medium',
 'src/result.ts defines a Result<T, E> union with ok()/err() constructors. Add exported helpers map(result, fn), mapErr(result, fn), andThen(result, fn) and unwrapOr(result, fallback): map/mapErr transform only their own side and pass the other through untouched; andThen chains a function returning a Result and short-circuits on err; unwrapOr returns the value or the fallback.' + NOTE,
 dict(PK, **{'src/result.ts': '''export type Result<T, E> = { ok: true; value: T } | { ok: false; error: E };

export function ok<T>(value: T): Result<T, never> {
  return { ok: true, value };
}

export function err<E>(error: E): Result<never, E> {
  return { ok: false, error };
}
'''}),
 ts_verify('''
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
'''),
 {'src/result.ts': '''export type Result<T, E> = { ok: true; value: T } | { ok: false; error: E };

export function ok<T>(value: T): Result<T, never> {
  return { ok: true, value };
}

export function err<E>(error: E): Result<never, E> {
  return { ok: false, error };
}

export function map<T, U, E>(r: Result<T, E>, fn: (t: T) => U): Result<U, E> {
  return r.ok ? ok(fn(r.value)) : r;
}

export function mapErr<T, E, F>(r: Result<T, E>, fn: (e: E) => F): Result<T, F> {
  return r.ok ? r : err(fn(r.error));
}

export function andThen<T, U, E, F>(r: Result<T, E>, fn: (t: T) => Result<U, F>): Result<U, E | F> {
  return r.ok ? fn(r.value) : r;
}

export function unwrapOr<T, E>(r: Result<T, E>, fallback: T): T {
  return r.ok ? r.value : fallback;
}
'''}, S, 480, 35))

# ---- 3 pagination (bugfix, held-out, mandatory)
A(task('ts-bug-paginate', 'typescript', 'bugfix', 'held-out', 'medium',
 'paginate(total, page, pageSize) in src/paginate.ts returns {page, pageSize, totalPages, offset, limit, hasPrev, hasNext}. totalPages is ceil(total/pageSize) with a minimum of 1; page is clamped into [1,totalPages]; offset is (page-1)*pageSize; limit is the number of items on that page (the last page may be short; an empty collection gives limit 0). pageSize must be a positive integer or a RangeError is thrown. Fix the existing implementation.' + NOTE,
 dict(PK, **{'src/paginate.ts': '''export interface Page {
  page: number;
  pageSize: number;
  totalPages: number;
  offset: number;
  limit: number;
  hasPrev: boolean;
  hasNext: boolean;
}

export function paginate(total: number, page: number, pageSize: number): Page {
  const totalPages = Math.floor(total / pageSize);
  const offset = page * pageSize;
  return { page, pageSize, totalPages, offset, limit: pageSize, hasPrev: page > 1, hasNext: page < totalPages };
}
'''}),
 ts_verify('''
import { paginate } from "../src/paginate.ts";
t("middle", () => assert.deepStrictEqual(paginate(95, 2, 10), { page: 2, pageSize: 10, totalPages: 10, offset: 10, limit: 10, hasPrev: true, hasNext: true }));
t("last short", () => assert.deepStrictEqual(paginate(95, 10, 10), { page: 10, pageSize: 10, totalPages: 10, offset: 90, limit: 5, hasPrev: true, hasNext: false }));
t("exact multiple", () => { const p = paginate(100, 10, 10); assert.strictEqual(p.totalPages, 10); assert.strictEqual(p.limit, 10); });
t("clamp high", () => assert.strictEqual(paginate(25, 99, 10).page, 3));
t("clamp low", () => { const p = paginate(25, 0, 10); assert.strictEqual(p.page, 1); assert.strictEqual(p.offset, 0); assert.strictEqual(p.hasPrev, false); });
t("empty", () => assert.deepStrictEqual(paginate(0, 1, 10), { page: 1, pageSize: 10, totalPages: 1, offset: 0, limit: 0, hasPrev: false, hasNext: false }));
t("bad page size", () => { assert.throws(() => paginate(5, 1, 0), RangeError); assert.throws(() => paginate(5, 1, 2.5), RangeError); });
'''),
 {'src/paginate.ts': '''export interface Page {
  page: number;
  pageSize: number;
  totalPages: number;
  offset: number;
  limit: number;
  hasPrev: boolean;
  hasNext: boolean;
}

export function paginate(total: number, page: number, pageSize: number): Page {
  if (!Number.isInteger(pageSize) || pageSize < 1) {
    throw new RangeError("pageSize must be a positive integer");
  }
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const clamped = Math.min(Math.max(page, 1), totalPages);
  const offset = (clamped - 1) * pageSize;
  const limit = Math.max(0, Math.min(pageSize, total - offset));
  return { page: clamped, pageSize, totalPages, offset, limit, hasPrev: clamped > 1, hasNext: clamped < totalPages };
}
'''}, S, 480, 30, mandatory=True))

# ---- 4 switch -> table (refactor, optimization)
A(task('ts-refactor-switch-table', 'typescript', 'refactor', 'optimization', 'easy',
 'shippingCost(region, weightKg) in src/shipping.ts is a long switch statement. Refactor it to a data-driven lookup table (a const object or Map of region -> {base, perKg}) with no `switch` statement left. Results must not change: unknown regions throw an Error whose message is "unknown region: <region>".' + NOTE,
 dict(PK, **{'src/shipping.ts': '''export function shippingCost(region: string, weightKg: number): number {
  switch (region) {
    case "eu":
      return 5 + 1.5 * weightKg;
    case "us":
      return 8 + 2 * weightKg;
    case "uk":
      return 6 + 1.75 * weightKg;
    case "apac":
      return 12 + 3 * weightKg;
    case "latam":
      return 14 + 3.5 * weightKg;
    default:
      throw new Error("unknown region: " + region);
  }
}
'''}),
 ts_verify('''
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
  assert(!/\\bswitch\\s*\\(/.test(src), "switch statement still present");
});
'''),
 {'src/shipping.ts': '''const RATES = new Map<string, { base: number; perKg: number }>([
  ["eu", { base: 5, perKg: 1.5 }],
  ["us", { base: 8, perKg: 2 }],
  ["uk", { base: 6, perKg: 1.75 }],
  ["apac", { base: 12, perKg: 3 }],
  ["latam", { base: 14, perKg: 3.5 }],
]);

export function shippingCost(region: string, weightKg: number): number {
  const rate = RATES.get(region);
  if (!rate) {
    throw new Error("unknown region: " + region);
  }
  return rate.base + rate.perKg * weightKg;
}
'''}, S, 420, 30))

# ---- 5 semver (feature, held-out)
A(task('ts-feat-semver-prerelease', 'typescript', 'feature', 'held-out', 'medium',
 'compareSemver(a, b) in src/semver.ts compares "MAJOR.MINOR.PATCH" strings and returns -1, 0 or 1. Extend it to support SemVer pre-release tags ("1.0.0-alpha.1"): a pre-release is lower than the same version without one; pre-release identifiers are compared left to right, numeric identifiers numerically, alphanumeric ones lexically, numeric lower than alphanumeric, and a shorter list is lower when all preceding identifiers are equal. Build metadata (+...) is ignored. Invalid input throws a TypeError.' + NOTE,
 dict(PK, **{'src/semver.ts': '''export function compareSemver(a: string, b: string): number {
  const pa = a.split(".").map(Number);
  const pb = b.split(".").map(Number);
  for (let i = 0; i < 3; i++) {
    if (pa[i] !== pb[i]) return pa[i] < pb[i] ? -1 : 1;
  }
  return 0;
}
'''}),
 ts_verify('''
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
'''),
 {'src/semver.ts': '''interface Parsed {
  core: number[];
  pre: string[];
}

function parse(v: string): Parsed {
  const m = /^(\\d+)\\.(\\d+)\\.(\\d+)(?:-([0-9A-Za-z.-]+))?(?:\\+[0-9A-Za-z.-]+)?$/.exec(v);
  if (!m) throw new TypeError("invalid semver: " + v);
  return { core: [Number(m[1]), Number(m[2]), Number(m[3])], pre: m[4] ? m[4].split(".") : [] };
}

const isNum = (s: string): boolean => /^\\d+$/.test(s);

export function compareSemver(a: string, b: string): number {
  const pa = parse(a);
  const pb = parse(b);
  for (let i = 0; i < 3; i++) {
    if (pa.core[i] !== pb.core[i]) return pa.core[i] < pb.core[i] ? -1 : 1;
  }
  if (pa.pre.length === 0 && pb.pre.length === 0) return 0;
  if (pa.pre.length === 0) return 1;
  if (pb.pre.length === 0) return -1;
  const n = Math.min(pa.pre.length, pb.pre.length);
  for (let i = 0; i < n; i++) {
    const x = pa.pre[i];
    const y = pb.pre[i];
    if (x === y) continue;
    const xn = isNum(x);
    const yn = isNum(y);
    if (xn && yn) return Number(x) < Number(y) ? -1 : 1;
    if (xn) return -1;
    if (yn) return 1;
    return x < y ? -1 : 1;
  }
  if (pa.pre.length === pb.pre.length) return 0;
  return pa.pre.length < pb.pre.length ? -1 : 1;
}
'''}, S, 600, 40))

# ---- 6 test writing validator (held-out)
A(task('ts-test-password-policy', 'typescript', 'testwriting', 'held-out', 'medium',
 'Write tests for validatePassword in src/policy.ts as tests/policy.test.ts using node:test and node:assert (run with: node --test tests/policy.test.ts from the repository root). Cover the minimum length, each required character class, the forbidden substring rule, and boundary lengths. Do not modify src/. Write erasable TypeScript only; import src with the .ts extension.',
 dict(PK, **{'src/policy.ts': '''export function validatePassword(pw: string, username = ""): string[] {
  const problems: string[] = [];
  if (pw.length < 10) problems.push("too short");
  if (pw.length > 64) problems.push("too long");
  if (!/[a-z]/.test(pw)) problems.push("needs lowercase");
  if (!/[A-Z]/.test(pw)) problems.push("needs uppercase");
  if (!/[0-9]/.test(pw)) problems.push("needs digit");
  if (username.length >= 3 && pw.toLowerCase().includes(username.toLowerCase())) problems.push("contains username");
  return problems;
}
'''}),
 mut_verify('''{
 'tests': ['tests/policy.test.ts'],
 'cmd': ['NODE', '--test', 'tests/policy.test.ts'],
 'src': 'src/policy.ts',
 'mutants': [
   ('pw.length < 10', 'pw.length < 9'),
   ('pw.length < 10', 'pw.length <= 10'),
   ('pw.length > 64', 'pw.length > 65'),
   ('pw.length > 64', 'pw.length >= 64'),
   ('/[a-z]/', '/[a-y]/'),
   ('!/[A-Z]/.test(pw)', '!/[A-Z]/.test(pw.toLowerCase())'),
   ('/[0-9]/', '/[1-9]/'),
   ('username.length >= 3', 'username.length >= 4'),
   ('pw.toLowerCase().includes(username.toLowerCase())', 'pw.includes(username)'),
 ]}'''),
 {'tests/policy.test.ts': '''import test from "node:test";
import assert from "node:assert";
import { validatePassword } from "../src/policy.ts";

const GOOD = "Abcdefgh12";

test("accepts a good password", () => {
  assert.deepStrictEqual(validatePassword(GOOD), []);
});
test("length boundaries: 9 too short, 10 ok, 64 ok, 65 too long", () => {
  assert.deepStrictEqual(validatePassword("Abcdefg12"), ["too short"]);
  assert.deepStrictEqual(validatePassword("Abcdefgh12"), []);
  assert.deepStrictEqual(validatePassword("Aa1" + "x".repeat(61)), []);
  assert.deepStrictEqual(validatePassword("Aa1" + "x".repeat(62)), ["too long"]);
});
test("lowercase required, including the letters a and z", () => {
  assert.deepStrictEqual(validatePassword("ABCDEFGHI1"), ["needs lowercase"]);
  assert.deepStrictEqual(validatePassword("ABCDEFGHI1a"), []);
  assert.deepStrictEqual(validatePassword("ABCDEFGHI1z"), []);
});
test("uppercase required", () => {
  assert.deepStrictEqual(validatePassword("abcdefghi1"), ["needs uppercase"]);
});
test("digit required, including 0", () => {
  assert.deepStrictEqual(validatePassword("Abcdefghij"), ["needs digit"]);
  assert.deepStrictEqual(validatePassword("Abcdefghi0"), []);
});
test("username rule is case-insensitive with a 3 char minimum", () => {
  assert.deepStrictEqual(validatePassword("XXBobbyXX12", "bobby"), ["contains username"]);
  assert.deepStrictEqual(validatePassword("XXBOBBYxx12", "bobby"), ["contains username"]);
  assert.deepStrictEqual(validatePassword("XXBobXXxx12", "bob"), ["contains username"]);
  assert.deepStrictEqual(validatePassword("XXboXXxx123", "bo"), []);
});
test("multiple problems are all reported", () => {
  assert.deepStrictEqual(validatePassword("abc"), ["too short", "needs uppercase", "needs digit"]);
});
'''}, ['tests/'], 480, 30, cmd=['{python}', '-B', '.verify/verify.py'], toolchains=['python', 'node']))
