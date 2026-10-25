'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { REVIEW_STATE_KEY, registerHunkReviewCommands } = require('../hunk-review');

function rangeOf(selection) {
  return {
    start: { line: selection.start.line, character: selection.start.character },
    end: { line: selection.end.line, character: selection.end.character }
  };
}

function createHarness(initialText, {
  startLine = 1,
  endLine = 2,
  endCharacter = 5,
  afterCaptureText,
  dirty = false,
  saveResult = true
} = {}) {
  let text = initialText;
  let textReads = 0;
  let onWarning = null;
  const handlers = new Map();
  const commandCalls = [];
  const stateUpdates = [];
  const warningCalls = [];
  const informationMessages = [];
  const errorMessages = [];
  const store = new Map();
  let saveCalls = 0;

  class Position {
    constructor(line, character) { this.line = line; this.character = character; }
  }
  class Selection {
    constructor(start, end) {
      this.start = start;
      this.end = end;
      this.isEmpty = start.line === end.line && start.character === end.character;
    }
  }

  const document = {
    uri: { scheme: 'file', toString: () => 'file:///repo/example.js' },
    isDirty: dirty,
    getText: () => {
      const capturedText = text;
      textReads++;
      if (textReads === 1 && afterCaptureText !== undefined)
        queueMicrotask(() => { text = afterCaptureText; });
      return capturedText;
    },
    save: async () => { saveCalls++; return saveResult; },
    lineAt: line => {
      const value = text.split(/\r\n|\n|\r/)[line] || '';
      return { range: { end: new Position(line, value.length) } };
    }
  };
  const editor = {
    document,
    selection: new Selection(new Position(startLine, 0), new Position(endLine, endCharacter)),
    revealRange() {}
  };

  const vscode = {
    Position,
    Selection,
    TextEditorRevealType: { InCenterIfOutsideViewport: 1 },
    window: {
      activeTextEditor: editor,
      showInformationMessage: message => informationMessages.push(message),
      showErrorMessage: message => errorMessages.push(message),
      showWarningMessage: async (...args) => {
        warningCalls.push(args);
        if (onWarning) onWarning();
        return 'Reject hunk';
      }
    },
    workspace: { asRelativePath: () => 'src/example.js' },
    commands: {
      registerCommand: (name, handler) => {
        handlers.set(name, handler);
        return { dispose() {} };
      },
      executeCommand: async (name, ...args) => {
        commandCalls.push({ command: name, args, range: rangeOf(editor.selection) });
      }
    }
  };
  const workspaceState = {
    get: (key, fallback) => store.has(key) ? store.get(key) : fallback,
    update: async (key, value) => {
      stateUpdates.push({ key, value });
      store.set(key, value);
    }
  };

  const guarded = async (_label, action) => {
    try { return await action(); }
    catch (error) {
      vscode.window.showErrorMessage(`PaRuX: ${error.message}`);
      return undefined;
    }
  };
  registerHunkReviewCommands(vscode, guarded, workspaceState);
  return {
    handlers,
    commandCalls,
    stateUpdates,
    warningCalls,
    informationMessages,
    errorMessages,
    saveCalls: () => saveCalls,
    ledger: () => store.get(REVIEW_STATE_KEY) || [],
    setText: value => { text = value; },
    setWarningEffect: effect => { onWarning = effect; }
  };
}

test('accept stages only the revalidated range and records one accepted decision', async () => {
  const h = createHarness('head\none\ntwo\ntail', {
    afterCaptureText: 'inserted\nhead\none\ntwo\ntail'
  });

  await h.handlers.get('parux.acceptHunk')();

  assert.deepEqual(h.commandCalls, [{
    command: 'git.stageSelectedRanges',
    args: [],
    range: {
      start: { line: 2, character: 0 },
      end: { line: 3, character: 3 }
    }
  }]);
  assert.equal(h.stateUpdates.length, 1);
  assert.equal(h.stateUpdates[0].key, REVIEW_STATE_KEY);
  assert.equal(h.ledger().length, 1);
  assert.equal(h.ledger()[0].action, 'accepted');
  assert.equal(h.ledger()[0].originalStartLine, 1);
  assert.equal(h.ledger()[0].resolvedStartLine, 2);
  assert.equal(h.ledger()[0].relocated, true);
});

test('reject reverts only the revalidated range and excludes the neighbouring line', async () => {
  const h = createHarness('head\nfirst\nsecond\nneighbor\ntail', { endCharacter: 6 });
  h.setWarningEffect(() => h.setText('inserted\nhead\nfirst\nsecond\nneighbor\ntail'));

  await h.handlers.get('parux.rejectHunk')();

  assert.deepEqual(h.commandCalls, [{
    command: 'git.revertSelectedRanges',
    args: [],
    range: {
      start: { line: 2, character: 0 },
      end: { line: 3, character: 6 }
    }
  }]);
  assert.equal(h.stateUpdates.length, 1);
  assert.equal(h.ledger().length, 1);
  assert.equal(h.ledger()[0].action, 'rejected');
  assert.equal(h.ledger()[0].resolvedStartLine, 2);
  assert.equal(h.ledger()[0].lineCount, 2);
  assert.equal(h.ledger()[0].relocated, true);
});

test('rejecting a stale hunk displays the stale message without a command or decision', async () => {
  const h = createHarness('head\none\ntwo\ntail');
  h.setWarningEffect(() => h.setText('head\nchanged\ntwo\ntail'));

  await h.handlers.get('parux.rejectHunk')();

  assert.equal(h.warningCalls.length, 1);
  assert.deepEqual(h.errorMessages, [
    'PaRuX: The selected hunk changed or was deleted before the decision could be applied. Re-open the diff and review it again.'
  ]);
  assert.deepEqual(h.commandCalls, []);
  assert.deepEqual(h.stateUpdates, []);
  assert.deepEqual(h.ledger(), []);
});

test('rejecting an ambiguously anchored hunk displays ambiguity without a command or decision', async () => {
  const h = createHarness('x\ndup\ny\nq\ndup\nz', { startLine: 1, endLine: 1, endCharacter: 3 });
  h.setWarningEffect(() => h.setText('p\ndup\nr\ns\ndup\nt'));

  await h.handlers.get('parux.rejectHunk')();

  assert.equal(h.warningCalls.length, 1);
  assert.deepEqual(h.errorMessages, [
    'PaRuX: The selected hunk now matches multiple locations. PaRuX will not guess which copy to change; re-select the intended hunk.'
  ]);
  assert.deepEqual(h.commandCalls, []);
  assert.deepEqual(h.stateUpdates, []);
  assert.deepEqual(h.ledger(), []);
});

test('accept and reject refuse an unsaved document before any Git command', async () => {
  const h = createHarness('head\none\ntwo\ntail', { dirty: true, saveResult: false });

  for (const command of ['parux.acceptHunk', 'parux.rejectHunk'])
    await h.handlers.get(command)();

  assert.equal(h.saveCalls(), 2);
  assert.equal(h.warningCalls.length, 0);
  assert.deepEqual(h.errorMessages, [
    'PaRuX: Save the file before making a Git hunk decision.',
    'PaRuX: Save the file before making a Git hunk decision.'
  ]);
  assert.deepEqual(h.commandCalls, []);
  assert.deepEqual(h.stateUpdates, []);
  assert.deepEqual(h.ledger(), []);
});
