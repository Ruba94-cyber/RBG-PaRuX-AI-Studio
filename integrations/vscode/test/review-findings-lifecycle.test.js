'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const os = require('node:os');
const { pathToFileURL, fileURLToPath } = require('node:url');
const {
  REVIEW_FINDINGS_STATE_KEY,
  isUriPathWithinRoot,
  registerReviewFindings
} = require('../review-findings');

const REVIEW = [
  '# PaRuX inline review',
  '**Source:** `src/worker.js` · task `review-1`',
  '',
  '- [High] src/worker.js:2 - cancellation can leak stale output.'
].join('\n');

function createHarness(saved = [], existingRoot = null) {
  const root = existingRoot || path.join(os.tmpdir(), 'parux-vscode-review-' + Math.random().toString(16).slice(2));
  const sourcePath = path.join(root, 'src', 'worker.js');
  const documents = new Map();
  const files = new Set();
  const realpaths = new Map();
  const opened = [];
  const changed = [];
  const closed = [];
  const deleted = [];
  const commandHandlers = new Map();
  const diagnostics = new Map();
  const warnings = [];
  const stored = new Map([[REVIEW_FINDINGS_STATE_KEY, structuredClone(saved)]]);
  let treeProvider = null;
  let shownDocuments = 0;

  function makeUri(fsPath) {
    const absolute = path.resolve(fsPath);
    const uri = {
      scheme: 'file',
      fsPath: absolute,
      toString: () => pathToFileURL(absolute).href
    };
    return uri;
  }

  const uriPaths = new Map();
  function rememberUri(uri) {
    uriPaths.set(uri.toString(), uri.fsPath);
    return uri;
  }
  function documentFor(uri, text, languageId = 'javascript') {
    let currentText = text;
    const document = {
      uri,
      languageId,
      getText: () => currentText,
      setText: value => { currentText = value; },
      get lineCount() { return currentText.split(/\r\n|\n|\r/).length; },
      lineAt(index) {
        const lines = currentText.split(/\r\n|\n|\r/);
        const value = lines[index];
        if (value === undefined) throw new RangeError('line outside document');
        return { text: value, range: { end: new Position(index, value.length) } };
      }
    };
    return document;
  }

  class Position { constructor(line, character) { this.line = line; this.character = character; } }
  class Range { constructor(start, end) { this.start = start; this.end = end; } }
  class TreeItem { constructor(label) { this.label = label; } }
  class ThemeIcon { constructor(id) { this.id = id; } }

  const rootUri = rememberUri(makeUri(root));
  function addFile(fsPath, text) {
    const absolute = path.resolve(fsPath);
    const document = documentFor(rememberUri(makeUri(absolute)), text);
    documents.set(absolute, document);
    files.add(absolute);
    return document;
  }
  addFile(sourcePath, 'header\ncancel()\ntail');
  const vscode = {
    Uri: {
      file: filePath => rememberUri(makeUri(filePath)),
      parse: value => {
        const knownPath = uriPaths.get(value);
        return knownPath ? rememberUri(makeUri(knownPath)) : rememberUri(makeUri(fileURLToPath(value)));
      },
      joinPath: (base, ...segments) => rememberUri(makeUri(path.join(base.fsPath, ...segments)))
    },
    FileType: { Directory: 2, File: 1 },
    Position,
    Range,
    Selection: Range,
    DiagnosticSeverity: { Warning: 1, Information: 3 },
    Diagnostic: class Diagnostic {
      constructor(range, message, severity) { this.range = range; this.message = message; this.severity = severity; }
    },
    TreeItem,
    TreeItemCollapsibleState: { None: 0 },
    ThemeIcon,
    TextEditorRevealType: { InCenterIfOutsideViewport: 0 },
    workspace: {
      workspaceFolders: [{ name: 'repo', uri: rootUri }],
      textDocuments: [],
      fs: {
        async stat(uri) {
          if (!files.has(uri.fsPath)) throw new Error('File not found');
          return { type: vscode.FileType.File };
        }
      },
      async openTextDocument(uriOrOptions) {
        if (uriOrOptions && typeof uriOrOptions === 'object' && 'content' in uriOrOptions) {
          const document = documentFor(rememberUri(makeUri(path.join(root, 'review.md'))), uriOrOptions.content, uriOrOptions.language);
          documents.set(document.uri.fsPath, document);
          return document;
        }
        const fsPath = typeof uriOrOptions === 'string' ? uriOrOptions : uriOrOptions.fsPath;
        const document = documents.get(path.resolve(fsPath));
        if (!document) throw new Error('File not found');
        return document;
      },
      onDidOpenTextDocument(handler) { opened.push(handler); return { dispose() {} }; },
      onDidChangeTextDocument(handler) { changed.push(handler); return { dispose() {} }; },
      onDidCloseTextDocument(handler) { closed.push(handler); return { dispose() {} }; },
      onDidDeleteFiles(handler) { deleted.push(handler); return { dispose() {} }; }
    },
    languages: {
      createDiagnosticCollection() {
        return {
          clear: () => diagnostics.clear(),
          set: (uri, values) => diagnostics.set(uri.toString(), values),
          dispose() {}
        };
      },
    },
    EventEmitter: class EventEmitter { constructor() { this.event = () => ({ dispose() {} }); } fire() {} dispose() {} },
    TreeItem,
    window: {
      createTreeView(_id, options) { treeProvider = options.treeDataProvider; return { dispose() {} }; },
      showInformationMessage() {},
      showWarningMessage(message) { warnings.push(message); },
      async showTextDocument(document) {
        shownDocuments++;
        return { document, revealRange() {}, selection: null };
      }
    },
    commands: {
      registerCommand(name, handler) { commandHandlers.set(name, handler); return { dispose() {} }; }
    }
  };
  vscode.window.createTreeView = (_id, options) => {
    treeProvider = options.treeDataProvider;
    return { dispose() {} };
  };
  const workspaceState = {
    get(key, fallback) { return stored.has(key) ? stored.get(key) : fallback; },
    async update(key, value) { stored.set(key, structuredClone(value)); }
  };

  registerReviewFindings(vscode, workspaceState, {
    realpath: async fsPath => realpaths.get(path.resolve(fsPath)) || path.resolve(fsPath)
  });

  return {
    vscode,
    root,
    sourcePath,
    addFile,
    documents,
    files,
    realpaths,
    diagnostics,
    stored,
    opened,
    changed,
    closed,
    deleted,
    commandHandlers,
    warnings,
    get treeProvider() { return treeProvider; },
    get shownDocuments() { return shownDocuments; }
  };
}

