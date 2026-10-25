from common import task, js_verify, mut_verify, PKG_CJS

S = ['src/']
TASKS = []
A = TASKS.append

PK = {'package.json': PKG_CJS}

# ---- 1 debouncer (bugfix, optimization)
A(task('js-bug-debounce-args', 'javascript', 'bugfix', 'optimization', 'medium',
 'createDebouncer(fn, wait, timers) in src/debounce.js returns a debounced function. When called repeatedly within `wait`, only ONE call to fn must happen, using the arguments of the LAST call, and the pending timer must be cleared on each new call. `timers` is an object with setTimeout/clearTimeout (injected for testing). Currently it uses the first call arguments and leaks timers. Fix it.',
 dict(PK, **{'src/debounce.js': '''"use strict";
function createDebouncer(fn, wait, timers) {
  let handle = null;
  return function debounced(...args) {
    if (handle === null) {
      handle = timers.setTimeout(() => {
        handle = null;
        fn(...args);
      }, wait);
    }
  };
}
module.exports = { createDebouncer };
'''}),
 js_verify('''
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
'''),
 {'src/debounce.js': '''"use strict";
function createDebouncer(fn, wait, timers) {
  let handle = null;
  return function debounced(...args) {
    if (handle !== null) {
      timers.clearTimeout(handle);
    }
    handle = timers.setTimeout(() => {
      handle = null;
      fn(...args);
    }, wait);
  };
}
module.exports = { createDebouncer };
'''}, S, 420, 30))

# ---- 2 deepEqual (bugfix, held-out)
A(task('js-bug-deep-equal', 'javascript', 'bugfix', 'held-out', 'medium',
 'deepEqual(a, b) in src/deepequal.js must compare plain objects, arrays, Dates (by time value) and primitives structurally. NaN equals NaN, an array never equals an object with the same keys, and objects with different key counts differ. Fix the current implementation (no external dependencies).',
 dict(PK, **{'src/deepequal.js': '''"use strict";
function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== "object" || typeof b !== "object" || a === null || b === null) return false;
  for (const key of Object.keys(a)) {
    if (!deepEqual(a[key], b[key])) return false;
  }
  return true;
}
module.exports = { deepEqual };
'''}),
 js_verify('''
const { deepEqual } = load("deepequal.js");
t("primitives", () => { assert(deepEqual(1, 1)); assert(!deepEqual(1, 2)); assert(!deepEqual(1, "1")); });
t("nan", () => assert(deepEqual(NaN, NaN)));
t("nested", () => assert(deepEqual({ a: [1, { b: 2 }] }, { a: [1, { b: 2 }] })));
t("extra key in b", () => assert(!deepEqual({ a: 1 }, { a: 1, b: 2 })));
t("extra key in a", () => assert(!deepEqual({ a: 1, b: 2 }, { a: 1 })));
t("array vs object", () => assert(!deepEqual([1, 2], { 0: 1, 1: 2 })));
t("array length", () => assert(!deepEqual([1, 2], [1, 2, 3])));
t("dates", () => { assert(deepEqual(new Date(5), new Date(5))); assert(!deepEqual(new Date(5), new Date(6))); });
t("null", () => { assert(!deepEqual(null, {})); assert(deepEqual(null, null)); });
t("undefined key", () => assert(!deepEqual({ a: undefined }, { b: undefined })));
'''),
 {'src/deepequal.js': '''"use strict";
function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a === "number" && typeof b === "number") return Number.isNaN(a) && Number.isNaN(b);
  if (typeof a !== "object" || typeof b !== "object" || a === null || b === null) return false;
  if (a instanceof Date || b instanceof Date) {
    return a instanceof Date && b instanceof Date && a.getTime() === b.getTime();
  }
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  const ka = Object.keys(a);
  const kb = Object.keys(b);
  if (ka.length !== kb.length) return false;
  for (const key of ka) {
    if (!Object.prototype.hasOwnProperty.call(b, key)) return false;
    if (!deepEqual(a[key], b[key])) return false;
  }
  return true;
}
module.exports = { deepEqual };
'''}, S, 480, 30))

