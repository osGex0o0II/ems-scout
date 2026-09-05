'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { isAllowedEmsUrl, isAllowedCdpUrl, sanitizeUrlForDisplay } = require('../../src/connection-policy');

test('EMS URL validation compares origin and path boundaries', () => {
  const configured = 'http://ems.example:8000/ui/#/home/27161';
  assert.equal(isAllowedEmsUrl('http://ems.example:8000/ui/#/home/1', configured), true);
  assert.equal(isAllowedEmsUrl('http://ems.example:8000/ui-malicious/#/home/1', configured), false);
  assert.equal(isAllowedEmsUrl('http://ems.example:8001/ui/#/home/1', configured), false);
  assert.equal(isAllowedEmsUrl('http://other.example:8000/ui/#/home/1', configured), false);
  assert.equal(isAllowedEmsUrl('https://ems.example:8000/ui/#/home/1', configured), false);
  assert.equal(isAllowedEmsUrl('http://user:secret@ems.example:8000/ui/#/home/1', configured), false);
});

test('CDP validation rejects remote endpoints unless explicitly enabled', () => {
  assert.equal(isAllowedCdpUrl('http://127.0.0.1:9222', false), true);
  assert.equal(isAllowedCdpUrl('http://localhost:9222', false), true);
  assert.equal(isAllowedCdpUrl('http://192.168.1.20:9222', false), false);
  assert.equal(isAllowedCdpUrl('http://192.168.1.20:9222', true), true);
  assert.equal(isAllowedCdpUrl('http://user:secret@127.0.0.1:9222', false), false);
});

test('URL diagnostics never expose credentials, query, or fragment', () => {
  assert.equal(sanitizeUrlForDisplay('http://user:secret@ems.example:8000/ui?token=secret#/home'), 'http://ems.example:8000/ui');
});

test('retained Edge launch paths bind debugging endpoints to loopback', () => {
  const root = path.resolve(__dirname, '..', '..');
  const realtime = fs.readFileSync(path.join(root, 'scripts', 'realtime-browser.js'), 'utf8');
  const fieldE2e = fs.readFileSync(path.join(root, 'scripts', 'field-e2e.ps1'), 'utf8');
  assert.match(realtime, /--remote-debugging-address=127\.0\.0\.1/);
  assert.match(fieldE2e, /--remote-debugging-address=127\.0\.0\.1/);
});
