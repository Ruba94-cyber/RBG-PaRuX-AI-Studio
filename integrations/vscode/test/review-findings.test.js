'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const {
  parseReviewMarkdown,
  normalizeStoredFindings,
  mergeFindings
} = require('../review-findings');
const { captureHunk, resolveHunk } = require('../hunk-identity');

test('parses inline severity and file ranges from PaRuX review markdown', () => {
  const findings = parseReviewMarkdown([
    '# PaRuX inline review',
    '',
    '**Source:** `src/worker.js` · task `t-1`',
    '',
    '- **High** `src/worker.js:12-14` — cancellation can publish stale output.',
    '- [LOW] src/worker.js:30 - rename this local for clarity.'
  ].join('\n'));
  assert.equal(findings.length, 2);
  assert.deepEqual(findings[0], {
    severity: 'high', file: 'src/worker.js', startLine: 12, endLine: 14,
    message: 'cancellation can publish stale output.', taskId: 't-1'
  });
  assert.equal(findings[1].severity, 'low');
  assert.equal(findings[1].startLine, 30);
});

test('inherits severity heading and source file for line-only findings', () => {
  const findings = parseReviewMarkdown([
    '# PaRuX inline review',
    '**Source:** `src/store.cs` · task `review-7`',
    '### Critical',
    '- Lines 40-42: transaction commits before the durable write.'
  ].join('\n'));
  assert.equal(findings.length, 1);
  assert.equal(findings[0].severity, 'critical');
  assert.equal(findings[0].file, 'src/store.cs');
  assert.equal(findings[0].startLine, 40);
  assert.equal(findings[0].endLine, 42);
});

test('does not invent findings without both severity and concrete location', () => {
  const findings = parseReviewMarkdown([
    '# PaRuX inline review',
    '**Source:** `src/a.cs` · task `r`',
    '- High: this is vague and has no source line.',
    '- src/a.cs:9 this has a line but no severity.'
  ].join('\n'));
  assert.deepEqual(findings, []);
});

test('stored state survives reload and a repeated review preserves per-finding disposition', () => {
  const descriptor = captureHunk('a\nb\nc', 'file:///repo/a.cs', 1, 1);
  const original = {
    schemaVersion: 1, id: 'f1', fileUri: 'file:///repo/a.cs', displayFile: 'a.cs',
    severity: 'high', message: 'bug', taskId: 't', state: 'resolved', descriptor,
    createdUtc: '2026-09-11T10:00:00Z'
  };
  const refreshed = { ...original, state: 'open', createdUtc: '2026-09-11T11:00:00Z' };
  const merged = mergeFindings(normalizeStoredFindings([null, original]), [refreshed]);
  assert.equal(merged.length, 1);
  assert.equal(merged[0].state, 'resolved');
  assert.equal(merged[0].createdUtc, '2026-09-11T10:00:00Z');
});

test('repeated review from a new task preserves disposition instead of duplicating the finding', () => {
  const descriptor = captureHunk('a\nb\nc', 'file:///repo/a.cs', 1, 1);
  const original = {
    schemaVersion: 1, id: 'legacy-task-1', fileUri: 'file:///repo/a.cs', displayFile: 'a.cs',
    severity: 'high', message: 'bug', taskId: 'task-1', state: 'dismissed', descriptor,
    createdUtc: '2026-09-11T10:00:00Z'
  };
  const refreshed = {
    ...original, id: 'new-task-2', taskId: 'task-2', state: 'open',
    createdUtc: '2026-09-11T11:00:00Z'
  };
  const merged = mergeFindings([original], [refreshed]);
  assert.equal(merged.length, 1);
  assert.equal(merged[0].state, 'dismissed');
  assert.equal(merged[0].taskId, 'task-2');
  assert.equal(merged[0].createdUtc, '2026-09-11T10:00:00Z');
});

test('finding hunk identity relocates after unrelated edits and fails closed after target edits', () => {
  const before = ['header', 'danger()', 'tail'].join('\n');
  const descriptor = captureHunk(before, 'file:///repo/a.js', 1, 1);
  const shifted = ['new', 'header', 'danger()', 'tail'].join('\n');
  const moved = resolveHunk(shifted, descriptor);
  assert.equal(moved.status, 'ok');
  assert.equal(moved.startLine, 2);
  assert.equal(moved.relocated, true);
  assert.equal(resolveHunk(['new','header','safe()','tail'].join('\n'), descriptor).status, 'stale');
});
