'use strict';

const path = require('node:path');
const fs = require('node:fs/promises');
const { captureHunk, resolveHunk, sha256 } = require('./hunk-identity');

const REVIEW_FINDINGS_STATE_KEY = 'parux.reviewFindings.v1';
const MAX_FINDINGS = 200;
const SEVERITY_ORDER = { critical: 0, high: 1, medium: 2, low: 3, info: 4 };
const SEVERITY_WORDS = 'critical|high|medium|low|info|error|warning|blocker|major|minor|p[0-3]';

function normalizeSeverity(value) {
  const text = String(value || '').trim().toLowerCase();
  if (text === 'error' || text === 'blocker' || text === 'p0') return 'critical';
  if (text === 'warning' || text === 'major' || text === 'p1') return 'high';
  if (text === 'moderate' || text === 'p2') return 'medium';
  if (text === 'minor' || text === 'p3') return 'low';
  return Object.prototype.hasOwnProperty.call(SEVERITY_ORDER, text) ? text : null;
}

function stripMarkdown(value) {
  return String(value || '')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/\*\*([^*]+)\*\*/g, '$1')
    .replace(/^\s*(?:[-*+]\s+|\d+[.)]\s+)/, '')
    .trim();
}

function parseLocation(text, defaultFile) {
  const normalized = String(text || '');
  const explicit = normalized.match(/(?:`)?([A-Za-z0-9_.@+\-/\\ ]+\.[A-Za-z0-9]+):([0-9]+)(?:[-:]([0-9]+))?(?:`)?/);
  if (explicit) {
    const start = Number(explicit[2]);
    const end = explicit[3] ? Number(explicit[3]) : start;
    return {
      file: explicit[1].trim().replace(/\\/g, '/'),
      startLine: Math.max(1, start),
      endLine: Math.max(Math.max(1, start), end),
      matched: explicit[0]
    };
  }
  const lineOnly = normalized.match(/\b(?:lines?|L)\s*([0-9]+)(?:\s*[-–:]\s*([0-9]+))?/i);
  if (lineOnly && defaultFile) {
    const start = Number(lineOnly[1]);
    const end = lineOnly[2] ? Number(lineOnly[2]) : start;
    return {
      file: defaultFile,
      startLine: Math.max(1, start),
      endLine: Math.max(Math.max(1, start), end),
      matched: lineOnly[0]
    };
  }
  return null;
}

function leadingSeverity(line) {
  const marker = String(line || '').match(new RegExp(
    `^\\s*(?:[-*+]\\s+|\\d+[.)]\\s+)?(?:\\[(${SEVERITY_WORDS})\\]|\\*\\*(${SEVERITY_WORDS})\\*\\*|(${SEVERITY_WORDS})\\s*:)`,
    'i'));
  if (!marker) return null;
  const raw = marker[1] || marker[2] || marker[3];
  return { severity: normalizeSeverity(raw), matched: marker[0] };
}

/** Parse the read-only review Markdown already produced by extension.js. No model-specific JSON is
 * trusted: only lines that carry both a severity and a concrete source location become findings. */
