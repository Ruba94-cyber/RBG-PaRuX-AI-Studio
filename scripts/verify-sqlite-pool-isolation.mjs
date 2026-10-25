#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname), '..');
const server = path.join(root, 'src', 'PaRuX.Server');
const forbidden = /\bSqliteConnection\s*\.\s*ClearAllPools\s*\(/g;
const violations = [];

function walk(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const absolute = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      walk(absolute);
      continue;
    }
    if (!entry.isFile() || !entry.name.endsWith('.cs')) continue;
    const text = fs.readFileSync(absolute, 'utf8');
    for (const match of text.matchAll(forbidden)) {
      const line = text.slice(0, match.index).split(/\r?\n/).length;
      violations.push(`${path.relative(root, absolute)}:${line}`);
    }
  }
}

walk(server);

if (violations.length) {
  console.error('Process-wide SQLite pool eviction is forbidden in PaRuX.Server.');
  console.error('Use SqlitePoolIsolation.ReleaseBackupDestination or ClearDatabasePools instead.');
  for (const violation of violations) console.error(` - ${violation}`);
  process.exitCode = 1;
} else {
  console.log('PASS: PaRuX.Server contains no SqliteConnection.ClearAllPools calls.');
}
