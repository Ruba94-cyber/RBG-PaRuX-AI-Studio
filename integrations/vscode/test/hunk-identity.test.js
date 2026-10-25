'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const {
  MAX_DECISIONS, captureHunk, resolveHunk, rangesOverlap, normalizeDecisions, appendDecision
} = require('../hunk-identity');

test('resolves unchanged hunk at original range', () => {
  const text = ['a','b','c','d'].join('\n');
  const h = captureHunk(text, 'src/a.cs', 1, 2);
  assert.deepEqual(resolveHunk(text, h), {status:'ok', startLine:1, endLine:2, score:2, available:2, relocated:false});
});

test('relocates hunk after unrelated lines are inserted before it', () => {
  const before = ['header','one','two','tail'].join('\n');
  const h = captureHunk(before, 'src/a.cs', 1, 2);
  const after = ['new','header','one','two','tail'].join('\n');
  const r = resolveHunk(after, h);
  assert.equal(r.status, 'ok');
  assert.equal(r.startLine, 2);
  assert.equal(r.relocated, true);
  assert.equal(r.score, 2);
});

test('fails closed when selected lines were deleted or edited', () => {
  const h = captureHunk('a\nb\nc\nd', 'src/a.cs', 1, 2);
  assert.equal(resolveHunk('a\nb changed\nd', h).status, 'stale');
  assert.equal(resolveHunk('a\nd', h).status, 'stale');
});

test('uses anchors to resolve duplicate selected text', () => {
  const before = ['top','left','same','right','middle','x','same','y'].join('\n');
  const h = captureHunk(before, 'f', 2, 2);
  const shifted = ['insert','top','left','same','right','middle','x','same','y'].join('\n');
  const r = resolveHunk(shifted, h);
  assert.equal(r.status, 'ok');
  assert.equal(r.startLine, 3);
  assert.equal(r.score, 2);
});

test('rejects ambiguous duplicate hunks when anchors cannot distinguish them', () => {
  const original = ['same','same','same'].join('\n');
  const h = captureHunk(original, 'f', 1, 1);
  const changed = ['x','same','y','same','z'].join('\n');
  const r = resolveHunk(changed, h);
  assert.equal(r.status, 'ambiguous');
});

test('detects overlapping ranges including containment', () => {
  assert.equal(rangesOverlap({startLine:2,endLine:5},{startLine:5,endLine:8}), true);
  assert.equal(rangesOverlap({startLine:2,endLine:8},{startLine:3,endLine:4}), true);
  assert.equal(rangesOverlap({startLine:2,endLine:4},{startLine:5,endLine:8}), false);
});

test('decision ledger survives reload, rejects malformed records and stays bounded', () => {
  let ledger = [];
  for (let i=0;i<MAX_DECISIONS+15;i++) ledger = appendDecision(ledger, {
    hunkId:`h${i}`, fileKey:'src/a.cs', action:i%2?'accepted':'rejected',
    originalStartLine:i, resolvedStartLine:i+1, lineCount:1, relocated:true,
    decidedUtc:`2026-09-10T20:${String(i%60).padStart(2,'0')}:00Z`
  });
  assert.equal(ledger.length, MAX_DECISIONS);
  const roundTrip = JSON.parse(JSON.stringify(ledger));
  assert.deepEqual(normalizeDecisions([null,{bad:true},...roundTrip]), ledger);
  assert.equal(ledger[0].hunkId, 'h15');
  assert.equal(ledger.at(-1).action, 'rejected');
});