function parseReviewMarkdown(markdown) {
  const text = String(markdown || '');
  if (!/^# PaRuX inline review\b/m.test(text)) return [];
  const source = text.match(/\*\*Source:\*\*\s*`([^`]+)`/)?.[1]?.replace(/\\/g, '/') || '';
  const taskId = text.match(/\btask\s*`([^`]+)`/)?.[1] || '';
  const lines = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
  const findings = [];
  let headingSeverity = null;

  for (const line of lines) {
    const heading = line.match(new RegExp(`^#{2,6}\\s*(?:\\[)?(${SEVERITY_WORDS})(?:\\])?\\b`, 'i'));
    if (heading) {
      headingSeverity = normalizeSeverity(heading[1]);
      continue;
    }

    const inline = leadingSeverity(line);
    const severity = inline?.severity || headingSeverity;
    if (!severity) continue;
    const location = parseLocation(line, source);
    if (!location) continue;

    let message = stripMarkdown(line);
    if (inline) {
      const cleanLeading = message.match(new RegExp(`^(?:\\[)?(${SEVERITY_WORDS})(?:\\])?\\s*[:—–-]*\\s*`, 'i'));
      if (cleanLeading) message = message.slice(cleanLeading[0].length);
    }
    const cleanLocation = parseLocation(message, source);
    if (cleanLocation) message = message.replace(cleanLocation.matched, '').trim();
    message = message.replace(/^\s*[:—–-]+\s*/, '').trim();
    if (!message) message = `${severity} review finding`;
    findings.push({
      severity,
      file: location.file,
      startLine: location.startLine,
      endLine: location.endLine,
      message,
      taskId
    });
  }

  const seen = new Set();
  return findings.filter(item => {
    const key = `${item.severity}|${item.file}|${item.startLine}|${item.endLine}|${item.message}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function findingIdentity(item) {
  const descriptor = item?.descriptor || {};
  const severity = normalizeSeverity(item?.severity);
  const fileUri = String(item?.fileUri || descriptor.fileKey || '').replace(/\\/g, '/');
  const message = String(item?.message || '').trim();
  if (!fileUri || !severity || !message) return null;
  return sha256([
    1,
    fileUri,
    descriptor.selectedHash || descriptor.id || '-',
    descriptor.beforeHash || '-',
    descriptor.afterHash || '-',
    Number.isInteger(descriptor.lineCount) ? descriptor.lineCount : 0,
    severity,
    message
  ].join('|'));
}

function isUriPathWithinRoot(rootUri, candidateUri) {
  if (!rootUri || !candidateUri
    || rootUri.scheme !== candidateUri.scheme
    || String(rootUri.authority || '') !== String(candidateUri.authority || '')) return false;
  if (rootUri.scheme === 'file') return null; // File URIs require realpath containment instead.
  const rootPath = path.posix.resolve(String(rootUri.path || '/'));
  const candidatePath = path.posix.resolve(String(candidateUri.path || '/'));
  const relative = path.posix.relative(rootPath, candidatePath);
  return relative === '' || (relative !== '..' && !relative.startsWith('../') && !path.posix.isAbsolute(relative));
}

function normalizeStoredFindings(value) {
  if (!Array.isArray(value)) return [];
  return value.filter(item => item
    && item.schemaVersion === 1
    && typeof item.id === 'string'
    && typeof item.fileUri === 'string'
    && item.descriptor
    && ['open', 'resolved', 'dismissed'].includes(item.state)
    && normalizeSeverity(item.severity)).slice(-MAX_FINDINGS);
}

function mergeFindings(existing, incoming) {
  const map = new Map();
  for (const item of normalizeStoredFindings(existing)) {
    const key = findingIdentity(item) || item.id;
    const previous = map.get(key);
    if (!previous
      || (previous.state === 'open' && item.state !== 'open')
      || (previous.state === item.state && String(item.createdUtc).localeCompare(String(previous.createdUtc)) > 0)) {
      map.set(key, item);
    }
  }
  for (const item of incoming) {
    const stableId = findingIdentity(item) || item.id;
    const previous = map.get(stableId);
    map.set(stableId, previous
      ? { ...item, id: stableId, state: previous.state, createdUtc: previous.createdUtc }
      : { ...item, id: stableId });
  }
  return [...map.values()]
    .sort((a, b) => String(a.createdUtc).localeCompare(String(b.createdUtc)))
    .slice(-MAX_FINDINGS);
}

function registerReviewFindings(vscode, workspaceState, fileSystem = fs) {
  const emitter = new vscode.EventEmitter();
  const diagnostics = vscode.languages.createDiagnosticCollection('parux-review');
  let findings = normalizeStoredFindings(workspaceState?.get(REVIEW_FINDINGS_STATE_KEY, []));

  async function persist() {
    if (workspaceState?.update) await workspaceState.update(REVIEW_FINDINGS_STATE_KEY, findings);
  }

  async function resolveFile(relativeFile) {
    const normalized = String(relativeFile || '').trim().replace(/\\/g, '/');
    const segments = normalized.split('/');
    if (!normalized
      || path.posix.isAbsolute(normalized)
      || path.win32.isAbsolute(normalized)
      || segments.some(segment => !segment || segment === '.' || segment === '..')) return null;
    for (const folder of vscode.workspace.workspaceFolders || []) {
      const uri = vscode.Uri.joinPath(folder.uri, ...segments);
      if (folder.uri.scheme === 'file' && uri.scheme === 'file' && folder.uri.fsPath && uri.fsPath) {
        const relative = path.relative(path.resolve(folder.uri.fsPath), path.resolve(uri.fsPath));
        if (relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) continue;
      }
      try {
        if (!await isWithinWorkspace(uri)) continue;
        const stat = await vscode.workspace.fs.stat(uri);
        if (stat.type !== vscode.FileType.Directory) return uri;
      } catch { }
    }
    return null;
  }

  async function isWithinWorkspace(uri) {
    if (!uri?.scheme) return false;
    for (const folder of vscode.workspace.workspaceFolders || []) {
      if (folder.uri?.scheme !== uri.scheme
        || String(folder.uri.authority || '') !== String(uri.authority || '')) continue;
      if (uri.scheme !== 'file') {
        if (!isUriPathWithinRoot(folder.uri, uri)) continue;
        try {
          const relative = path.posix.relative(
            path.posix.resolve(String(folder.uri.path || '/')),
            path.posix.resolve(String(uri.path || '/')));
          let current = folder.uri;
          for (const segment of relative ? relative.split('/') : []) {
            current = vscode.Uri.joinPath(current, segment);
            const stat = await vscode.workspace.fs.stat(current);
            const symbolicLink = vscode.FileType.SymbolicLink || 64;
            if ((stat.type & symbolicLink) !== 0) return false;
          }
          return true;
        } catch { continue; }
      }
      if (!folder.uri.fsPath || !uri.fsPath) continue;
      try {
        const [rootPath, candidatePath] = await Promise.all([
          fileSystem.realpath(folder.uri.fsPath),
          fileSystem.realpath(uri.fsPath)
        ]);
        const relative = path.relative(rootPath, candidatePath);
        if (relative === '' || (relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative)))
          return true;
      } catch { }
    }
    return false;
  }

  async function captureParsedFinding(parsed) {
    const uri = await resolveFile(parsed.file);
    if (!uri) return null;
    const document = await vscode.workspace.openTextDocument(uri);
    if (document.lineCount < 1) return null;
    const start = Math.min(document.lineCount - 1, Math.max(0, parsed.startLine - 1));
    const end = Math.min(document.lineCount - 1, Math.max(start, parsed.endLine - 1));
    const descriptor = captureHunk(document.getText(), uri.toString(), start, end);
    const id = findingIdentity({ fileUri: uri.toString(), severity: parsed.severity, message: parsed.message, descriptor });
    return {
      schemaVersion: 1,
      id,
      fileUri: uri.toString(),
      displayFile: parsed.file,
      severity: parsed.severity,
      message: parsed.message,
      taskId: parsed.taskId || '',
      state: 'open',
      descriptor,
      createdUtc: new Date().toISOString()
    };
  }

  async function ingestDocument(document) {
    if (document.languageId !== 'markdown') return;
    const parsed = parseReviewMarkdown(document.getText());
    if (!parsed.length) return;
    const captured = [];
    for (const item of parsed) {
      const finding = await captureParsedFinding(item);
      if (finding) captured.push(finding);
    }
    if (!captured.length) return;
    findings = mergeFindings(findings, captured);
    await persist();
    await refresh();
    vscode.window.showInformationMessage(`PaRuX: ${captured.length} review finding(s) attached to source locations.`);
  }

  async function resolveCurrent(item) {
    const uri = vscode.Uri.parse(item.fileUri);
    if (!await isWithinWorkspace(uri)) throw new Error('Review finding target is no longer an existing workspace file.');
    await vscode.workspace.fs.stat(uri);
    const document = await vscode.workspace.openTextDocument(uri);
    return { document, resolution: resolveHunk(document.getText(), item.descriptor) };
  }

  async function refresh() {
    diagnostics.clear();
    const byUri = new Map();
    for (const item of findings.filter(f => f.state === 'open')) {
      let current;
      try { current = await resolveCurrent(item); } catch { continue; }
      if (current.resolution.status !== 'ok') continue;
      const range = new vscode.Range(
        new vscode.Position(current.resolution.startLine, 0),
        current.document.lineAt(current.resolution.endLine).range.end);
      const diagnostic = new vscode.Diagnostic(
        range,
        `[${item.severity.toUpperCase()}] ${item.message}`,
        item.severity === 'critical' || item.severity === 'high'
          ? vscode.DiagnosticSeverity.Warning
          : vscode.DiagnosticSeverity.Information);
      diagnostic.source = 'PaRuX Review';
      diagnostic.code = item.id;
      const key = item.fileUri;
      const entry = byUri.get(key) || { uri: vscode.Uri.parse(key), values: [] };
      entry.values.push(diagnostic);
      byUri.set(key, entry);
    }
    for (const { uri, values } of byUri.values()) diagnostics.set(uri, values);
    emitter.fire(undefined);
  }

  class FindingItem extends vscode.TreeItem {
    constructor(item, resolution, locationStatus) {
      super(item.message, vscode.TreeItemCollapsibleState.None);
      this.finding = item;
      this.contextValue = `paruxReviewFinding.${item.state}`;
      const line = (resolution?.startLine ?? item.descriptor.originalStartLine) + 1;
      const locationLabel = locationStatus === 'ok'
        ? (resolution?.relocated ? 'relocated' : 'current')
        : locationStatus;
      this.locationStatus = locationLabel;
      this.description = `${item.severity} · ${item.displayFile}:${line} · ${item.state} · ${locationLabel}`;
      this.tooltip = `${item.severity.toUpperCase()} · ${item.displayFile}:${line} · ${locationLabel}\n${item.message}`;
      this.iconPath = new vscode.ThemeIcon(item.state === 'resolved' ? 'pass-filled' : item.state === 'dismissed' ? 'circle-slash' : 'issues');
      this.command = { command: 'parux.openReviewFinding', title: 'Open finding', arguments: [item] };
    }
  }

  const provider = {
    onDidChangeTreeData: emitter.event,
    getTreeItem: element => element,
    async getChildren(element) {
      if (element) return [];
      const rows = [];
      const ordered = [...findings].sort((a, b) =>
        (SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity]) || String(b.createdUtc).localeCompare(String(a.createdUtc)));
      for (const item of ordered) {
        let resolution = null;
        let locationStatus = 'unavailable';
        try {
          resolution = (await resolveCurrent(item)).resolution;
          locationStatus = resolution.status;
        } catch { }
        rows.push(new FindingItem(item, resolution?.status === 'ok' ? resolution : null, locationStatus));
      }
      return rows;
    }
  };

  async function openFinding(item) {
    let current;
    try { current = await resolveCurrent(item); }
    catch {
      vscode.window.showWarningMessage('PaRuX: this finding\'s source file is no longer available in the workspace; re-run review after restoring or reopening the file.');
      return;
    }
    if (current.resolution.status !== 'ok') {
      vscode.window.showWarningMessage('PaRuX: this finding is stale or ambiguous after source edits; re-run review instead of navigating to a guessed line.');
      return;
    }
    const editor = await vscode.window.showTextDocument(current.document, { preview: true });
    const selection = new vscode.Selection(
      new vscode.Position(current.resolution.startLine, 0),
      current.document.lineAt(current.resolution.endLine).range.end);
    editor.selection = selection;
    editor.revealRange(selection, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
  }

  async function setState(item, state) {
    const id = item?.id || item?.finding?.id;
    if (!id) return;
    findings = findings.map(f => f.id === id ? { ...f, state } : f);
    await persist();
    await refresh();
  }

  const tree = vscode.window.createTreeView('paruxReviewFindings', { treeDataProvider: provider, showCollapseAll: false });
  const subscriptions = [
    diagnostics,
    emitter,
    tree,
    vscode.workspace.onDidOpenTextDocument(document => ingestDocument(document).catch(() => {})),
    vscode.workspace.onDidChangeTextDocument(event => {
      if (findings.some(item => item.fileUri === event.document.uri.toString())) refresh().catch(() => {});
    }),
    ...(vscode.workspace.onDidCloseTextDocument ? [vscode.workspace.onDidCloseTextDocument(document => {
      if (findings.some(item => item.fileUri === document.uri.toString())) refresh().catch(() => {});
    })] : []),
    ...(vscode.workspace.onDidDeleteFiles ? [vscode.workspace.onDidDeleteFiles(() => refresh().catch(() => {}))] : []),
    vscode.commands.registerCommand('parux.openReviewFinding', item => openFinding(item.finding || item)),
    vscode.commands.registerCommand('parux.resolveReviewFinding', item => setState(item, 'resolved')),
    vscode.commands.registerCommand('parux.dismissReviewFinding', item => setState(item, 'dismissed')),
    vscode.commands.registerCommand('parux.reopenReviewFinding', item => setState(item, 'open'))
  ];

  for (const document of vscode.workspace.textDocuments) ingestDocument(document).catch(() => {});
  refresh().catch(() => {});
  return subscriptions;
}

module.exports = {
  REVIEW_FINDINGS_STATE_KEY,
  MAX_FINDINGS,
  normalizeSeverity,
  parseLocation,
  parseReviewMarkdown,
  findingIdentity,
  normalizeStoredFindings,
  mergeFindings,
  isUriPathWithinRoot,
  registerReviewFindings
};
