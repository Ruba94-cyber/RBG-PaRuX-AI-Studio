'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
const extension = fs.readFileSync(path.join(root, 'extension.js'), 'utf8');
const hunkReview = fs.readFileSync(path.join(root, 'hunk-review.js'), 'utf8');
const main = fs.readFileSync(path.join(root, 'main.js'), 'utf8');

const requiredCommands = [
  'parux.fixSelection',
  'parux.explainSelection',
  'parux.testSelection',
  'parux.reviewSelection',
  'parux.hunkFeedback',
  'parux.acceptHunk',
  'parux.rejectHunk'
];

test('VS Code V2 exposes every editor action in manifest and activation events', () => {
  const contributed = new Set((manifest.contributes?.commands || []).map(entry => entry.command));
  const activations = new Set(manifest.activationEvents || []);

  for (const command of requiredCommands) {
    assert.ok(contributed.has(command), `missing contributed command ${command}`);
    assert.ok(activations.has(`onCommand:${command}`), `missing activation event for ${command}`);
  }
  assert.equal(manifest.main, './main.js');
  assert.match(main, /registerHunkReviewCommands/);
});

test('VS Code V2 captures bounded selection, surrounding text, open files and diagnostics', () => {
  for (const marker of [
    '[PARUX_VSCODE_CONTEXT_V2]',
    '[SELECTED_OR_TARGET_TEXT]',
    '[ACTIVE_FILE_SURROUNDING_CONTEXT]',
    '[OPEN_FILES]',
    '[WORKSPACE_DIAGNOSTICS]'
  ]) {
    assert.match(extension, new RegExp(marker.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
  }

  assert.match(extension, /MAX_SELECTION_CHARS\s*=\s*16000/);
  assert.match(extension, /MAX_ACTIVE_CONTEXT_CHARS\s*=\s*24000/);
  assert.match(extension, /MAX_OPEN_FILE_CHARS\s*=\s*3500/);
  assert.match(extension, /MAX_CONTEXT_CHARS\s*=\s*56000/);
  assert.match(extension, /MAX_DIAGNOSTICS\s*=\s*80/);
  assert.match(extension, /vscode\.workspace\.textDocuments/);
  assert.match(extension, /vscode\.languages\.getDiagnostics\(\)/);
});

test('mutating editor actions use coding authority while explain and review stay read-only', () => {
  assert.match(extension, /parux\.fixSelection[\s\S]{0,180}startEditorCodingTask\('fix'/);
  assert.match(extension, /parux\.testSelection[\s\S]{0,180}startEditorCodingTask\('test'/);
  assert.match(extension, /parux\.hunkFeedback[\s\S]{0,900}startEditorCodingTask\('hunk-feedback'/);
  assert.match(extension, /parux\.explainSelection[\s\S]{0,180}runReadOnlyEditorTask\('explain'/);
  assert.match(extension, /parux\.reviewSelection[\s\S]{0,180}runReadOnlyEditorTask\('review'/);
});

test('hunk feedback requires explicit reviewer text before a write-capable task starts', () => {
  assert.match(extension, /prompt:\s*'What should PaRuX change in this selected hunk\?'/);
  assert.match(extension, /validateInput:\s*value\s*=>\s*value\.trim\(\)\s*\?\s*undefined\s*:\s*'Reviewer feedback is required\.'/);
});

test('accept and reject hunk stay human Git decisions rather than AI write tasks', () => {
  assert.match(hunkReview, /parux\.acceptHunk/);
  assert.match(hunkReview, /git\.stageSelectedRanges/);
  assert.match(hunkReview, /parux\.rejectHunk/);
  assert.match(hunkReview, /git\.revertSelectedRanges/);
  assert.match(hunkReview, /showWarningMessage[\s\S]{0,400}'Reject hunk'/);
  assert.doesNotMatch(hunkReview, /createCodingTask|createTask|PaRuXCoreClient/);
});
