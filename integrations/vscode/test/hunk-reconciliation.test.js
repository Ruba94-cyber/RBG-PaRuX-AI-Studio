'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { captureHunk, resolveHunk, rangesOverlap } = require('../hunk-identity');

// BIC-09 reconciliation matrix: moved / deleted / overlapping / partially accepted hunks.

test('a hunk whose neighbours were both deleted still resolves when its text is unique', () => {
  const before = ['top', 'x1', 'x2', 'bottom', 'z'].join('\n');
  const h = captureHunk(before, 'src/a.cs', 1, 2);
  const r = resolveHunk(['x1', 'x2', 'z'].join('\n'), h);
  assert.equal(r.status, 'ok');
  assert.equal(r.startLine, 0);
  assert.equal(r.relocated, true);
});

test('a hunk moved to the end of the file keeps resolving', () => {
  const before = ['a', 'b', 'c', 'd', 'e'].join('\n');
  const h = captureHunk(before, 'src/a.cs', 1, 2);
  const r = resolveHunk(['a', 'd', 'e', 'b', 'c'].join('\n'), h);
  assert.equal(r.status, 'ok');
  assert.equal(r.startLine, 3);
  assert.equal(r.endLine, 4);
});

test('partially accepted hunk: the rest is a distinct descriptor that survives a rewrite of the accepted half', () => {
  const text = ['h', 'a', 'b', 'c', 'd', 't'].join('\n');
  const whole = captureHunk(text, 'src/a.cs', 1, 4);
  const acceptedHalf = captureHunk(text, 'src/a.cs', 1, 2);
  const rest = captureHunk(text, 'src/a.cs', 3, 4);
  assert.notEqual(acceptedHalf.id, rest.id);
  assert.equal(rangesOverlap({ startLine: 1, endLine: 2 }, { startLine: 3, endLine: 4 }), false);
  const edited = ['h', 'A', 'B', 'c', 'd', 't'].join('\n');
  assert.equal(resolveHunk(edited, whole).status, 'stale');
  assert.equal(resolveHunk(edited, acceptedHalf).status, 'stale');
  const restResult = resolveHunk(edited, rest);
  assert.equal(restResult.status, 'ok');
  assert.equal(restResult.startLine, 3);
});

test('an overlapping concurrent edit makes only the overlapped descriptor stale', () => {
  const text = ['1', '2', '3', '4', '5', '6'].join('\n');
  const left = captureHunk(text, 'src/a.cs', 0, 1);
  const right = captureHunk(text, 'src/a.cs', 2, 5);
  assert.equal(rangesOverlap({ startLine: 0, endLine: 2 }, { startLine: 2, endLine: 5 }), true);
  const edited = ['1', '2', '3', '4', 'five', '6'].join('\n');
  assert.equal(resolveHunk(edited, left).status, 'ok');
  assert.equal(resolveHunk(edited, right).status, 'stale');
});

test('an empty or truncated document fails closed for any descriptor', () => {
  const h = captureHunk(['a', 'b'].join('\n'), 'src/a.cs', 0, 1);
  assert.equal(resolveHunk('', h).status, 'stale');
  assert.equal(resolveHunk('a', h).status, 'stale');
});
