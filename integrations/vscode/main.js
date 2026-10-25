'use strict';

const vscode = require('vscode');
const base = require('./extension');
const { registerHunkReviewCommands } = require('./hunk-review');
const { registerReviewFindings } = require('./review-findings');

function activate(context) {
  base.activate(context);

  async function guarded(label, action) {
    try { return await action(); }
    catch (error) {
      console.error(`PaRuX ${label}:`, error);
      vscode.window.showErrorMessage(`PaRuX: ${error.message}`);
      return undefined;
    }
  }

  context.subscriptions.push(...registerHunkReviewCommands(vscode, guarded, context.workspaceState));
  context.subscriptions.push(...registerReviewFindings(vscode, context.workspaceState));
}

function deactivate() {
  if (typeof base.deactivate === 'function') return base.deactivate();
}

module.exports = { activate, deactivate };
