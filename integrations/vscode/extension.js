'use strict';

const path = require('path');
const vscode = require('vscode');
const { PaRuXCoreClient, PROFILES } = require('./core-client');

const ALIAS_PATTERN = /^[A-Za-z][A-Za-z0-9._-]{0,31}$/;
const MAX_REPOSITORIES = 8;
const MAX_SELECTION_CHARS = 16000;
const MAX_ACTIVE_CONTEXT_CHARS = 24000;
const MAX_OPEN_FILE_CHARS = 3500;
const MAX_CONTEXT_CHARS = 56000;
const MAX_DIAGNOSTICS = 80;

function taskStatus(task) { return String(task.status || 'unknown').toLowerCase(); }
function isTerminal(task) { return ['completed', 'failed', 'cancelled'].includes(taskStatus(task)); }
function pathKey(value) {
  const normalized = path.resolve(String(value || '')).replace(/[\\/]+$/, '');
  return process.platform === 'win32' ? normalized.toLowerCase() : normalized;
}
function bounded(value, max) {
  const text = String(value || '');
  if (text.length <= max) return text;
  return text.slice(0, Math.max(0, max - 36)) + `\n… [PaRuX bounded ${text.length - max} chars]`;
}
function cleanAlias(value, used) {
  let alias = String(value || 'repo').replace(/[^A-Za-z0-9._-]/g, '-').replace(/^[^A-Za-z]+/, '');
  if (!alias) alias = 'repo';
  alias = alias.slice(0, 28);
  let candidate = alias;
  let index = 2;
  while (used.has(candidate.toLowerCase()) || candidate.toLowerCase() === 'primary')
    candidate = `${alias.slice(0, 24)}-${index++}`;
  used.add(candidate.toLowerCase());
  return candidate;
}

function statusIcon(status) {
  switch (status) {
    case 'running': return new vscode.ThemeIcon('sync~spin');
    case 'waiting': return new vscode.ThemeIcon('clock');
    case 'verifying': return new vscode.ThemeIcon('beaker');
    case 'completed': return new vscode.ThemeIcon('pass-filled');
    case 'failed': return new vscode.ThemeIcon('error');
    case 'blocked': return new vscode.ThemeIcon('warning');
    case 'cancelled': return new vscode.ThemeIcon('circle-slash');
    default: return new vscode.ThemeIcon('circle-outline');
  }
}

class TaskItem extends vscode.TreeItem {
  constructor(task) {
    const role = task.role || 'auto';
    super(`${role}: ${task.goal || task.id}`, vscode.TreeItemCollapsibleState.None);
    this.task = task;
    this.id = task.id;
    this.contextValue = 'paruxTask';
    this.description = `${task.status || 'unknown'} · ${task.progressPercent || 0}%`;
    this.iconPath = statusIcon(taskStatus(task));
    this.tooltip = new vscode.MarkdownString(
      `**PaRuX task** \`${task.id}\`\n\n` +
      `Role: **${role}**  \nStatus: **${task.status || 'unknown'}**  \n` +
      `Stage: ${task.stage || '-'}  \nVerification: ${task.verificationStatus || '-'}  \n` +
      `Repair: ${task.repairAttemptsUsed || 0}/${task.maxRepairAttempts || 0}`);
    this.command = { command: 'parux.showTaskLogs', title: 'Show PaRuX task logs', arguments: [this] };
  }
}

class TaskTreeProvider {
  constructor(clientFactory, statusBar) {
    this.clientFactory = clientFactory;
    this.statusBar = statusBar;
    this._onDidChangeTreeData = new vscode.EventEmitter();
    this.onDidChangeTreeData = this._onDidChangeTreeData.event;
    this.lastTasks = [];
  }

  refresh() { this._onDidChangeTreeData.fire(undefined); }
  getTreeItem(element) { return element; }

  async getChildren(element) {
    if (element) return [];
    try {
      const response = await this.clientFactory().listTasks();
      const tasks = Array.isArray(response?.tasks) ? response.tasks : [];
      this.lastTasks = tasks;
      const active = tasks.filter(task => !isTerminal(task)).length;
      this.statusBar.text = `$(hubot) PaRuX: ${active} active`;
      this.statusBar.tooltip = `${tasks.length} durable PaRuX task(s); ${active} non-terminal.`;
      this.statusBar.show();
      return tasks.map(task => new TaskItem(task));
    } catch (error) {
      this.lastTasks = [];
      this.statusBar.text = '$(debug-disconnect) PaRuX Core offline';
      this.statusBar.tooltip = error.message;
      this.statusBar.show();
      return [];
    }
  }
}

