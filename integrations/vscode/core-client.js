'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const http = require('http');
const https = require('https');

const PROFILES = Object.freeze({
  offline: 'offline',
  packageFetch: 'package-fetch',
  trustedRestore: 'trusted-restore'
});

function defaultDataRoot(platform = process.platform, env = process.env, home = os.homedir()) {
  if (platform === 'win32') {
    const local = env.LOCALAPPDATA;
    if (!local) throw new Error('LOCALAPPDATA is unavailable; configure parux.dataRoot explicitly.');
    return path.join(local, 'PaRuX');
  }
  if (platform === 'darwin') return path.join(home, 'Library', 'Application Support', 'PaRuX');
  return path.join(env.XDG_DATA_HOME || path.join(home, '.local', 'share'), 'PaRuX');
}

function resolveDataRoot(configured) {
  const value = (configured || '').trim();
  return path.resolve(value || defaultDataRoot());
}

function isLoopbackHost(hostname) {
  const value = (hostname || '').toLowerCase();
  return value === 'localhost' || value === '127.0.0.1' || value === '::1' || value === '[::1]';
}

function parseSafeBootstrap(raw) {
  const bootstrap = typeof raw === 'string' ? JSON.parse(raw) : raw;
  if (!bootstrap || typeof bootstrap.endpoint !== 'string' || typeof bootstrap.token !== 'string')
    throw new Error('PaRuX Core bootstrap is missing endpoint/token.');
  const endpoint = new URL(bootstrap.endpoint);
  if ((endpoint.protocol !== 'http:' && endpoint.protocol !== 'https:') || !isLoopbackHost(endpoint.hostname))
    throw new Error('Refusing to send the PaRuX Core token to a non-loopback endpoint.');
  if (!bootstrap.token.trim()) throw new Error('PaRuX Core bootstrap token is empty.');
  return { ...bootstrap, endpoint: endpoint.toString().replace(/\/$/, '') };
}

function normalizeProfile(value) {
  const normalized = (value || '').trim().toLowerCase();
  if (normalized === PROFILES.packageFetch || normalized === PROFILES.trustedRestore) return normalized;
  return PROFILES.offline;
}

function normalizeAdditionalRepositories(values) {
  if (!Array.isArray(values)) return [];
  return values.map(value => ({
    alias: String(value?.alias || '').trim(),
    workspaceRoot: String(value?.workspaceRoot || '').trim()
  }));
}

class PaRuXCoreClient {
  constructor(options = {}) {
    this.dataRoot = resolveDataRoot(options.dataRoot);
    this.fs = options.fs || fs;
    this.http = options.http || http;
    this.https = options.https || https;
  }

  loadBootstrap() {
    const file = path.join(this.dataRoot, 'server', 'bootstrap.json');
    let raw;
    try { raw = this.fs.readFileSync(file, 'utf8'); }
    catch (error) { throw new Error(`PaRuX Core bootstrap is unavailable at ${file}: ${error.message}`); }
    return parseSafeBootstrap(raw);
  }

  request(method, route, body) {
    const bootstrap = this.loadBootstrap();
    const target = new URL(route, bootstrap.endpoint + '/');
    if (!isLoopbackHost(target.hostname)) throw new Error('Refusing a PaRuX Core request outside loopback.');
    const payload = body === undefined || body === null ? null : Buffer.from(JSON.stringify(body), 'utf8');
    const transport = target.protocol === 'https:' ? this.https : this.http;
    const options = {
      method,
      hostname: target.hostname.replace(/^\[|\]$/g, ''),
      port: target.port || (target.protocol === 'https:' ? 443 : 80),
      path: target.pathname + target.search,
      headers: {
        'X-PaRuX-Token': bootstrap.token,
        'Accept': 'application/json'
      }
    };
    if (payload) {
      options.headers['Content-Type'] = 'application/json';
      options.headers['Content-Length'] = payload.length;
    }
    if (target.protocol === 'https:') {
      // The endpoint has already been proven loopback-only; PaRuX may use a local self-signed cert.
      options.agent = new https.Agent({ rejectUnauthorized: false });
    }

    return new Promise((resolve, reject) => {
      const req = transport.request(options, response => {
        const chunks = [];
        response.on('data', chunk => chunks.push(chunk));
        response.on('end', () => {
          const text = Buffer.concat(chunks).toString('utf8');
          if (response.statusCode < 200 || response.statusCode >= 300)
            return reject(new Error(`PaRuX Core request failed (${response.statusCode}): ${text}`));
          if (!text.trim()) return resolve(null);
          try { resolve(JSON.parse(text)); }
          catch (error) { reject(new Error(`PaRuX Core returned invalid JSON: ${error.message}`)); }
        });
      });
      req.on('error', reject);
      req.setTimeout(30000, () => req.destroy(new Error('PaRuX Core request timed out.')));
      if (payload) req.write(payload);
      req.end();
    });
  }

  listTasks() { return this.request('GET', '/api/v1/orchestrator/tasks'); }
  getTask(id) { return this.request('GET', `/api/v1/orchestrator/tasks/${encodeURIComponent(id)}`); }
  getLogs(id) { return this.request('GET', `/api/v1/orchestrator/tasks/${encodeURIComponent(id)}/logs`); }
  getDesktopWorktree(id) { return this.request('GET', `/api/v1/orchestrator/tasks/${encodeURIComponent(id)}/desktop-worktree`); }
  getRepositorySet(id) { return this.request('GET', `/api/v1/orchestrator/tasks/${encodeURIComponent(id)}/repositories`); }
  replaceRepositorySet(id, primaryWorkspaceRoot, additionalRepositories = []) {
    return this.request('PUT', `/api/v1/orchestrator/tasks/${encodeURIComponent(id)}/repositories`, {
      primaryWorkspaceRoot,
      additionalRepositories: normalizeAdditionalRepositories(additionalRepositories)
    });
  }
  schedule() { return this.request('POST', '/api/v1/orchestrator/schedule'); }
  stopTask(id) { return this.request('POST', `/api/v1/orchestrator/tasks/${encodeURIComponent(id)}/cancel`); }
  resumeTask(id, source = 'VS Code') {
    return this.request('POST', `/api/v1/orchestrator/tasks/${encodeURIComponent(id)}/resume`, { source });
  }
  createTask(goal, role = 'reasoning', maxRepairAttempts = 0) {
    return this.request('POST', '/api/v1/orchestrator/tasks', {
      goal: String(goal || '').trim(),
      role,
      maxRepairAttempts: Math.max(0, Math.min(3, Number(maxRepairAttempts) || 0)),
      grantedCapabilities: []
    });
  }
  createCodingTask(goal, workspaceRoot, profile, maxRepairAttempts = 2, additionalRepositories = []) {
    return this.request('POST', '/api/v1/orchestrator/coding-tasks', {
      goal,
      workspaceRoot,
      additionalRepositories: normalizeAdditionalRepositories(additionalRepositories),
      codingPermissionProfile: normalizeProfile(profile),
      maxRepairAttempts
    });
  }
}

module.exports = {
  PROFILES,
  PaRuXCoreClient,
  defaultDataRoot,
  resolveDataRoot,
  isLoopbackHost,
  parseSafeBootstrap,
  normalizeProfile,
  normalizeAdditionalRepositories
};
