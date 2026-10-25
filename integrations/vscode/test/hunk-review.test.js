'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { REVIEW_STATE_KEY, registerHunkReviewCommands } = require('../hunk-review');

function harness(initialText, startLine=1, endLine=2) {
  let text = initialText;
  const handlers = new Map();
  const executed = [];
  const store = new Map();
  class Position { constructor(line, character) { this.line=line; this.character=character; } }
  class Selection { constructor(start, end) { this.start=start; this.end=end; this.isEmpty=false; } }
  const uri = { scheme:'file', toString:()=> 'file:///repo/a.js' };
  const document = {
    uri, isDirty:false,
    getText:()=>text,
    save:async()=>true,
    lineAt:i=>({ range:{ end:new Position(i, (text.split('\n')[i] || '').length) } })
  };
  const editor = {
    document,
    selection:new Selection(new Position(startLine,0), new Position(endLine,5)),
    revealRange(){}
  };
  let onWarning = null;
  const vscode = {
    Position, Selection, TextEditorRevealType:{InCenterIfOutsideViewport:1},
    window:{
      activeTextEditor:editor,
      showInformationMessage(){},
      showWarningMessage:async()=>{ if (onWarning) onWarning(); return 'Reject hunk'; }
    },
    workspace:{ asRelativePath:()=> 'a.js' },
    commands:{
      registerCommand:(name, fn)=>{ handlers.set(name, fn); return {dispose(){}}; },
      executeCommand:async name=>{ executed.push(name); }
    }
  };
  const workspaceState = {
    get:(key,fallback)=>store.has(key)?store.get(key):fallback,
    update:async(key,value)=>store.set(key,value)
  };
  registerHunkReviewCommands(vscode, (_label, action)=>action(), workspaceState);
  return { handlers, executed, store, editor, setText:value=>{text=value;}, setWarning:fn=>{onWarning=fn;}, workspaceState, vscode };
}

test('accept stages exactly the revalidated whole-line selection and persists decision', async () => {
  const h = harness('head\none\ntwo\ntail');
  await h.handlers.get('parux.acceptHunk')();
  assert.deepEqual(h.executed, ['git.stageSelectedRanges']);
  assert.equal(h.editor.selection.start.line, 1);
  assert.equal(h.editor.selection.end.line, 2);
  const ledger = h.store.get(REVIEW_STATE_KEY);
  assert.equal(ledger.length, 1);
  assert.equal(ledger[0].action, 'accepted');
  assert.equal(ledger[0].relocated, false);
});

test('reject relocates after unrelated edit during confirmation before invoking Git', async () => {
  const h = harness('head\none\ntwo\ntail');
  h.setWarning(()=>h.setText('insert\nhead\none\ntwo\ntail'));
  await h.handlers.get('parux.rejectHunk')();
  assert.deepEqual(h.executed, ['git.revertSelectedRanges']);
  assert.equal(h.editor.selection.start.line, 2);
  assert.equal(h.editor.selection.end.line, 3);
  const ledger = h.store.get(REVIEW_STATE_KEY);
  assert.equal(ledger[0].action, 'rejected');
  assert.equal(ledger[0].relocated, true);
});

test('reject fails closed if selected lines change during confirmation', async () => {
  const h = harness('head\none\ntwo\ntail');
  h.setWarning(()=>h.setText('head\nchanged\ntwo\ntail'));
  await assert.rejects(h.handlers.get('parux.rejectHunk')(), /changed or was deleted/i);
  assert.deepEqual(h.executed, []);
  assert.equal(h.store.has(REVIEW_STATE_KEY), false);
});

test('accepted and rejected decisions survive extension registration reload', async () => {
  const h = harness('head\none\ntwo\ntail');
  await h.handlers.get('parux.acceptHunk')();
  h.editor.selection = new h.vscode.Selection(new h.vscode.Position(3,0), new h.vscode.Position(3,4));
  registerHunkReviewCommands(h.vscode, (_label, action)=>action(), h.workspaceState);
  await h.handlers.get('parux.rejectHunk')();
  const ledger = h.store.get(REVIEW_STATE_KEY);
  assert.deepEqual(ledger.map(x=>x.action), ['accepted','rejected']);
});