async function flushEvents() {
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));
}

async function ingest(harness, markdown = REVIEW) {
  const document = await harness.vscode.workspace.openTextDocument({ language: 'markdown', content: markdown });
  await Promise.all(harness.opened.map(handler => handler(document)));
  await flushEvents();
}

async function changeSource(harness, text) {
  const document = harness.documents.get(harness.sourcePath);
  document.setText(text);
  for (const handler of harness.changed) handler({ document });
  await flushEvents();
}

test('review diagnostics relocate, then remain visibly stale and never guess after target edits', async () => {
  const harness = createHarness();
  await ingest(harness);
  let current = [...harness.diagnostics.values()][0][0];
  assert.equal(current.range.start.line, 1);

  await changeSource(harness, 'new header\nheader\ncancel()\ntail');
  current = [...harness.diagnostics.values()][0][0];
  assert.equal(current.range.start.line, 2);
  let [item] = await harness.treeProvider.getChildren();
  assert.equal(item.locationStatus, 'relocated');
  assert.match(item.description, /relocated/);

  await changeSource(harness, 'new header\nheader\nreturn safely\ntail');
  assert.equal(harness.diagnostics.size, 0);
  [item] = await harness.treeProvider.getChildren();
  assert.equal(item.locationStatus, 'stale');
  assert.equal(item.finding.state, 'open');
  assert.equal(harness.stored.get(REVIEW_FINDINGS_STATE_KEY)[0].state, 'open');
  assert.doesNotMatch(JSON.stringify(harness.stored.get(REVIEW_FINDINGS_STATE_KEY)), /cancel\(\)/);

  await harness.commandHandlers.get('parux.openReviewFinding')(item);
  assert.equal(harness.shownDocuments, 0);
  assert.match(harness.warnings.at(-1), /stale or ambiguous/);
});

test('removed review targets are marked unavailable and are not opened at their former line', async () => {
  const harness = createHarness();
  await ingest(harness);
  const cachedDocument = harness.documents.get(harness.sourcePath);
  harness.files.delete(harness.sourcePath); // VS Code may still return this open cached document.
  for (const handler of harness.deleted) handler({ files: [cachedDocument.uri] });
  await flushEvents();

  const [item] = await harness.treeProvider.getChildren();
  assert.equal(item.locationStatus, 'unavailable');
  assert.equal(harness.diagnostics.size, 0);
  await harness.commandHandlers.get('parux.openReviewFinding')(item);
  assert.equal(harness.shownDocuments, 0);
  assert.equal(harness.stored.get(REVIEW_FINDINGS_STATE_KEY)[0].state, 'open');
});

