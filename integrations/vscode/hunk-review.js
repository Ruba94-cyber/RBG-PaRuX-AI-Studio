'use strict';

const {
  captureHunk,
  resolveHunk,
  normalizeDecisions,
  appendDecision
} = require('./hunk-identity');

const REVIEW_STATE_KEY = 'parux.hunkReview.decisions.v1';

/**
 * Registers explicit per-hunk review decisions on top of VS Code's built-in Git commands.
 * A decision snapshots a whole-line hunk identity before any async UI is shown, then resolves that
 * identity against the current document immediately before the Git command. Unrelated line shifts
 * are tolerated; changed/deleted/ambiguous hunks fail closed rather than applying a stale range.
 * Only hashes/ranges/decision metadata are persisted in workspaceState, never source text.
 */
function registerHunkReviewCommands(vscode, guarded, workspaceState) {
  async function captureSelection() {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.uri.scheme !== 'file')
      throw new Error('Open the modified side of a local Git diff before accepting or rejecting a hunk.');
    if (editor.selection.isEmpty)
      throw new Error('Select the changed lines/hunk to review first.');

    if (editor.document.isDirty) {
      const saved = await editor.document.save();
      if (!saved) throw new Error('Save the file before making a Git hunk decision.');
    }

    const startLine = editor.selection.start.line;
    let endLine = editor.selection.end.line;
    // A line-wise selection commonly ends at column 0 of the next line. Do not accidentally fold
    // that untouched line into the hunk identity or the Git operation.
    if (editor.selection.end.character === 0 && endLine > startLine) endLine--;
    const fileKey = editor.document.uri.toString();
    const descriptor = captureHunk(editor.document.getText(), fileKey, startLine, endLine);
    return { editor, descriptor };
  }

  function resolveSelection(editor, descriptor) {
    if (editor.document.uri.toString() !== descriptor.fileKey)
      throw new Error('The active editor changed while the hunk decision was pending. Re-select the hunk.');
    const resolution = resolveHunk(editor.document.getText(), descriptor);
    if (resolution.status === 'stale')
      throw new Error('The selected hunk changed or was deleted before the decision could be applied. Re-open the diff and review it again.');
    if (resolution.status === 'ambiguous')
      throw new Error('The selected hunk now matches multiple locations. PaRuX will not guess which copy to change; re-select the intended hunk.');

    const selection = new vscode.Selection(
      new vscode.Position(resolution.startLine, 0),
      editor.document.lineAt(resolution.endLine).range.end);
    editor.selection = selection;
    editor.revealRange(selection, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
    return resolution;
  }

  async function recordDecision(descriptor, resolution, action) {
    if (!workspaceState || typeof workspaceState.update !== 'function') return;
    const existing = normalizeDecisions(workspaceState.get(REVIEW_STATE_KEY, []));
    const updated = appendDecision(existing, {
      hunkId: descriptor.id,
      fileKey: descriptor.fileKey,
      action,
      originalStartLine: descriptor.originalStartLine,
      resolvedStartLine: resolution.startLine,
      lineCount: descriptor.lineCount,
      relocated: resolution.relocated,
      decidedUtc: new Date().toISOString()
    });
    await workspaceState.update(REVIEW_STATE_KEY, updated);
  }

  const accept = vscode.commands.registerCommand('parux.acceptHunk', () => guarded('accept hunk', async () => {
    const { editor, descriptor } = await captureSelection();
    const resolution = resolveSelection(editor, descriptor);
    await vscode.commands.executeCommand('git.stageSelectedRanges');
    await recordDecision(descriptor, resolution, 'accepted');
    const moved = resolution.relocated ? ` (relocated to line ${resolution.startLine + 1})` : '';
    vscode.window.showInformationMessage(`PaRuX: selected hunk accepted and staged${moved}.`);
  }));

  const reject = vscode.commands.registerCommand('parux.rejectHunk', () => guarded('reject hunk', async () => {
    const { editor, descriptor } = await captureSelection();
    const file = vscode.workspace.asRelativePath(editor.document.uri, false);
    const answer = await vscode.window.showWarningMessage(
      `Reject the selected changed lines in ${file}? This reverts only the revalidated hunk to the Git base.`,
      { modal: true },
      'Reject hunk');
    if (answer !== 'Reject hunk') return;

    // Confirmation is asynchronous: re-resolve after it, not before it. Any edit made while the
    // modal was open therefore either relocates safely by identity or aborts as stale/ambiguous.
    const resolution = resolveSelection(editor, descriptor);
    await vscode.commands.executeCommand('git.revertSelectedRanges');
    await recordDecision(descriptor, resolution, 'rejected');
    const moved = resolution.relocated ? ` (relocated to line ${resolution.startLine + 1})` : '';
    vscode.window.showInformationMessage(`PaRuX: selected hunk rejected and reverted${moved}.`);
  }));

  return [accept, reject];
}

module.exports = { REVIEW_STATE_KEY, registerHunkReviewCommands };
