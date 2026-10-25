import test from "node:test";
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
