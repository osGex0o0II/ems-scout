'use strict';

const http = require('http');
const path = require('path');
const { spawn } = require('child_process');

const ROOT = path.join(__dirname, '..');
const DEFAULT_PORT = 17777;
const HEALTH_PATH = '/api/health';
const SERVER_ENTRY = path.join(ROOT, 'src', 'panel', 'server.js');

let ownedProcess = null;
let restartTimer = null;
let stopping = false;
let serviceOptions = null;
let restartCount = 0;
let starting = false;

function panelUrl(port = DEFAULT_PORT) {
  return `http://127.0.0.1:${port}`;
}

function requestJson(method, routePath, body, options = {}) {
  const port = options.port || DEFAULT_PORT;
  const timeoutMs = options.timeoutMs || 30000;
  const payload = body === undefined ? null : JSON.stringify(body);

  return new Promise((resolve, reject) => {
    const req = http.request({
      hostname: '127.0.0.1',
      port,
      path: routePath,
      method,
      timeout: timeoutMs,
      headers: {
        accept: 'application/json',
        ...(payload ? {
          'content-type': 'application/json; charset=utf-8',
          'content-length': Buffer.byteLength(payload),
        } : {}),
      },
    }, res => {
      let text = '';
      res.setEncoding('utf8');
      res.on('data', chunk => { text += chunk; });
      res.on('end', () => {
        let parsed = null;
        try {
          parsed = text ? JSON.parse(text) : null;
        } catch (err) {
          reject(new Error(`Invalid JSON from ${routePath}: ${err.message}`));
          return;
        }
        if (res.statusCode < 200 || res.statusCode >= 300 || (parsed && parsed.ok === false)) {
          reject(new Error((parsed && parsed.error) || `HTTP ${res.statusCode} ${routePath}`));
          return;
        }
        resolve(parsed && Object.prototype.hasOwnProperty.call(parsed, 'data') ? parsed.data : parsed);
      });
    });

    req.on('timeout', () => {
      req.destroy(new Error(`Request timeout: ${routePath}`));
    });
    req.on('error', reject);
    if (payload) req.write(payload);
    req.end();
  });
}

async function isHealthy(port = DEFAULT_PORT) {
  try {
    const health = await requestJson('GET', HEALTH_PATH, undefined, { port, timeoutMs: 1500 });
    return !!(health && health.ok !== false);
  } catch {
    return false;
  }
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

async function waitForHealth(port = DEFAULT_PORT, timeoutMs = 30000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (await isHealthy(port)) return true;
    await delay(300);
  }
  return false;
}

function nodeRuntimeEnv() {
  if (process.env.EMS_NODE_RUNTIME) {
    return { command: process.env.EMS_NODE_RUNTIME, extraEnv: {} };
  }
  if (process.defaultApp) {
    return { command: process.env.NODE || 'node', extraEnv: {} };
  }
  return {
    command: process.execPath,
    extraEnv: { ELECTRON_RUN_AS_NODE: '1' },
  };
}

function spawnServer(port, logger) {
  const runtime = nodeRuntimeEnv();
  const child = spawn(runtime.command, [SERVER_ENTRY, `--port=${port}`], {
    cwd: ROOT,
    env: {
      ...process.env,
      EMS_PANEL_PORT: String(port),
      ...runtime.extraEnv,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  });

  ownedProcess = child;
  logger.info('started panel server process', { pid: child.pid, port, runtime: runtime.command });

  child.stdout.on('data', chunk => {
    logger.info('[server]', chunk.toString('utf8').trimEnd());
  });
  child.stderr.on('data', chunk => {
    logger.error('[server]', chunk.toString('utf8').trimEnd());
  });
  child.on('error', err => {
    logger.error('panel server process error', err);
  });
  child.on('exit', (code, signal) => {
    logger.warn('panel server process exited', { pid: child.pid, code, signal, stopping });
    if (ownedProcess === child) ownedProcess = null;
    if (!stopping && !starting && serviceOptions && restartCount < 5) {
      restartCount += 1;
      clearTimeout(restartTimer);
      restartTimer = setTimeout(() => {
        spawnServer(serviceOptions.port, serviceOptions.logger);
      }, Math.min(1000 * restartCount, 5000));
    }
  });

  return child;
}

function waitForProcessReady(child, port, logger) {
  return new Promise((resolve, reject) => {
    let settled = false;
    const timeout = setTimeout(() => {
      if (settled) return;
      settled = true;
      cleanup();
      resolve();
    }, 1200);

    function cleanup() {
      clearTimeout(timeout);
      child.off('exit', onExit);
      child.off('error', onError);
    }

    function onExit(code, signal) {
      if (settled) return;
      settled = true;
      cleanup();
      reject(new Error(`Panel server exited before health check at ${panelUrl(port)} (code=${code}, signal=${signal || ''}). Port may be occupied by a non-Panel process.`));
    }

    function onError(err) {
      if (settled) return;
      settled = true;
      cleanup();
      logger.error('panel server failed to start', err);
      reject(err);
    }

    child.once('exit', onExit);
    child.once('error', onError);
  });
}

async function ensureRunning(options = {}) {
  const port = Number(options.port || process.env.EMS_PANEL_PORT || DEFAULT_PORT);
  const logger = options.logger || console;
  serviceOptions = { port, logger };
  stopping = false;

  if (await isHealthy(port)) {
    logger.info('panel server already healthy', { url: panelUrl(port) });
    return { owned: false, url: panelUrl(port), port, pid: null };
  }

  if (!ownedProcess) {
    restartCount = 0;
    starting = true;
    spawnServer(port, logger);
    try {
      await waitForProcessReady(ownedProcess, port, logger);
    } finally {
      starting = false;
    }
  }

  const healthy = await waitForHealth(port, options.timeoutMs || 30000);
  if (!healthy) {
    throw new Error(`Panel server did not become healthy at ${panelUrl(port)}${HEALTH_PATH}`);
  }
  return { owned: true, url: panelUrl(port), port, pid: ownedProcess && ownedProcess.pid };
}

function stopOwnedServer(logger = console) {
  stopping = true;
  clearTimeout(restartTimer);
  restartTimer = null;
  const proc = ownedProcess;
  ownedProcess = null;
  if (!proc || proc.killed) return;

  logger.info('stopping owned panel server', { pid: proc.pid });
  if (process.platform === 'win32') {
    const killer = spawn('taskkill', ['/PID', String(proc.pid), '/T', '/F'], {
      windowsHide: true,
      stdio: 'ignore',
    });
    killer.on('error', err => logger.error('taskkill failed', err));
    return;
  }
  proc.kill('SIGTERM');
}

module.exports = {
  DEFAULT_PORT,
  panelUrl,
  requestJson,
  isHealthy,
  waitForHealth,
  ensureRunning,
  stopOwnedServer,
};