# ---- 3 flatten (bugfix, held-out)
A(task('js-bug-flatten-depth', 'javascript', 'bugfix', 'held-out', 'easy',
 'flatten(arr, depth = 1) in src/flatten.js must flatten nested arrays up to `depth` levels (depth 0 = no flattening; Infinity = fully). It currently flattens everything regardless of depth. It must not mutate its input. Fix it without using Array.prototype.flat.',
 dict(PK, **{'src/flatten.js': '''"use strict";
function flatten(arr, depth = 1) {
  const out = [];
  for (const item of arr) {
    if (Array.isArray(item)) out.push(...flatten(item, depth));
    else out.push(item);
  }
  return out;
}
module.exports = { flatten };
'''}),
 js_verify('''
const { flatten } = load("flatten.js");
t("default depth 1", () => assert.deepStrictEqual(flatten([1, [2, [3]]]), [1, 2, [3]]));
t("depth 0", () => assert.deepStrictEqual(flatten([1, [2]], 0), [1, [2]]));
t("depth 2", () => assert.deepStrictEqual(flatten([1, [2, [3, [4]]]], 2), [1, 2, 3, [4]]));
t("infinity", () => assert.deepStrictEqual(flatten([1, [2, [3, [4]]]], Infinity), [1, 2, 3, 4]));
t("no mutation", () => { const a = [1, [2]]; flatten(a); assert.deepStrictEqual(a, [1, [2]]); });
t("empty", () => assert.deepStrictEqual(flatten([]), []));
t("does not use Array.prototype.flat", () => {
  const src = require("node:fs").readFileSync(require("node:path").join(SRC, "flatten.js"), "utf8");
  assert(!/\\.flat\\s*\\(/.test(src) && !/flatMap/.test(src));
});
'''),
 {'src/flatten.js': '''"use strict";
function flatten(arr, depth = 1) {
  const out = [];
  for (const item of arr) {
    if (Array.isArray(item) && depth > 0) out.push(...flatten(item, depth - 1));
    else out.push(item);
  }
  return out;
}
module.exports = { flatten };
'''}, S, 300, 25))

# ---- 4 event emitter (feature, optimization)
A(task('js-feat-emitter-once-off', 'javascript', 'feature', 'optimization', 'medium',
 'Extend Emitter in src/emitter.js with once(event, listener) (the listener runs at most one time) and off(event, listener) (removes a specific listener; removing an unknown listener is a no-op). emit must return true if any listener ran, false otherwise, and must keep working if a listener removes itself while emitting.',
 dict(PK, **{'src/emitter.js': '''"use strict";
class Emitter {
  constructor() { this.listeners = new Map(); }
  on(event, listener) {
    if (!this.listeners.has(event)) this.listeners.set(event, []);
    this.listeners.get(event).push(listener);
    return this;
  }
  emit(event, ...args) {
    const list = this.listeners.get(event) || [];
    for (const l of list) l(...args);
  }
}
module.exports = { Emitter };
'''}),
 js_verify('''
const { Emitter } = load("emitter.js");
t("on/emit", () => { const e = new Emitter(); const got = []; e.on("x", (v) => got.push(v)); assert.strictEqual(e.emit("x", 1), true); assert.deepStrictEqual(got, [1]); });
t("emit false when none", () => assert.strictEqual(new Emitter().emit("nope"), false));
t("once", () => { const e = new Emitter(); let n = 0; e.once("x", () => n++); e.emit("x"); e.emit("x"); assert.strictEqual(n, 1); });
t("off", () => { const e = new Emitter(); let n = 0; const f = () => n++; e.on("x", f); e.off("x", f); assert.strictEqual(e.emit("x"), false); assert.strictEqual(n, 0); });
t("off unknown", () => { const e = new Emitter(); e.off("x", () => {}); e.on("x", () => {}); e.off("x", () => {}); assert.strictEqual(e.emit("x"), true); });
t("self removal during emit", () => { const e = new Emitter(); const order = []; const a = () => { order.push("a"); e.off("x", a); }; e.on("x", a); e.on("x", () => order.push("b")); e.emit("x"); e.emit("x"); assert.deepStrictEqual(order, ["a", "b", "b"]); });
t("off removes once wrapper", () => { const e = new Emitter(); let n = 0; const f = () => n++; e.once("x", f); e.off("x", f); e.emit("x"); assert.strictEqual(n, 0); });
t("chaining", () => { const e = new Emitter(); assert.strictEqual(e.on("a", () => {}), e); assert.strictEqual(e.once("a", () => {}), e); assert.strictEqual(e.off("a", () => {}), e); });
'''),
 {'src/emitter.js': '''"use strict";
class Emitter {
  constructor() { this.listeners = new Map(); }
  on(event, listener) {
    if (!this.listeners.has(event)) this.listeners.set(event, []);
    this.listeners.get(event).push(listener);
    return this;
  }
  once(event, listener) {
    const wrapper = (...args) => {
      this.off(event, wrapper);
      listener(...args);
    };
    wrapper.original = listener;
    return this.on(event, wrapper);
  }
  off(event, listener) {
    const list = this.listeners.get(event);
    if (!list) return this;
    const i = list.findIndex((l) => l === listener || l.original === listener);
    if (i >= 0) list.splice(i, 1);
    return this;
  }
  emit(event, ...args) {
    const list = (this.listeners.get(event) || []).slice();
    for (const l of list) l(...args);
    return list.length > 0;
  }
}
module.exports = { Emitter };
'''}, S, 480, 35))

