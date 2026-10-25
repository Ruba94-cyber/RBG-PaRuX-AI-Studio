'use strict';
// BIC-09 S3: editor edge cases for hunk identity and the accept/reject commands - moved and
// deleted lines, overlapping edits, partial accept/reject, restart and error recovery.
const test = require('node:test');
const assert = require('node:assert/strict');
const { captureHunk, resolveHunk, rangesOverlap } = require('../hunk-identity');
const { REVIEW_STATE_KEY, registerHunkReviewCommands } = require('../hunk-review');

function harness(initialText, { start = 1, end = 2, endChar = 5 } = {}) {
  let text = initialText;
  const handlers = new Map();
  const executed = [];
  const store = new Map();
  const messages = [];
  let gitFailure = null;
  let saveResult = true;
  let onWarning = null;
  class Position { constructor(line, character) { this.line = line; this.character = character; } }
  class Selection {
    constructor(a, b) { this.start = a; this.end = b; this.isEmpty = a.line === b.line && a.character === b.character; }
  }
  const uri = { scheme: 'file', toString: () => 'file:///repo/a.js' };
  const document = {
    uri, isDirty: false,
    getText: () => text,
    save: async () => saveResult,
    lineAt: i => ({ range: { end: new Position(i, (text.replace(/\r\n/g, '\n').split('\n')[i] || '').length) } })
  };
  const editor = {
    document,
    selection: new Selection(new Position(start, 0), new Position(end, endChar)),
    revealRange() {}
  };
  const vscode = {
    Position, Selection, TextEditorRevealType: { InCenterIfOutsideViewport: 1 },
    window: {
      activeTextEditor: editor,
      showInformationMessage: m => messages.push(m),
      showWarningMessage: async () => { if (onWarning) onWarning(); return 'Reject hunk'; }
    },
    workspace: { asRelativePath: () => 'a.js' },
    commands: {
      registerCommand: (name, fn) => { handlers.set(name, fn); return { dispose() {} }; },
      executeCommand: async name => {
        if (gitFailure) throw gitFailure;
        executed.push(name);
      }
    }
  };
  const workspaceState = {
    get: (key, fallback) => (store.has(key) ? store.get(key) : fallback),
    update: async (key, value) => store.set(key, value)
  };
  const register = () => registerHunkReviewCommands(vscode, (_l, action) => action(), workspaceState);
  register();
  const select = (s, e, ec = 5) => { editor.selection = new Selection(new Position(s, 0), new Position(e, ec)); };
  return {
    handlers, executed, store, editor, messages, vscode, workspaceState, register, select,
    setText: v => { text = v; },
    setWarning: fn => { onWarning = fn; },
    failGit: err => { gitFailure = err; },
    setSave: v => { saveResult = v; },
    ledger: () => store.get(REVIEW_STATE_KEY) || []
  };
}

// ---- moved and deleted lines ----

test('a hunk moved to a new position by a block move is relocated when it is unique', () => {
  const before = ['alpha', 'beta', 'gamma', 'delta', 'epsilon', 'zeta'].join('\n');
  const hunk = captureHunk(before, 'f', 1, 2); // beta, gamma
  const moved = ['alpha', 'delta', 'epsilon', 'zeta', 'beta', 'gamma'].join('\n');
  const r = resolveHunk(moved, hunk);
  assert.equal(r.status, 'ok');
  assert.equal(r.startLine, 4);
  assert.equal(r.relocated, true);
});

test('a moved hunk with an identical copy elsewhere is ambiguous and never guessed', () => {
  const before = ['x', 'dup', 'y', 'q', 'dup', 'z'].join('\n');
  const hunk = captureHunk(before, 'f', 1, 1);
  // anchors (x/y, q/z) no longer neighbour either copy
  const after = ['p', 'dup', 'r', 's', 'dup', 't'].join('\n');
  assert.equal(resolveHunk(after, hunk).status, 'ambiguous');
});

test('deleting the whole selected hunk makes accept fail closed with no Git call or ledger entry', async () => {
  const h = harness('head\none\ntwo\ntail');
  h.setWarning(() => h.setText('head\ntail'));
  await assert.rejects(h.handlers.get('parux.rejectHunk')(), /changed or was deleted/i);
  assert.deepEqual(h.executed, []);
  assert.deepEqual(h.ledger(), []);
});

test('deleting only part of the hunk invalidates it instead of reverting a different range', async () => {
  const h = harness('head\none\ntwo\ntail');
  h.setWarning(() => h.setText('head\none\ntail'));
  await assert.rejects(h.handlers.get('parux.rejectHunk')(), /changed or was deleted/i);
  assert.deepEqual(h.executed, []);
});

test('a hunk stays valid across a line-ending conversion of the whole file', () => {
  const crlf = 'head\r\none\r\ntwo\r\ntail';
  const hunk = captureHunk(crlf, 'f', 1, 2);
  const lf = resolveHunk('head\none\ntwo\ntail', hunk);
  assert.equal(lf.status, 'ok');
  assert.equal(lf.relocated, false);
});

// ---- overlapping edits ----

test('overlapping hunks are tracked independently: an edit under one leaves the other resolvable', () => {
  const text = ['a', 'b', 'c', 'd', 'e'].join('\n');
  const first = captureHunk(text, 'f', 1, 2); // b,c
  const second = captureHunk(text, 'f', 2, 3); // c,d
  assert.equal(rangesOverlap({ startLine: 1, endLine: 2 }, { startLine: 2, endLine: 3 }), true);
  const edited = ['a', 'b', 'c', 'D!', 'e'].join('\n');
  assert.equal(resolveHunk(edited, first).status, 'ok');
  assert.equal(resolveHunk(edited, second).status, 'stale');
});

