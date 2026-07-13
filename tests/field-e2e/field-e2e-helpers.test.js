'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');

const root = path.resolve(__dirname, '../..');
const helper = path.join(root, 'scripts', 'field-e2e-helpers.ps1');

function run(expression) {
  return spawnSync('powershell.exe', [
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
    `. '${helper.replaceAll("'", "''")}'; ${expression}`,
  ], { cwd: root, encoding: 'utf8' });
}

test('Windows argument quoting preserves spaces, wildcard characters, quotes and trailing slashes', () => {
  const result = run("ConvertTo-WindowsCommandLine @('plain', 'C:\\A B\\[x]\\profile\\', 'a\"b')");
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), 'plain "C:\\A B\\[x]\\profile\\\\" "a\\\"b"');
});

test('profile matcher requires the exact user-data-dir argument', () => {
  const result = run("@((Test-ProfileCommandLine 'msedge --user-data-dir=\"C:\\A B\\[x]\"' 'C:\\A B\\[x]'), (Test-ProfileCommandLine 'msedge --user-data-dir=\"C:\\A B\\[x]-old\"' 'C:\\A B\\[x]')) -join ','");
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), 'True,False');
});

test('profile matcher accepts the exact command-line quoting emitted for Edge', () => {
  const result = run("$profile = 'C:\\A B\\[x]'; $arg = ConvertTo-WindowsCommandLine @(('--user-data-dir=' + $profile)); @((Test-ProfileCommandLine ('msedge ' + $arg) $profile), (Test-ProfileCommandLine ('msedge ' + $arg) ($profile + '-old'))) -join ','");
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), 'True,False');
});

test('EMS URL sanitizer strips credentials query and fragment', () => {
  const result = run("Get-SanitizedEmsUrl 'https://user:pass@example.com:8443/ui/path?token=secret#frag'");
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), 'https://example.com:8443/ui/path');
});

test('credential-bearing EMS URL is refused on the command line', () => {
  const result = run("try { Assert-SafeEmsUrlForCommandLine 'https://example.com/ui?ticket=secret'; 'allowed' } catch { 'rejected' }");
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), 'rejected');
});