# ---- 5 template render (feature, held-out, mandatory)
A(task('js-feat-template-render', 'javascript', 'feature', 'held-out', 'medium',
 'render(template, data) in src/render.js replaces {{name}} placeholders. Extend it: support dotted paths ({{user.name}}), a fallback after a pipe ({{nick|friend}} uses "friend" when the value is undefined or null), and HTML-escape &, <, > and " in substituted values. Unknown placeholders without fallback must render as an empty string. Whitespace inside braces ({{ name }}) is allowed.',
 dict(PK, **{'src/render.js': '''"use strict";
function render(template, data) {
  return template.replace(/\\{\\{(\\w+)\\}\\}/g, (m, key) => String(data[key]));
}
module.exports = { render };
'''}),
 js_verify('''
const { render } = load("render.js");
t("simple", () => assert.strictEqual(render("Hi {{name}}", { name: "Ann" }), "Hi Ann"));
t("path", () => assert.strictEqual(render("{{user.name}}", { user: { name: "Bo" } }), "Bo"));
t("deep path missing", () => assert.strictEqual(render("[{{a.b.c}}]", { a: {} }), "[]"));
t("fallback", () => assert.strictEqual(render("{{nick|friend}}", {}), "friend"));
t("fallback null", () => assert.strictEqual(render("{{nick|friend}}", { nick: null }), "friend"));
t("fallback unused", () => assert.strictEqual(render("{{nick|friend}}", { nick: "Z" }), "Z"));
t("zero is a value", () => assert.strictEqual(render("{{n|none}}", { n: 0 }), "0"));
t("escape", () => assert.strictEqual(render("{{x}}", { x: '<a href="1">&</a>' }), "&lt;a href=&quot;1&quot;&gt;&amp;&lt;/a&gt;"));
t("unknown empty", () => assert.strictEqual(render("a{{zzz}}b", {}), "ab"));
t("spaces", () => assert.strictEqual(render("{{ name }}", { name: "Q" }), "Q"));
t("multiple", () => assert.strictEqual(render("{{a}}-{{b}}-{{a}}", { a: 1, b: 2 }), "1-2-1"));
'''),
 {'src/render.js': '''"use strict";
function escapeHtml(s) {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}
function lookup(data, path) {
  let cur = data;
  for (const part of path.split(".")) {
    if (cur === undefined || cur === null) return undefined;
    cur = cur[part];
  }
  return cur;
}
function render(template, data) {
  return template.replace(/\\{\\{\\s*([\\w.]+)\\s*(?:\\|([^}]*))?\\}\\}/g, (m, path, fallback) => {
    const value = lookup(data, path);
    if (value === undefined || value === null) {
      return fallback === undefined ? "" : escapeHtml(fallback.trim());
    }
    return escapeHtml(String(value));
  });
}
module.exports = { render };
'''}, S, 540, 35, mandatory=True))