class PaRuXEditorCodeActions {
  provideCodeActions(document, range, context) {
    const actions = [];
    const add = (title, command, kind) => {
      const action = new vscode.CodeAction(title, kind);
      action.command = { command, title, arguments: [{ uri: document.uri, range }] };
      action.diagnostics = context.diagnostics;
      actions.push(action);
    };
    add('PaRuX: Fix with Coding Agent', 'parux.fixSelection', vscode.CodeActionKind.QuickFix);
    add('PaRuX: Explain this code', 'parux.explainSelection', vscode.CodeActionKind.Empty);
    add('PaRuX: Generate / repair tests', 'parux.testSelection', vscode.CodeActionKind.RefactorRewrite);
    add('PaRuX: Review selected hunk', 'parux.reviewSelection', vscode.CodeActionKind.Empty);
    return actions;
  }
}

function activate(context) {
  const output = vscode.window.createOutputChannel('PaRuX Agent');
  const statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 90);
  statusBar.command = 'parux.refreshAgents';

  const clientFactory = () => {
    const configured = vscode.workspace.getConfiguration('parux').get('dataRoot', '');
    return new PaRuXCoreClient({ dataRoot: configured });
  };
  const provider = new TaskTreeProvider(clientFactory, statusBar);
  const tree = vscode.window.createTreeView('paruxAgents', { treeDataProvider: provider, showCollapseAll: false });

  function taskFrom(value) {
    if (value instanceof TaskItem) return value.task;
    if (value && value.task) return value.task;
    return value && value.id ? value : null;
  }

  async function guarded(label, action) {
    try { return await action(); }
    catch (error) {
      output.appendLine(`[${new Date().toISOString()}] ${label}: ${error.stack || error.message}`);
      vscode.window.showErrorMessage(`PaRuX: ${error.message}`);
      return undefined;
    }
  }

  async function browseFolder(title, defaultPath) {
    const picked = await vscode.window.showOpenDialog({
      canSelectFiles: false,
      canSelectFolders: true,
      canSelectMany: false,
      title,
      defaultUri: defaultPath ? vscode.Uri.file(defaultPath) : undefined
    });
    return picked && picked.length ? picked[0].fsPath : null;
  }

  async function choosePrimaryWorkspace(existingPrimary) {
    if (existingPrimary) {
      const action = await vscode.window.showQuickPick([
        { label: 'Keep current primary', description: existingPrimary, value: 'keep' },
        { label: 'Choose another primary…', description: 'Replace the complete authority set before execution starts', value: 'browse' }
      ], { placeHolder: 'Primary repository', ignoreFocusOut: true });
      if (!action) return null;
      if (action.value === 'keep') return existingPrimary;
      return browseFolder('Choose PaRuX primary repository', existingPrimary);
    }

    const folders = vscode.workspace.workspaceFolders || [];
    if (folders.length === 1) return folders[0].uri.fsPath;
    if (folders.length > 1) {
      const choices = folders.map(folder => ({ label: folder.name, description: folder.uri.fsPath, path: folder.uri.fsPath }));
      choices.push({ label: 'Browse…', description: 'Choose another local Git repository', path: null });
      const picked = await vscode.window.showQuickPick(choices, { placeHolder: 'Primary repository authority for this PaRuX coding task' });
      if (!picked) return null;
      if (picked.path) return picked.path;
    }
    return browseFolder('Choose PaRuX primary coding repository');
  }

  async function collectRepositorySet(existing) {
    const repositories = Array.isArray(existing?.repositories) ? existing.repositories : [];
    const currentPrimary = repositories.find(repository => repository.isPrimary)?.workspaceRoot || null;
    const primaryWorkspaceRoot = await choosePrimaryWorkspace(currentPrimary);
    if (!primaryWorkspaceRoot) return null;

    const topology = await vscode.window.showQuickPick([
      { label: 'Single repository', description: 'Use only the primary repository', value: 'single' },
      { label: 'Multi-repository', description: 'Authorize up to 7 additional independent repositories', value: 'multi' }
    ], { placeHolder: `Repository topology · primary: ${primaryWorkspaceRoot}`, ignoreFocusOut: true });
    if (!topology) return null;
    if (topology.value === 'single') return { primaryWorkspaceRoot, additionalRepositories: [] };

    const additionalRepositories = [];
    const aliases = new Set(['primary']);
    const paths = new Set([pathKey(primaryWorkspaceRoot)]);
    while (additionalRepositories.length < MAX_REPOSITORIES - 1) {
      const next = await vscode.window.showQuickPick([
        { label: 'Add repository…', description: `${additionalRepositories.length + 1}/${MAX_REPOSITORIES - 1} additional aliases configured`, value: 'add' },
        { label: 'Finish', description: `Create/apply ${1 + additionalRepositories.length} repository authority set`, value: 'finish' }
      ], { placeHolder: 'Build the complete repository set locally; PaRuX writes it only after Finish', ignoreFocusOut: true });
      if (!next) return null;
      if (next.value === 'finish') break;

      const workspaceRoot = await browseFolder('Choose additional PaRuX repository');
      if (!workspaceRoot) return null;
      const key = pathKey(workspaceRoot);
      if (paths.has(key)) {
        vscode.window.showWarningMessage('PaRuX: that repository path is already in this authority set.');
        continue;
      }

      const alias = await vscode.window.showInputBox({
        prompt: 'Alias used as the top-level directory inside the PaRuX aggregate worktree',
        placeHolder: 'backend, frontend, docs…',
        ignoreFocusOut: true,
        validateInput: value => {
          const normalized = value.trim();
          if (!ALIAS_PATTERN.test(normalized)) return "Start with a letter; use only letters, numbers, '.', '_' or '-' (max 32).";
          if (normalized.toLowerCase() === 'primary') return "'primary' is reserved.";
          if (aliases.has(normalized.toLowerCase())) return 'That alias is already used.';
          return undefined;
        }
      });
      if (!alias) return null;
      const normalizedAlias = alias.trim();
      aliases.add(normalizedAlias.toLowerCase());
      paths.add(key);
      additionalRepositories.push({ alias: normalizedAlias, workspaceRoot });
    }
    return { primaryWorkspaceRoot, additionalRepositories };
  }

  function targetDocument(target) {
    if (target?.uri) {
      const direct = vscode.workspace.textDocuments.find(doc => doc.uri.toString() === target.uri.toString());
      if (direct) return direct;
    }
    return vscode.window.activeTextEditor?.document || null;
  }

  function targetRange(document, target) {
    if (target?.range) return new vscode.Range(target.range.start, target.range.end);
    const editor = vscode.window.activeTextEditor;
    if (editor && editor.document.uri.toString() === document.uri.toString() && !editor.selection.isEmpty)
      return editor.selection;
    const line = editor && editor.document.uri.toString() === document.uri.toString()
      ? editor.selection.active.line : 0;
    return document.lineAt(Math.min(Math.max(0, line), Math.max(0, document.lineCount - 1))).range;
  }

  function buildRepositorySetForEditor(primaryRoot) {
    const additionalRepositories = [];
    const used = new Set(['primary']);
    const seen = new Set([pathKey(primaryRoot)]);
    for (const folder of vscode.workspace.workspaceFolders || []) {
      if (additionalRepositories.length >= MAX_REPOSITORIES - 1) break;
      const root = folder.uri.fsPath;
      if (!root || seen.has(pathKey(root))) continue;
      seen.add(pathKey(root));
      additionalRepositories.push({ alias: cleanAlias(folder.name, used), workspaceRoot: root });
    }
    return { primaryWorkspaceRoot: primaryRoot, additionalRepositories };
  }

  function diagnosticSeverity(value) {
    return ['Error', 'Warning', 'Information', 'Hint'][value] || 'Unknown';
  }

  function captureEditorContext(target) {
    const document = targetDocument(target);
    if (!document || document.uri.scheme !== 'file')
      throw new Error('Open a local source file before using PaRuX editor actions.');
    const folder = vscode.workspace.getWorkspaceFolder(document.uri);
    if (!folder) throw new Error('The active file must belong to an open workspace/repository.');
    const root = folder.uri.fsPath;
    const range = targetRange(document, target);
    const relative = path.relative(root, document.uri.fsPath).replace(/\\/g, '/');
    const selection = bounded(document.getText(range), MAX_SELECTION_CHARS);

    const startLine = Math.max(0, range.start.line - 80);
    const endLine = Math.min(document.lineCount - 1, range.end.line + 80);
    const surrounding = bounded(document.getText(new vscode.Range(
      new vscode.Position(startLine, 0), document.lineAt(endLine).range.end)), MAX_ACTIVE_CONTEXT_CHARS);

    const open = [];
    let openBudget = 0;
    for (const doc of vscode.workspace.textDocuments) {
      if (doc.uri.scheme !== 'file' || doc.uri.toString() === document.uri.toString()) continue;
      const docFolder = vscode.workspace.getWorkspaceFolder(doc.uri);
      if (!docFolder || open.length >= 8 || openBudget >= 20000) continue;
      const rel = `${docFolder.name}:${path.relative(docFolder.uri.fsPath, doc.uri.fsPath).replace(/\\/g, '/')}`;
      const text = bounded(doc.getText(), Math.min(MAX_OPEN_FILE_CHARS, 20000 - openBudget));
      openBudget += text.length;
      open.push(`--- ${rel}\n${text}`);
    }

    const diagnostics = [];
    for (const [uri, values] of vscode.languages.getDiagnostics()) {
      if (diagnostics.length >= MAX_DIAGNOSTICS) break;
      const diagFolder = vscode.workspace.getWorkspaceFolder(uri);
      if (!diagFolder) continue;
      for (const diagnostic of values) {
        if (diagnostics.length >= MAX_DIAGNOSTICS) break;
        diagnostics.push(
          `${diagFolder.name}:${path.relative(diagFolder.uri.fsPath, uri.fsPath).replace(/\\/g, '/')}` +
          `:${diagnostic.range.start.line + 1}:${diagnostic.range.start.character + 1}` +
          ` [${diagnosticSeverity(diagnostic.severity)}] ${bounded(diagnostic.message, 500)}`);
      }
    }

    const payload = [
      '[PARUX_VSCODE_CONTEXT_V2]',
      `primary_repository=${root}`,
      `active_file=${relative}`,
      `selection=${range.start.line + 1}:${range.start.character + 1}-${range.end.line + 1}:${range.end.character + 1}`,
      '', '[SELECTED_OR_TARGET_TEXT]', selection,
      '', '[ACTIVE_FILE_SURROUNDING_CONTEXT]', surrounding,
      '', '[OPEN_FILES]', open.length ? open.join('\n\n') : '(none)',
      '', '[WORKSPACE_DIAGNOSTICS]', diagnostics.length ? diagnostics.join('\n') : '(none)',
      '[/PARUX_VSCODE_CONTEXT_V2]'
    ].join('\n');

    return {
      root,
      repositorySet: buildRepositorySetForEditor(root),
      file: relative,
      range,
      payload: bounded(payload, MAX_CONTEXT_CHARS)
    };
  }

  function editorPermissionProfile() {
    const configured = String(vscode.workspace.getConfiguration('parux').get('editorPermissionProfile', PROFILES.offline));
    return configured === PROFILES.packageFetch || configured === PROFILES.trustedRestore
      ? configured : PROFILES.offline;
  }

  async function startEditorCodingTask(kind, target, extraInstruction) {
    const editorContext = captureEditorContext(target);
    const instruction = kind === 'fix'
      ? 'Fix the selected/current code and the relevant diagnostics. Establish the root cause from repository evidence, preserve unrelated behavior, run the most relevant deterministic tests/validation and leave a reviewable diff.'
      : kind === 'test'
        ? 'Create or repair high-value tests for the selected/current code. Cover the behavior and relevant diagnostics, prefer deterministic focused tests, run them, and change production code only when a real defect is required to make the intended behavior pass.'
        : 'Apply the reviewer feedback to this selected hunk. Verify the surrounding repository before editing, preserve unrelated code, run relevant tests, and leave a minimal reviewable diff.';
    const goal = `${instruction}${extraInstruction ? `\nReviewer feedback: ${extraInstruction}` : ''}\n\n${editorContext.payload}`;
    const client = clientFactory();
    const task = await client.createCodingTask(
      goal,
      editorContext.repositorySet.primaryWorkspaceRoot,
      editorPermissionProfile(),
      2,
      editorContext.repositorySet.additionalRepositories);
    await client.schedule();
    provider.refresh();
    vscode.window.showInformationMessage(`PaRuX ${kind} task ${task.id} created for ${editorContext.file}.`);
    return task;
  }

  function humanTaskResult(task) {
    const raw = String(task?.resultJson || '').trim();
    if (!raw) return task?.error || 'PaRuX completed without a textual result.';
    try {
      const value = JSON.parse(raw);
      for (const key of ['content', 'output', 'summary', 'result', 'message'])
        if (typeof value?.[key] === 'string' && value[key].trim()) return value[key].trim();
      return '```json\n' + JSON.stringify(value, null, 2) + '\n```';
    } catch { return raw; }
  }

  async function runReadOnlyEditorTask(kind, target) {
    const editorContext = captureEditorContext(target);
    const instruction = kind === 'review'
      ? 'Review the selected hunk like a senior code reviewer. Find correctness, regression, security, concurrency, data-loss and performance issues. Rank concrete findings by severity, cite the supplied file/line range, and do not invent repository facts that are absent from the bounded editor context.'
      : 'Explain the selected/current code and relevant diagnostics precisely. Describe behavior, data/control flow, important invariants, likely failure modes and what the diagnostics imply. Do not modify files.';
    const client = clientFactory();
    const task = await client.createTask(`${instruction}\n\n${editorContext.payload}`, kind === 'review' ? 'reviewer' : 'reasoning', 0);
    await client.schedule();
    provider.refresh();

    await vscode.window.withProgress({
      location: vscode.ProgressLocation.Notification,
      title: `PaRuX ${kind} · ${editorContext.file}`,
      cancellable: false
    }, async progress => {
      const deadline = Date.now() + 5 * 60 * 1000;
      let latest = task;
      while (Date.now() < deadline && !isTerminal(latest)) {
        progress.report({ message: `${latest.status || 'queued'} · ${latest.stage || ''}` });
        await new Promise(resolve => setTimeout(resolve, 1000));
        latest = await client.getTask(task.id);
      }
      if (!isTerminal(latest)) {
        vscode.window.showInformationMessage(`PaRuX task ${task.id} is still running; it remains visible in PaRuX Agents.`);
        return;
      }
      const document = await vscode.workspace.openTextDocument({
        language: 'markdown',
        content: `# PaRuX ${kind === 'review' ? 'inline review' : 'explanation'}\n\n` +
          `**Source:** \`${editorContext.file}\` · task \`${task.id}\`\n\n${humanTaskResult(latest)}`
      });
      await vscode.window.showTextDocument(document, { preview: true, viewColumn: vscode.ViewColumn.Beside });
    });
  }

  context.subscriptions.push(
    output,
    statusBar,
    tree,
    vscode.languages.registerCodeActionsProvider({ scheme: 'file' }, new PaRuXEditorCodeActions(), {
      providedCodeActionKinds: [vscode.CodeActionKind.QuickFix, vscode.CodeActionKind.RefactorRewrite, vscode.CodeActionKind.Empty]
    }),
    vscode.commands.registerCommand('parux.refreshAgents', () => provider.refresh()),
    vscode.commands.registerCommand('parux.fixSelection', target => guarded('fix selection', () => startEditorCodingTask('fix', target))),
    vscode.commands.registerCommand('parux.testSelection', target => guarded('test selection', () => startEditorCodingTask('test', target))),
    vscode.commands.registerCommand('parux.explainSelection', target => guarded('explain selection', () => runReadOnlyEditorTask('explain', target))),
    vscode.commands.registerCommand('parux.reviewSelection', target => guarded('review selection', () => runReadOnlyEditorTask('review', target))),
    vscode.commands.registerCommand('parux.hunkFeedback', target => guarded('hunk feedback', async () => {
      const feedback = await vscode.window.showInputBox({
        prompt: 'What should PaRuX change in this selected hunk?',
        placeHolder: 'Address this review comment…',
        ignoreFocusOut: true,
        validateInput: value => value.trim() ? undefined : 'Reviewer feedback is required.'
      });
      if (!feedback) return;
      await startEditorCodingTask('hunk-feedback', target, feedback.trim());
    })),
    vscode.commands.registerCommand('parux.runCodingTask', async () => guarded('run coding task', async () => {
      const repositorySet = await collectRepositorySet(null);
      if (!repositorySet) return;
      const goal = await vscode.window.showInputBox({
        prompt: 'What should the PaRuX Coding Agent implement?',
        placeHolder: 'Implement/fix/refactor…',
        ignoreFocusOut: true,
        validateInput: value => value.trim() ? undefined : 'A task goal is required.'
      });
      if (!goal) return;
      const profile = await vscode.window.showQuickPick([
        { label: 'Offline', description: 'No dependency downloads; safest default', value: PROFILES.offline },
        { label: 'Package Fetch', description: 'Frozen/no-script dependency fetch where supported', value: PROFILES.packageFetch },
        { label: 'Trusted Restore', description: 'Restore may execute repository/plugin/build-backend code', value: PROFILES.trustedRestore }
      ], { placeHolder: 'Coding permission profile for this new task', ignoreFocusOut: true });
      if (!profile) return;

      const client = clientFactory();
      const task = await client.createCodingTask(
        goal.trim(), repositorySet.primaryWorkspaceRoot, profile.value, 2, repositorySet.additionalRepositories);
      await client.schedule();
      provider.refresh();
      vscode.window.showInformationMessage(
        `PaRuX coding task ${task.id} created (${1 + repositorySet.additionalRepositories.length} repo, ${profile.label}).`);
    })),
    vscode.commands.registerCommand('parux.configureTaskRepositories', async value => guarded('configure task repositories', async () => {
      const task = taskFrom(value);
      if (!task) return;
      const client = clientFactory();
      const latest = await client.getTask(task.id);
      if (String(latest?.role || '').toLowerCase() !== 'coding')
        return vscode.window.showWarningMessage('PaRuX: multi-repository worktrees are a Coding Agent capability; this task is not coding.');
      const existing = await client.getRepositorySet(task.id);
      const repositorySet = await collectRepositorySet(existing);
      if (!repositorySet) return;
      const set = await client.replaceRepositorySet(
        task.id, repositorySet.primaryWorkspaceRoot, repositorySet.additionalRepositories);
      await client.schedule();
      provider.refresh();
      vscode.window.showInformationMessage(`PaRuX task ${task.id}: ${set.repositories?.length || 0} repository authorities configured atomically.`);
    })),
    vscode.commands.registerCommand('parux.showTaskLogs', async value => guarded('show task logs', async () => {
      const task = taskFrom(value);
      if (!task) return;
      const response = await clientFactory().getLogs(task.id);
      const logs = Array.isArray(response?.logs) ? response.logs : [];
      output.clear();
      output.appendLine(`PaRuX task ${task.id}`);
      output.appendLine(`Goal: ${task.goal || ''}`);
      output.appendLine(`Status: ${task.status || ''} · ${task.stage || ''}`);
      output.appendLine('');
      for (const log of logs) output.appendLine(`${log.timestampUtc || ''}  ${log.message || ''}`);
      output.show(true);
    })),
    vscode.commands.registerCommand('parux.openTaskWorktree', async value => guarded('open task worktree', async () => {
      const task = taskFrom(value);
      if (!task) return;
      const desktop = await clientFactory().getDesktopWorktree(task.id);
      const worktree = desktop?.worktreePath;
      if (!worktree) return vscode.window.showWarningMessage('PaRuX: this task has no reviewable worktree yet.');
      await vscode.commands.executeCommand('vscode.openFolder', vscode.Uri.file(worktree), true);
    })),
    vscode.commands.registerCommand('parux.stopTask', async value => guarded('stop task', async () => {
      const task = taskFrom(value);
      if (!task) return;
      if (isTerminal(task)) return vscode.window.showInformationMessage(`PaRuX task is already ${task.status}.`);
      const answer = await vscode.window.showWarningMessage(`Stop PaRuX task ${task.id}?`, { modal: true }, 'Stop task');
      if (answer !== 'Stop task') return;
      await clientFactory().stopTask(task.id);
      provider.refresh();
    })),
    vscode.commands.registerCommand('parux.resumeTask', async value => guarded('resume task', async () => {
      const task = taskFrom(value);
      if (!task) return;
      const latest = await clientFactory().getTask(task.id);
      const status = taskStatus(latest || task);
      if (status === 'completed' || status === 'cancelled')
        return vscode.window.showWarningMessage('PaRuX: completed/cancelled tasks are immutable; create a continuation instead.');
      if ((latest?.role || '').toLowerCase() === 'coding') {
        const desktop = await clientFactory().getDesktopWorktree(task.id);
        if (!desktop?.worktreePath)
          return vscode.window.showErrorMessage('PaRuX: coding resume refused because the preserved worktree is unavailable.');
      }
      const client = clientFactory();
      await client.resumeTask(task.id);
      await client.schedule();
      provider.refresh();
    }))
  );

  let timer;
  const resetTimer = () => {
    if (timer) clearInterval(timer);
    const seconds = Math.max(2, Math.min(60, Number(vscode.workspace.getConfiguration('parux').get('refreshSeconds', 5)) || 5));
    timer = setInterval(() => provider.refresh(), seconds * 1000);
  };
  resetTimer();
  context.subscriptions.push({ dispose: () => timer && clearInterval(timer) });
  context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(event => {
    if (event.affectsConfiguration('parux.refreshSeconds')) resetTimer();
    if (event.affectsConfiguration('parux.dataRoot')) provider.refresh();
  }));
  provider.refresh();
}

function deactivate() { }

module.exports = { activate, deactivate };
