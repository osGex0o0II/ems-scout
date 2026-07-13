'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');
const { matchesEmsPageUrl, normalizeEmsUrl, parseEnumerateOptions } = require('../../src/enumerate-options');

test('enumeration options parse paths, buildings, recapture and diagnostics without global state', () => {
  const root = path.resolve('/isolated/app');
  const options = parseEnumerateOptions([
    '--edge',
    '--append',
    '--bldg=1号, 2号',
    '--recapture=3号:1087:144,6号:194:158',
    '--out-dir=./capture',
    '--cdp-url=http://127.0.0.1:9333',
    '--log-level=DEBUG',
    '--log-category=RULE,VUE',
    '--log-file',
    '--no-net-monitor',
  ], { EMS_URL: 'http://ems.local:8000/ui/#/home' }, root);

  assert.equal(options.USE_CDP, true);
  assert.equal(options.APPEND, true);
  assert.deepEqual(options.FILTER, ['1号', '2号']);
  assert.deepEqual(options.RECAPTURE_TARGETS, [
    { building: '3号', x: 1087, y: 144 },
    { building: '6号', x: 194, y: 158 },
  ]);
  assert.equal(options.RECAPTURE_MODE, true);
  assert.equal(options.OUT_DIR, path.resolve('./capture'));
  assert.equal(options.OUT_FILE, path.join(path.resolve('./capture'), 'enum_full_v5.json'));
  assert.equal(options.EMS_URL, 'http://ems.local:8000/ui/');
  assert.equal(options.CDP_URL, 'http://127.0.0.1:9333');
  assert.equal(options.ENABLE_NETWORK_MONITOR, false);
  assert.equal(options.LOG_LEVEL, 'DEBUG');
  assert.equal(options.LOG_CATEGORIES, 'RULE,VUE');
  assert.equal(options.LOG_FILE, true);
});

test('EMS URL matching uses configured host and UI path', () => {
  const expected = normalizeEmsUrl('http://ems.local:8000/ui/#/home');
  assert.equal(matchesEmsPageUrl('http://ems.local:8000/ui/#/aircon', expected), true);
  assert.equal(matchesEmsPageUrl('http://other.local:8000/ui/', expected), false);
  assert.equal(matchesEmsPageUrl('/ui/fallback', 'invalid-url'), true);
});
