'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const http = require('http');
const {
  PaRuXCoreClient,
  PROFILES,
  defaultDataRoot,
  isLoopbackHost,
  normalizeProfile,
  normalizeAdditionalRepositories,
  parseSafeBootstrap
} = require('../core-client');

test('bootstrap rejects non-loopback endpoint before token can be used', () => {
  assert.throws(() => parseSafeBootstrap({ endpoint: 'https://example.com:443', token: 'secret' }), /non-loopback/i);
  assert.doesNotThrow(() => parseSafeBootstrap({ endpoint: 'http://127.0.0.1:12345', token: 'secret' }));
  assert.doesNotThrow(() => parseSafeBootstrap({ endpoint: 'http://localhost:12345', token: 'secret' }));
});

test('loopback host policy is explicit', () => {
  assert.equal(isLoopbackHost('127.0.0.1'), true);
  assert.equal(isLoopbackHost('localhost'), true);
  assert.equal(isLoopbackHost('::1'), true);
  assert.equal(isLoopbackHost('192.168.1.10'), false);
  assert.equal(isLoopbackHost('parux.local'), false);
});

test('profile normalization is fail-closed', () => {
  assert.equal(normalizeProfile(PROFILES.packageFetch), 'package-fetch');
  assert.equal(normalizeProfile(PROFILES.trustedRestore), 'trusted-restore');
  assert.equal(normalizeProfile('typo'), 'offline');
  assert.equal(normalizeProfile(null), 'offline');
});

test('additional repository payload normalization strips accidental whitespace', () => {
  assert.deepEqual(normalizeAdditionalRepositories([
    { alias: ' backend ', workspaceRoot: ' C:\\repo\\backend ' }
  ]), [{ alias: 'backend', workspaceRoot: 'C:\\repo\\backend' }]);
  assert.deepEqual(normalizeAdditionalRepositories(null), []);
});

test('Windows data root mirrors PaRuX LocalApplicationData convention', () => {
  assert.equal(defaultDataRoot('win32', { LOCALAPPDATA: 'C:\\Users\\Test\\AppData\\Local' }, 'C:\\Users\\Test'),
    path.join('C:\\Users\\Test\\AppData\\Local', 'PaRuX'));
});

test('Core request sends token only to validated loopback and preserves atomic multi-repository and resume payloads', async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'parux-vscode-test-'));
  const serverDir = path.join(root, 'server');
  fs.mkdirSync(serverDir, { recursive: true });
  const observed = [];

  const server = http.createServer((req, res) => {
    const chunks = [];
    req.on('data', chunk => chunks.push(chunk));
    req.on('end', () => {
      observed.push({
        method: req.method,
        url: req.url,
        token: req.headers['x-parux-token'],
        body: chunks.length ? JSON.parse(Buffer.concat(chunks).toString('utf8')) : null
      });
      res.writeHead(200, { 'Content-Type': 'application/json' });
      if (req.method === 'PUT') {
        res.end(JSON.stringify({ taskId: 'task-1', repositories: [] }));
      } else if (req.url.endsWith('/resume')) {
        res.end(JSON.stringify({ id: 'task-1', role: 'coding', status: 'running' }));
      } else {
        res.end(JSON.stringify({ id: 'task-1', role: 'coding', status: 'queued' }));
      }
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => {
    server.close();
    try { fs.rmSync(root, { recursive: true, force: true }); } catch { }
  });

  const address = server.address();
  fs.writeFileSync(path.join(serverDir, 'bootstrap.json'), JSON.stringify({
    endpoint: `http://127.0.0.1:${address.port}`,
    token: 'test-token'
  }));

  const client = new PaRuXCoreClient({ dataRoot: root });
  const additional = [{ alias: ' backend ', workspaceRoot: path.join(root, 'backend') }];
  const result = await client.createCodingTask('Fix it', root, PROFILES.trustedRestore, 2, additional);
  await client.replaceRepositorySet('task-1', root, [{ alias: 'frontend', workspaceRoot: path.join(root, 'frontend') }]);
  const resumed = await client.resumeTask('task-1', 'VS Code test');

  assert.equal(result.id, 'task-1');
  assert.equal(resumed.status, 'running');
  assert.equal(observed.length, 3);
  assert.deepEqual(observed[0], {
    method: 'POST',
    url: '/api/v1/orchestrator/coding-tasks',
    token: 'test-token',
    body: {
      goal: 'Fix it',
      workspaceRoot: root,
      additionalRepositories: [{ alias: 'backend', workspaceRoot: path.join(root, 'backend') }],
      codingPermissionProfile: 'trusted-restore',
      maxRepairAttempts: 2
    }
  });
  assert.deepEqual(observed[1], {
    method: 'PUT',
    url: '/api/v1/orchestrator/tasks/task-1/repositories',
    token: 'test-token',
    body: {
      primaryWorkspaceRoot: root,
      additionalRepositories: [{ alias: 'frontend', workspaceRoot: path.join(root, 'frontend') }]
    }
  });
  assert.deepEqual(observed[2], {
    method: 'POST',
    url: '/api/v1/orchestrator/tasks/task-1/resume',
    token: 'test-token',
    body: { source: 'VS Code test' }
  });
});