test('accepting one hunk then an overlapping one records both decisions without merging them', async () => {
  const h = harness('head\none\ntwo\nthree\ntail', { start: 1, end: 2 });
  await h.handlers.get('parux.acceptHunk')();
  h.select(2, 3);
  await h.handlers.get('parux.acceptHunk')();
  const ledger = h.ledger();
  assert.equal(ledger.length, 2);
  assert.notEqual(ledger[0].hunkId, ledger[1].hunkId);
  assert.deepEqual(ledger.map(x => [x.originalStartLine, x.lineCount]), [[1, 2], [2, 2]]);
  assert.deepEqual(h.executed, ['git.stageSelectedRanges', 'git.stageSelectedRanges']);
});

// ---- partial accept / reject ----

test('partial accept stages only the selected sub-range and a later reject of the rest is separate', async () => {
  const h = harness('head\none\ntwo\nthree\ntail', { start: 1, end: 1 });
  await h.handlers.get('parux.acceptHunk')();
  assert.equal(h.editor.selection.start.line, 1);
  assert.equal(h.editor.selection.end.line, 1, 'only the first line of the hunk is staged');
  h.select(2, 3);
  await h.handlers.get('parux.rejectHunk')();
  assert.deepEqual(h.executed, ['git.stageSelectedRanges', 'git.revertSelectedRanges']);
  assert.deepEqual(h.ledger().map(x => [x.action, x.originalStartLine, x.lineCount]),
    [['accepted', 1, 1], ['rejected', 2, 2]]);
});

test('a selection ending at column 0 of the next line does not fold that untouched line in', async () => {
  const h = harness('head\none\ntwo\ntail', { start: 1, end: 3, endChar: 0 });
  await h.handlers.get('parux.acceptHunk')();
  assert.equal(h.ledger()[0].lineCount, 2);
  assert.equal(h.editor.selection.end.line, 2);
});

test('an empty selection is refused before any Git command', async () => {
  const h = harness('head\none\ntwo\ntail', { start: 1, end: 1, endChar: 0 });
  h.editor.selection = new h.vscode.Selection(new h.vscode.Position(1, 2), new h.vscode.Position(1, 2));
  await assert.rejects(h.handlers.get('parux.acceptHunk')(), /Select the changed lines/);
  assert.deepEqual(h.executed, []);
});

// ---- restart ----

test('a persisted descriptor survives JSON round trip and still resolves after unrelated edits', () => {
  const text = ['h', 'one', 'two', 't'].join('\n');
  const stored = JSON.parse(JSON.stringify(captureHunk(text, 'src/a.cs', 1, 2)));
  const after = ['new', 'h', 'one', 'two', 't'].join('\n');
  const r = resolveHunk(after, stored);
  assert.equal(r.status, 'ok');
  assert.equal(r.startLine, 2);
});

test('a persisted descriptor from an unsupported schema is stale, never trusted', () => {
  const stored = { ...captureHunk('a\nb', 'f', 0, 0), schemaVersion: 99 };
  assert.deepEqual(resolveHunk('a\nb', stored), { status: 'stale', reason: 'unsupported-descriptor' });
});

test('decisions made before a restart are preserved and new ones append after re-registration', async () => {
  const h = harness('head\none\ntwo\ntail');
  await h.handlers.get('parux.acceptHunk')();
  h.register(); // simulated extension-host restart on the same workspaceState
  h.select(3, 3, 4);
  await h.handlers.get('parux.acceptHunk')();
  assert.deepEqual(h.ledger().map(x => x.originalStartLine), [1, 3]);
});

// ---- error recovery ----

test('a failing Git command records no decision and a retry afterwards succeeds', async () => {
  const h = harness('head\none\ntwo\ntail');
  h.failGit(new Error('index.lock exists'));
  await assert.rejects(h.handlers.get('parux.acceptHunk')(), /index\.lock/);
  assert.deepEqual(h.ledger(), []);
  h.failGit(null);
  await h.handlers.get('parux.acceptHunk')();
  assert.equal(h.ledger().length, 1);
  assert.deepEqual(h.executed, ['git.stageSelectedRanges']);
});

test('an unsaved file that cannot be saved blocks the decision before Git is invoked', async () => {
  const h = harness('head\none\ntwo\ntail');
  h.editor.document.isDirty = true;
  h.setSave(false);
  await assert.rejects(h.handlers.get('parux.acceptHunk')(), /Save the file/);
  assert.deepEqual(h.executed, []);
  assert.deepEqual(h.ledger(), []);
});

test('a corrupt persisted ledger is ignored and does not block a new decision', async () => {
  const h = harness('head\none\ntwo\ntail');
  h.store.set(REVIEW_STATE_KEY, [null, { bad: true }, 'x', { schemaVersion: 9, hunkId: 'h' }]);
  await h.handlers.get('parux.acceptHunk')();
  assert.equal(h.ledger().length, 1);
  assert.equal(h.ledger()[0].action, 'accepted');
});

test('switching the active editor while a rejection is pending aborts without reverting anything', async () => {
  const h = harness('head\none\ntwo\ntail');
  h.setWarning(() => {
    h.editor.document.uri = { scheme: 'file', toString: () => 'file:///repo/other.js' };
  });
  await assert.rejects(h.handlers.get('parux.rejectHunk')(), /active editor changed/i);
  assert.deepEqual(h.executed, []);
  assert.deepEqual(h.ledger(), []);
});

test('a non-file editor (untitled or remote diff side) is refused', async () => {
  const h = harness('head\none\ntwo\ntail');
  h.editor.document.uri = { scheme: 'untitled', toString: () => 'untitled:1' };
  await assert.rejects(h.handlers.get('parux.acceptHunk')(), /local Git diff/);
  assert.deepEqual(h.executed, []);
});
