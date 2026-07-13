'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { sanitizeErrorForDisplay, sanitizeUrlForDisplay } = require('../../src/url-sanitizer');

const root = path.resolve(__dirname, '../..');

test('URL display sanitizer removes credentials query and fragment', () => {
  assert.equal(
    sanitizeUrlForDisplay('https://user:pass@example.com:8443/ui/path?token=secret#fragment'),
    'https://example.com:8443/ui/path');
  assert.equal(sanitizeUrlForDisplay('not a url'), '<invalid-url>');
});

test('error sanitizer removes raw and percent-encoded credential URLs', () => {
  const secretUrl = 'https://user:pass@example.com/ui?ticket=TOPSECRET#session';
  const error = new Error(`goto ${secretUrl} via /json/new?${encodeURIComponent(secretUrl)}`);
  const safe = sanitizeErrorForDisplay(error, [secretUrl]);
  assert.doesNotMatch(safe, /user|pass|TOPSECRET|session|%3Fticket/i);
  assert.match(safe, /https:\/\/example\.com\/ui/);
  assert.doesNotMatch(safe, /\n\s+at /);
});

test('full EMS URL stays out of logs evidence and child-process arguments', () => {
  const enumerator = fs.readFileSync(path.join(root, 'src/enumerate.js'), 'utf8');
  const inspector = fs.readFileSync(path.join(root, 'scripts/inspect-ems-source.js'), 'utf8');
  const verifier = fs.readFileSync(path.join(root, 'src/verify-live.js'), 'utf8');
  const realtimeBrowser = fs.readFileSync(path.join(root, 'scripts/realtime-browser.js'), 'utf8');
  const fieldE2e = fs.readFileSync(path.join(root, 'scripts/field-e2e.ps1'), 'utf8');
  const waitLogin = fs.readFileSync(path.join(root, 'scripts/wait-ems-login.js'), 'utf8');
  const optionsSource = fs.readFileSync(path.join(root, 'src/enumerate-options.js'), 'utf8');
  const viewModel = fs.readFileSync(path.join(
    root, 'native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs'), 'utf8');

  assert.doesNotMatch(enumerator, /\blog\([^\n]*\$\{EMS_URL\}/);
  assert.match(enumerator, /sanitizeUrlForDisplay\(EMS_URL\)/);
  assert.match(inspector, /emsUrl:\s*sanitizeUrlForDisplay\(EMS_URL\)/);
  assert.doesNotMatch(verifier, /log\(`URL: \$\{state\.url\}/);
  assert.match(verifier, /sanitizeUrlForDisplay\(state\.url\)/);
  assert.doesNotMatch(viewModel, /"--ems-url="\s*\+\s*settings\.EmsUrl/);
  assert.match(viewModel, /\["EMS_URL"\]\s*=\s*settings\.EmsUrl/);
  const enumerationMethod = viewModel.slice(
    viewModel.indexOf('private async Task RunEnumerationAsync'),
    viewModel.indexOf('private async Task RunImportAsync'));
  assert.match(enumerationMethod, /BuildTaskEnvironment\(settings\)/);
  assert.doesNotMatch(enumerationMethod, /BuildDataEnvironment\(/);
  assert.doesNotMatch(realtimeBrowser, /^\s*emsUrl,\s*$/m);
  assert.match(realtimeBrowser, /sanitizeUrlForDisplay\(emsUrl\)/);
  assert.doesNotMatch(fieldE2e, /<\$\(\$page\.url\)>/);
  assert.match(waitLogin, /sanitizeErrorForDisplay/);
  assert.match(inspector, /sanitizeErrorForDisplay/);
  assert.doesNotMatch(optionsSource + inspector + waitLogin, /argValue\('--ems-url'|argumentValue\(argv, '--ems-url'/);
});