# ---- 6 callbacks -> async (refactor, held-out)
A(task('js-refactor-async-await', 'javascript', 'refactor', 'held-out', 'medium',
 'src/loader.js exposes loadUser(id, cb) built on a callback-style helper chain. Refactor it into `async function loadUser(id)` that returns a promise resolving to the same value {id, name, plan} and rejecting with the same error messages. Keep fetchRaw(id, cb) in src/loader.js exported and unchanged (it is the I/O boundary). loadUser must no longer take a callback.',
 dict(PK, **{'src/loader.js': '''"use strict";
const DB = { 1: { name: "ann", planId: "p1" }, 2: { name: "bo", planId: "zz" } };
const PLANS = { p1: "pro" };

function fetchRaw(id, cb) {
  setImmediate(() => {
    if (!DB[id]) return cb(new Error("user not found: " + id));
    cb(null, DB[id]);
  });
}

function loadUser(id, cb) {
  fetchRaw(id, (err, raw) => {
    if (err) return cb(err);
    const plan = PLANS[raw.planId];
    if (!plan) return cb(new Error("unknown plan: " + raw.planId));
    cb(null, { id, name: raw.name, plan });
  });
}

module.exports = { fetchRaw, loadUser };
'''}),
 js_verify('''
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
  assert(/async function loadUser\\s*\\(\\s*id\\s*\\)/.test(src), "async function loadUser(id) expected");
});
''', wrap_async=True),
 {'src/loader.js': '''"use strict";
const DB = { 1: { name: "ann", planId: "p1" }, 2: { name: "bo", planId: "zz" } };
const PLANS = { p1: "pro" };

function fetchRaw(id, cb) {
  setImmediate(() => {
    if (!DB[id]) return cb(new Error("user not found: " + id));
    cb(null, DB[id]);
  });
}

function fetchRawAsync(id) {
  return new Promise((resolve, reject) => {
    fetchRaw(id, (err, raw) => (err ? reject(err) : resolve(raw)));
  });
}

async function loadUser(id) {
  const raw = await fetchRawAsync(id);
  const plan = PLANS[raw.planId];
  if (!plan) throw new Error("unknown plan: " + raw.planId);
  return { id, name: raw.name, plan };
}

module.exports = { fetchRaw, loadUser };
'''}, S, 540, 35))

# ---- 7 test writing chunk (optimization)
A(task('js-test-chunk-uniq', 'javascript', 'testwriting', 'optimization', 'easy',
 'Write tests for chunk and uniqueBy in src/lists.js as tests/lists.test.js using the built-in node:test and node:assert modules (run with: node --test tests/lists.test.js from the repository root). Cover chunk sizes, remainder chunk, invalid sizes (throws RangeError), uniqueBy keeping the first occurrence and preserving order. Do not modify src/.',
 dict(PK, **{'src/lists.js': '''"use strict";
function chunk(arr, size) {
  if (!Number.isInteger(size) || size < 1) throw new RangeError("size must be a positive integer");
  const out = [];
  for (let i = 0; i < arr.length; i += size) out.push(arr.slice(i, i + size));
  return out;
}
function uniqueBy(arr, keyFn) {
  const seen = new Set();
  const out = [];
  for (const item of arr) {
    const k = keyFn(item);
    if (!seen.has(k)) {
      seen.add(k);
      out.push(item);
    }
  }
  return out;
}
module.exports = { chunk, uniqueBy };
'''}),
 mut_verify('''{
 'tests': ['tests/lists.test.js'],
 'cmd': ['NODE', '--test', 'tests/lists.test.js'],
 'src': 'src/lists.js',
 'mutants': [
   ('i += size', 'i += size + 1'),
   ('size < 1', 'size < 2'),   ('!Number.isInteger(size) ||', ''),
   ('out.push(arr.slice(i, i + size))', 'out.push(arr.slice(i, i + size - 1))'),
   ('if (!seen.has(k))', 'if (seen.has(k))'),
   ('seen.add(k);', ''),
   ('out.push(item);', 'out.unshift(item);'),
 ]}'''),
 {'tests/lists.test.js': '''"use strict";
const test = require("node:test");
const assert = require("node:assert");
const { chunk, uniqueBy } = require("../src/lists.js");

test("chunk splits evenly", () => {
  assert.deepStrictEqual(chunk([1, 2, 3, 4], 2), [[1, 2], [3, 4]]);
});
test("chunk keeps remainder", () => {
  assert.deepStrictEqual(chunk([1, 2, 3, 4, 5], 2), [[1, 2], [3, 4], [5]]);
});
test("chunk size larger than array", () => {
  assert.deepStrictEqual(chunk([1, 2], 5), [[1, 2]]);
});
test("chunk size 1 and empty input", () => {
  assert.deepStrictEqual(chunk([1, 2, 3], 1), [[1], [2], [3]]);
  assert.deepStrictEqual(chunk([], 3), []);
});
test("chunk rejects invalid sizes", () => {
  assert.throws(() => chunk([1], 0), RangeError);
  assert.throws(() => chunk([1], -1), RangeError);
  assert.throws(() => chunk([1], 1.5), RangeError);
});
test("uniqueBy keeps first occurrence in order", () => {
  const rows = [{ id: 1, v: "a" }, { id: 2, v: "b" }, { id: 1, v: "c" }, { id: 3, v: "d" }];
  assert.deepStrictEqual(uniqueBy(rows, (r) => r.id).map((r) => r.v), ["a", "b", "d"]);
});
test("uniqueBy on empty and all-distinct", () => {
  assert.deepStrictEqual(uniqueBy([], (x) => x), []);
  assert.deepStrictEqual(uniqueBy([3, 1, 2], (x) => x), [3, 1, 2]);
});
'''}, ['tests/'], 420, 30, cmd=['{python}', '-B', '.verify/verify.py'], toolchains=['python', 'node']))

