"use strict";
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
