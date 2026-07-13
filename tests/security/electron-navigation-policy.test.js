'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const { isExternalBrowserUrl, isSamePanelOrigin } = require('../../electron/navigation-policy');

test('legacy Electron navigation uses exact panel origin and safe external schemes', () => {
  const panel = 'http://127.0.0.1:17777';
  assert.equal(isSamePanelOrigin('http://127.0.0.1:17777/panel', panel), true);
  assert.equal(isSamePanelOrigin('http://127.0.0.1:17777.evil.test/panel', panel), false);
  assert.equal(isSamePanelOrigin('http://127.0.0.1:17778/panel', panel), false);
  assert.equal(isExternalBrowserUrl('https://example.com/docs'), true);
  assert.equal(isExternalBrowserUrl('file:///C:/secret'), false);
  assert.equal(isExternalBrowserUrl('custom-handler://payload'), false);
  assert.equal(isExternalBrowserUrl('javascript:alert(1)'), false);
});

test('legacy Electron renderer sandbox remains enabled', () => {
  const source = fs.readFileSync(path.join(__dirname, '../../electron/window.js'), 'utf8');
  assert.match(source, /sandbox:\s*true/);
  assert.doesNotMatch(source, /nextUrl\.startsWith\(url\)/);
});