# ---- 8 query parse (bugfix, optimization)
A(task('js-bug-query-parse', 'javascript', 'bugfix', 'optimization', 'medium',
 'parseQuery(qs) in src/query.js parses a URL query string (with or without leading "?") into an object. Values must be percent-decoded and "+" means space; keys that appear more than once become arrays in order; a key without "=" maps to an empty string. Fix the current implementation.',
 dict(PK, **{'src/query.js': '''"use strict";
function parseQuery(qs) {
  const out = {};
  for (const pair of qs.split("&")) {
    const [k, v] = pair.split("=");
    out[k] = v;
  }
  return out;
}
module.exports = { parseQuery };
'''}),
 js_verify('''
const { parseQuery } = load("query.js");
t("basic", () => assert.deepStrictEqual(parseQuery("a=1&b=2"), { a: "1", b: "2" }));
t("leading ?", () => assert.deepStrictEqual(parseQuery("?a=1"), { a: "1" }));
t("decode", () => assert.deepStrictEqual(parseQuery("q=h%C3%A9llo%20w"), { q: "héllo w" }));
t("plus is space", () => assert.deepStrictEqual(parseQuery("q=a+b"), { q: "a b" }));
t("repeat", () => assert.deepStrictEqual(parseQuery("t=1&t=2&t=3"), { t: ["1", "2", "3"] }));
t("no equals", () => assert.deepStrictEqual(parseQuery("flag&x=1"), { flag: "", x: "1" }));
t("equals in value", () => assert.deepStrictEqual(parseQuery("e=a=b"), { e: "a=b" }));
t("empty", () => assert.deepStrictEqual(parseQuery(""), {}));
t("encoded key", () => assert.deepStrictEqual(parseQuery("a%20b=1"), { "a b": "1" }));
'''),
 {'src/query.js': '''"use strict";
function dec(s) {
  return decodeURIComponent(s.replace(/\\+/g, " "));
}
function parseQuery(qs) {
  const out = {};
  const body = qs.startsWith("?") ? qs.slice(1) : qs;
  if (body === "") return out;
  for (const pair of body.split("&")) {
    if (pair === "") continue;
    const i = pair.indexOf("=");
    const k = dec(i < 0 ? pair : pair.slice(0, i));
    const v = i < 0 ? "" : dec(pair.slice(i + 1));
    if (Object.prototype.hasOwnProperty.call(out, k)) {
      out[k] = [].concat(out[k], v);
    } else {
      out[k] = v;
    }
  }
  return out;
}
module.exports = { parseQuery };
'''}, S, 420, 30))
