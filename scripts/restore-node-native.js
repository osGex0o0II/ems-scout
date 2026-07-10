'use strict';

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const ROOT = path.join(__dirname, '..');
const DEV_NATIVE = path.join(ROOT, 'node_modules', 'better-sqlite3', 'build', 'Release', 'better_sqlite3.node');

function fail(message) {
  console.error('[restore-node-native] ' + message);
  process.exit(1);
}

const npmCli = path.join(path.dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js');
if (!fs.existsSync(npmCli)) fail('npm CLI not found beside Node runtime: ' + npmCli);

const rebuild = spawnSync(process.execPath, [npmCli, 'rebuild', 'better-sqlite3'], {
  cwd: ROOT,
  stdio: 'inherit',
  env: process.env,
});
if (rebuild.error) fail('npm rebuild failed to start: ' + rebuild.error.message);
if (rebuild.status !== 0) process.exit(rebuild.status || 1);

if (!fs.existsSync(DEV_NATIVE)) fail('development native module missing after rebuild: ' + DEV_NATIVE);

const load = spawnSync(process.execPath, ['-e', "require('better-sqlite3'); console.log('dev native ok')"], {
  cwd: ROOT,
  stdio: 'inherit',
  env: process.env,
});
if (load.status !== 0) process.exit(load.status || 1);

console.log('[restore-node-native] development Node ABI ready');
