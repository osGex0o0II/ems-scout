'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');

const root = path.resolve(__dirname, '../..');
const helper = path.join(root, 'scripts', 'prepare-sidecar-helpers.ps1');

function run(expression) {
  return spawnSync('powershell.exe', [
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
    `. '${helper.replaceAll("'", "''")}'; ${expression}`,
  ], { cwd: root, encoding: 'utf8' });
}

test('sidecar cleanup path must remain inside its explicit owned root', () => {
  const expression = [
    "$owned = Join-Path $env:TEMP 'ems-owned'",
    "$cases = @((Join-Path $owned 'child'), (Join-Path $env:TEMP 'data\\1号楼'), (Split-Path $owned -Parent), ($owned + '-sibling'))",
    "$cases | ForEach-Object { try { Assert-SafeOwnedDirectory $_ 'test' $owned; 'allow' } catch { 'reject' } }",
  ].join('; ');
  const result = run(expression);
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(result.stdout.trim().split(/\r?\n/), ['allow', 'reject', 'reject', 'reject']);
});
