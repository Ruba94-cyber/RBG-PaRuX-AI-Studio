'use strict';

const crypto = require('node:crypto');

const SCHEMA_VERSION = 1;
const MAX_DECISIONS = 200;

function sha256(value) {
  return crypto.createHash('sha256').update(String(value), 'utf8').digest('hex');
}

function linesOf(text) {
  return String(text ?? '').replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
}

function clampRange(lineCount, startLine, endLine) {
  if (!Number.isInteger(startLine) || !Number.isInteger(endLine))
    throw new TypeError('Hunk line range must use integer line numbers.');
  if (startLine < 0 || endLine < startLine || endLine >= lineCount)
    throw new RangeError(`Hunk line range ${startLine}-${endLine} is outside a ${lineCount}-line document.`);
  return { startLine, endLine };
}

function windowText(lines, startLine, lineCount) {
  return lines.slice(startLine, startLine + lineCount).join('\n');
}

function captureHunk(text, fileKey, startLine, endLine) {
  const lines = linesOf(text);
  const range = clampRange(lines.length, startLine, endLine);
  const lineCount = range.endLine - range.startLine + 1;
  const selectedHash = sha256(windowText(lines, range.startLine, lineCount));
  const beforeHash = range.startLine > 0 ? sha256(lines[range.startLine - 1]) : null;
  const afterHash = range.endLine + 1 < lines.length ? sha256(lines[range.endLine + 1]) : null;
  const normalizedFile = String(fileKey || '').replace(/\\/g, '/');
  if (!normalizedFile) throw new TypeError('A repository-relative file key is required.');
  const id = sha256([
    SCHEMA_VERSION, normalizedFile, selectedHash, beforeHash || '-', afterHash || '-',
    range.startLine, lineCount
  ].join('|'));
  return {
    schemaVersion: SCHEMA_VERSION,
    id,
    fileKey: normalizedFile,
    originalStartLine: range.startLine,
    lineCount,
    selectedHash,
    beforeHash,
    afterHash
  };
}

function anchorScore(lines, startLine, descriptor) {
  let score = 0;
  let available = 0;
  if (descriptor.beforeHash) {
    available++;
    if (startLine > 0 && sha256(lines[startLine - 1]) === descriptor.beforeHash) score++;
  }
  if (descriptor.afterHash) {
    available++;
    const after = startLine + descriptor.lineCount;
    if (after < lines.length && sha256(lines[after]) === descriptor.afterHash) score++;
  }
  return { score, available };
}

function resolveHunk(text, descriptor) {
  if (!descriptor || descriptor.schemaVersion !== SCHEMA_VERSION)
    return { status: 'stale', reason: 'unsupported-descriptor' };
  if (!Number.isInteger(descriptor.lineCount) || descriptor.lineCount < 1 || !descriptor.selectedHash)
    return { status: 'stale', reason: 'invalid-descriptor' };

  const lines = linesOf(text);
  if (descriptor.lineCount > lines.length)
    return { status: 'stale', reason: 'selected-lines-missing' };

  const candidates = [];
  for (let start = 0; start + descriptor.lineCount <= lines.length; start++) {
    if (sha256(windowText(lines, start, descriptor.lineCount)) !== descriptor.selectedHash) continue;
    const anchors = anchorScore(lines, start, descriptor);
    candidates.push({ startLine: start, endLine: start + descriptor.lineCount - 1, ...anchors });
  }
  if (candidates.length === 0)
    return { status: 'stale', reason: 'selected-lines-changed-or-deleted' };

  const original = candidates.find(candidate => candidate.startLine === descriptor.originalStartLine);
  if (original && (candidates.length === 1 || original.score === original.available)) {
    return { status: 'ok', ...original, relocated: false };
  }
  if (candidates.length === 1)
    return { status: 'ok', ...candidates[0], relocated: candidates[0].startLine !== descriptor.originalStartLine };

  const bestScore = Math.max(...candidates.map(candidate => candidate.score));
  const best = candidates.filter(candidate => candidate.score === bestScore);
  if (best.length === 1 && (bestScore > 0 || best[0].available === 0))
    return { status: 'ok', ...best[0], relocated: best[0].startLine !== descriptor.originalStartLine };
  return { status: 'ambiguous', reason: 'multiple-matching-hunks', candidates: candidates.length };
}

function rangesOverlap(left, right) {
  if (!left || !right) return false;
  return left.startLine <= right.endLine && right.startLine <= left.endLine;
}

function normalizeDecisions(value) {
  if (!Array.isArray(value)) return [];
  return value
    .filter(item => item && item.schemaVersion === SCHEMA_VERSION && typeof item.hunkId === 'string')
    .slice(-MAX_DECISIONS);
}

function appendDecision(existing, decision) {
  if (!decision || !['accepted', 'rejected'].includes(decision.action))
    throw new TypeError('Decision action must be accepted or rejected.');
  if (!decision.hunkId || !decision.fileKey)
    throw new TypeError('Decision requires hunkId and fileKey.');
  const next = normalizeDecisions(existing);
  next.push({
    schemaVersion: SCHEMA_VERSION,
    hunkId: String(decision.hunkId),
    fileKey: String(decision.fileKey).replace(/\\/g, '/'),
    action: decision.action,
    originalStartLine: Number.isInteger(decision.originalStartLine) ? decision.originalStartLine : -1,
    resolvedStartLine: Number.isInteger(decision.resolvedStartLine) ? decision.resolvedStartLine : -1,
    lineCount: Number.isInteger(decision.lineCount) ? decision.lineCount : 0,
    relocated: Boolean(decision.relocated),
    decidedUtc: String(decision.decidedUtc || new Date().toISOString())
  });
  return next.slice(-MAX_DECISIONS);
}

module.exports = {
  SCHEMA_VERSION,
  MAX_DECISIONS,
  sha256,
  captureHunk,
  resolveHunk,
  rangesOverlap,
  normalizeDecisions,
  appendDecision
};
