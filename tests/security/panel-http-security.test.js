'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const { Readable } = require('node:stream');
const { spawnSync } = require('node:child_process');
const test = require('node:test');
const { requestJson } = require('../../electron/server-manager');
const { createServer } = require('../../src/panel/server');

const {
  HttpRequestError,
  authorizeApiRequest,
  isLoopbackHost,
  readJsonBody,
  resolveStaticPath,
} = require('../../src/panel/http-security');

function request(body, headers = {}) {
  const req = Readable.from(body === undefined ? [] : [body]);
  req.headers = headers;
  return req;
}

function get(port, requestPath) {
  return new Promise((resolve, reject) => {
    const req = http.get({ hostname: '127.0.0.1', port, path: requestPath }, res => {
      res.resume();
      res.on('end', () => resolve(res));
    });
    req.on('error', reject);
  });
}

function send(port, requestPath, { method = 'GET', headers = {}, body = '' } = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request({ hostname: '127.0.0.1', port, path: requestPath, method, headers }, res => {
      let text = '';
      res.setEncoding('utf8');
      res.on('data', chunk => { text += chunk; });
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: text }));
    });
    req.on('error', reject);
    req.end(body);
  });
}

test('panel only accepts explicit loopback Host values', () => {
  assert.equal(isLoopbackHost('127.0.0.1:4173'), true);
  assert.equal(isLoopbackHost('localhost:4173'), true);
  assert.equal(isLoopbackHost('[::1]:4173'), true);
  assert.equal(isLoopbackHost('example.test:4173'), false);
  assert.equal(isLoopbackHost('127.0.0.1.example.test'), false);
  assert.equal(isLoopbackHost(''), false);
});

test('API authorization requires session token and same-origin mutations', () => {
  const token = 'session-secret';
  assert.doesNotThrow(() => authorizeApiRequest({
    method: 'GET',
    headers: { host: '127.0.0.1:4173', 'x-ems-panel-token': token },
  }, token));
  assert.throws(() => authorizeApiRequest({
    method: 'GET', headers: { host: '127.0.0.1:4173' },
  }, token), error => error instanceof HttpRequestError && error.statusCode === 403);
  assert.doesNotThrow(() => authorizeApiRequest({
    method: 'POST',
    headers: {
      host: '127.0.0.1:4173',
      origin: 'http://127.0.0.1:4173',
      'x-ems-panel-token': token,
    },
  }, token));
  assert.throws(() => authorizeApiRequest({
    method: 'POST',
    headers: {
      host: '127.0.0.1:4173',
      origin: 'https://evil.test',
      'x-ems-panel-token': token,
    },
  }, token), error => error instanceof HttpRequestError && error.statusCode === 403);
});

test('JSON body limit counts bytes and reports boundary errors', async () => {
  await assert.rejects(
    readJsonBody(request(undefined, { 'content-length': '1048577' })),
    error => error instanceof HttpRequestError && error.statusCode === 413,
  );
  await assert.rejects(
    readJsonBody(request('{"value":"中"}', { 'content-type': 'text/plain' })),
    error => error instanceof HttpRequestError && error.statusCode === 415,
  );
  await assert.rejects(
    readJsonBody(request('{broken', { 'content-type': 'application/json' })),
    error => error instanceof HttpRequestError && error.statusCode === 400,
  );
  assert.deepEqual(await readJsonBody(request('{"ok":true}', {
    'content-type': 'application/json; charset=utf-8',
  })), { ok: true });
});

test('static path resolution cannot escape through sibling-prefix traversal', () => {
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-panel-static-'));
  const root = path.join(parent, 'panel');
  const sibling = path.join(parent, 'panel-private');
  fs.mkdirSync(root);
  fs.mkdirSync(sibling);
  fs.writeFileSync(path.join(root, 'index.html'), 'panel');
  fs.writeFileSync(path.join(sibling, 'secret.txt'), 'secret');
  try {
    assert.equal(resolveStaticPath(root, '/index.html'), path.join(root, 'index.html'));
    assert.throws(() => resolveStaticPath(root, '/../panel-private/secret.txt'),
      error => error instanceof HttpRequestError && error.statusCode === 403);
  } finally {
    fs.rmSync(parent, { recursive: true, force: true });
  }
});

test('Electron panel client establishes a session before protected API calls', async () => {
  const server = createServer();
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  try {
    const health = await requestJson('GET', '/api/health', undefined, {
      port: server.address().port,
      timeoutMs: 2000,
    });
    assert.equal(health.ok, true);
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
});

test('live panel responses cannot be embedded by another site', async () => {
  const server = createServer();
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  try {
    for (const requestPath of ['/panel/', '/api/session']) {
      const response = await get(server.address().port, requestPath);
      assert.equal(response.headers['x-frame-options'], 'DENY');
      assert.match(response.headers['content-security-policy'] || '', /frame-ancestors 'none'/);
    }
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
});

test('direct legacy panel bootstrap remains disabled without the environment gate', () => {
  const entry = path.join(__dirname, '../../src/panel/server.js');
  const disabled = spawnSync(process.execPath, [entry, '--check'], {
    cwd: path.join(__dirname, '../..'),
    env: { ...process.env, EMS_ENABLE_LEGACY_PANEL: '' },
    encoding: 'utf8',
  });
  assert.equal(disabled.status, 2);
  assert.match(disabled.stderr, /disabled by default/i);

  const enabled = spawnSync(process.execPath, [entry, '--check'], {
    cwd: path.join(__dirname, '../..'),
    env: { ...process.env, EMS_ENABLE_LEGACY_PANEL: '1' },
    encoding: 'utf8',
  });
  assert.equal(enabled.status, 0, enabled.stderr);
  assert.match(enabled.stdout, /"ok": true/);
});

test('live panel enforces Host token Origin body and static-path boundaries', async () => {
  const server = createServer();
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  try {
    const port = server.address().port;
    const session = await send(port, '/api/session');
    const token = JSON.parse(session.body).data.token;
    const auth = { 'x-ems-panel-token': token };
    const largeBody = 'x'.repeat(1024 * 1024 + 1);
    const results = await Promise.all([
      send(port, '/api/not-found', { headers: auth }),
      send(port, '/api/not-found'),
      send(port, '/api/session', { headers: { host: 'evil.test' } }),
      send(port, '/api/tasks/stop', {
        method: 'POST', headers: { ...auth, origin: 'https://evil.test' },
      }),
      send(port, '/api/tasks', {
        method: 'POST',
        headers: { ...auth, 'content-type': 'application/json', 'content-length': Buffer.byteLength(largeBody) },
        body: largeBody,
      }),
      send(port, '/%2e%2e%5cpackage.json'),
    ]);
    assert.deepEqual(results.map(result => result.status), [404, 403, 403, 403, 413, 403]);
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
});
