#!/usr/bin/env node
'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { adaptLegacySnapshot, serializeSnapshot } = require('./snapshot-adapter');

const CANCEL_EXIT_CODE = 130;
const FORCE_KILL_DELAY_MS = 4000;

function collectionPaths(argv, env = process.env, root = path.join(__dirname, '..')) {
  const outputArgument = argv.find(argument => argument.startsWith('--out-dir='));
  const outputDirectory = path.resolve(
    outputArgument ? outputArgument.slice('--out-dir='.length) : env.EMS_OUT_DIR || path.join(root, 'out'));
  return {
    legacyPath: path.join(outputDirectory, 'enum_full_v5.json'),
    snapshotPath: path.resolve(env.EMS_SNAPSHOT_PATH || path.join(outputDirectory, 'collection_snapshot_v1.json')),
  };
}

function snapshotVersions(env = process.env) {
  let playwright = 'unknown';
  try {
    playwright = require('playwright/package.json').version || playwright;
  } catch {
  }
  return {
    collector: 'enum-v5-sidecar/1',
    playwright,
    rules: env.EMS_RULES_VERSION || 'ems-rules-js/v1',
    databaseSchema: 'v2-identity',
    sourceRevision: env.EMS_SOURCE_REVISION || 'unknown',
  };
}

function collectionScope(argv) {
  return argv.some(argument => argument === '--bldg' || argument.startsWith('--bldg='))
    ? 'building'
    : 'full';
}

function adaptCollectedFile({ legacyPath, snapshotPath, workflowId, scope, env = process.env }) {
  if (!workflowId) throw new Error('EMS_WORKFLOW_ID is required');
  if (path.resolve(legacyPath) === path.resolve(snapshotPath)) {
    throw new Error('CollectionSnapshot output must not overwrite the legacy evidence file');
  }
  const legacy = JSON.parse(fs.readFileSync(legacyPath, 'utf8').replace(/^\uFEFF/, ''));
  const snapshot = adaptLegacySnapshot(legacy, {
    workflowId,
    scope,
    versions: snapshotVersions(env),
  });
  writeAtomic(snapshotPath, serializeSnapshot(snapshot));
  return snapshot;
}

function writeAtomic(outputPath, text) {
  const absolute = path.resolve(outputPath);
  fs.mkdirSync(path.dirname(absolute), { recursive: true });
  const temporary = `${absolute}.tmp-${process.pid}-${Date.now()}`;
  try {
    fs.writeFileSync(temporary, text, { encoding: 'utf8', flag: 'wx' });
    fs.renameSync(temporary, absolute);
  } finally {
    fs.rmSync(temporary, { force: true });
  }
}

async function runCollection(argv, options = {}) {
  const env = options.env || process.env;
  const root = options.root || path.join(__dirname, '..');
  const collectorScript = options.collectorScript || path.join(root, 'src', 'enumerate.js');
  const runtime = options.runtime || process.execPath;
  const spawnProcess = options.spawnProcess || spawn;
  const paths = collectionPaths(argv, env, root);

  let child = null;
  let terminalAction = null;
  let stdoutRemainder = '';
  let cancellationRequested = false;
  let forceKillTimer = null;
  const requestCancellation = () => {
    if (cancellationRequested) return;
    cancellationRequested = true;
    if (child && !child.killed) {
      try { child.kill('SIGINT'); } catch {}
      forceKillTimer = setTimeout(() => {
        try { child.kill('SIGKILL'); } catch {}
      }, FORCE_KILL_DELAY_MS);
      forceKillTimer.unref();
    }
  };
  const signalHandlers = new Map([
    ['SIGINT', requestCancellation],
    ['SIGTERM', requestCancellation],
  ]);
  for (const [signal, handler] of signalHandlers) process.on(signal, handler);

  try {
    child = spawnProcess(runtime, [collectorScript, ...argv], {
      cwd: root,
      env,
      shell: false,
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    child.stdout.on('data', chunk => {
      const text = chunk.toString();
      process.stdout.write(text);
      const lines = (stdoutRemainder + text).split(/\r?\n/);
      stdoutRemainder = lines.pop() || '';
      for (const line of lines) {
        const match = /^\[ACTION\]\s*(return|switch_to_cdp)\s*$/.exec(line.trim());
        if (match) terminalAction = match[1];
      }
    });
    child.stderr.pipe(process.stderr, { end: false });
    const result = await new Promise((resolve, reject) => {
      child.once('error', reject);
      child.once('close', (code, signal) => resolve({ code, signal }));
    });
    if (cancellationRequested || result.signal) return CANCEL_EXIT_CODE;
    if (result.code !== 0) return Number.isInteger(result.code) ? result.code : 1;
    const trailingAction = /^\[ACTION\]\s*(return|switch_to_cdp)\s*$/.exec(stdoutRemainder.trim());
    if (trailingAction) terminalAction = trailingAction[1];
    if (terminalAction) {
      process.stderr.write(`[collection-sidecar] adapter skipped after action=${terminalAction}\n`);
      return 0;
    }

    const snapshot = adaptCollectedFile({
      ...paths,
      workflowId: env.EMS_WORKFLOW_ID,
      scope: collectionScope(argv),
      env,
    });
    process.stderr.write(
      `[collection-sidecar] snapshot=${paths.snapshotPath} cards=${snapshot.counts.uniqueCardCount} ` +
      `sha256=${snapshot.artifact.sha256}\n`);
    return 0;
  } finally {
    if (forceKillTimer) clearTimeout(forceKillTimer);
    for (const [signal, handler] of signalHandlers) process.removeListener(signal, handler);
  }
}

async function main() {
  try {
    process.exitCode = await runCollection(process.argv.slice(2));
  } catch (error) {
    process.stderr.write(`[collection-sidecar] ${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  }
}

if (require.main === module) void main();

module.exports = {
  adaptCollectedFile,
  collectionScope,
  collectionPaths,
  runCollection,
  snapshotVersions,
};