test('re-ingest and extension reload preserve disposition without storing source lines', async () => {
  const first = createHarness();
  await ingest(first);
  let [item] = await first.treeProvider.getChildren();
  await first.commandHandlers.get('parux.resolveReviewFinding')(item);
  assert.equal(first.stored.get(REVIEW_FINDINGS_STATE_KEY)[0].state, 'resolved');
  await ingest(first);

  const persisted = first.stored.get(REVIEW_FINDINGS_STATE_KEY);
  assert.equal(persisted.length, 1);
  assert.equal(persisted[0].state, 'resolved');
  assert.doesNotMatch(JSON.stringify(persisted), /cancel\(\)/);

  const reloaded = createHarness(JSON.parse(JSON.stringify(persisted)), first.root);
  await ingest(reloaded);
  const [restored] = await reloaded.treeProvider.getChildren();
  assert.equal(restored.finding.state, 'resolved');
  assert.equal(reloaded.diagnostics.size, 0);
});

test('a persisted finding from a different workspace root is unavailable and never opened', async () => {
  const original = createHarness();
  await ingest(original);
  const persisted = JSON.parse(JSON.stringify(original.stored.get(REVIEW_FINDINGS_STATE_KEY)));

  const movedWorkspace = createHarness(persisted);
  await flushEvents();
  const [item] = await movedWorkspace.treeProvider.getChildren();
  assert.equal(item.locationStatus, 'unavailable');
  assert.equal(item.finding.state, 'open');
  assert.equal(movedWorkspace.diagnostics.size, 0);
  await movedWorkspace.commandHandlers.get('parux.openReviewFinding')(item);
  assert.equal(movedWorkspace.shownDocuments, 0);
});

test('review Markdown cannot escape the workspace to attach a finding to an outside file', async () => {
  const harness = createHarness();
  const root = path.dirname(path.dirname(harness.sourcePath));
  const outsidePath = path.resolve(root, '..', '..', 'outside.js');
  harness.documents.set(outsidePath, {
    uri: harness.vscode.Uri.file(outsidePath),
    getText: () => 'secretOutsideWorkspace()',
    lineCount: 1,
    lineAt: () => ({ range: { end: new (class { constructor(line, character) { this.line = line; this.character = character; } })(0, 24) } })
  });
  const markdown = [
    '# PaRuX inline review',
    '**Source:** `../../outside.js` · task `review-escape`',
    '',
    '- [High] ../../outside.js:1 - do not resolve outside this workspace.'
  ].join('\n');
  await ingest(harness, markdown);

  assert.equal(harness.stored.get(REVIEW_FINDINGS_STATE_KEY).length, 0);
  assert.equal(harness.diagnostics.size, 0);
});

test('a symlinked workspace entry resolving outside the workspace is rejected', async () => {
  const harness = createHarness();
  const linkTarget = path.join(harness.root, 'linked', 'secret.js');
  const outsideTarget = path.resolve(harness.root, '..', 'private', 'secret.js');
  harness.addFile(linkTarget, 'privateData()');
  harness.realpaths.set(linkTarget, outsideTarget);
  const markdown = [
    '# PaRuX inline review',
    '**Source:** `linked/secret.js` · task `review-symlink`',
    '',
    '- [High] linked/secret.js:1 - do not resolve through an external link.'
  ].join('\n');
  await ingest(harness, markdown);

  assert.equal(harness.stored.get(REVIEW_FINDINGS_STATE_KEY).length, 0);
  assert.equal(harness.diagnostics.size, 0);
});

test('provider-backed workspace containment matches scheme, authority and path boundaries', () => {
  const root = { scheme: 'vscode-remote', authority: 'ssh-remote+host-a', path: '/workspace/repo' };
  assert.equal(isUriPathWithinRoot(root, {
    scheme: 'vscode-remote', authority: 'ssh-remote+host-a', path: '/workspace/repo/src/app.cs'
  }), true);
  assert.equal(isUriPathWithinRoot(root, {
    scheme: 'vscode-remote', authority: 'ssh-remote+host-a', path: '/workspace/repository-secret/app.cs'
  }), false);
  assert.equal(isUriPathWithinRoot(root, {
    scheme: 'vscode-remote', authority: 'ssh-remote+host-b', path: '/workspace/repo/src/app.cs'
  }), false);
  assert.equal(isUriPathWithinRoot(root, {
    scheme: 'file', authority: '', path: '/workspace/repo/src/app.cs'
  }), false);
});
