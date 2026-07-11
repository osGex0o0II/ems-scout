'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { createEnumerationOutputStore } = require('../../src/enumerate-output');

const timestamp = new Date('2026-07-11T12:00:00.000Z');

test('non-append output starts clean and accumulates this process in building order', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-enum-output-'));
  const outputFile = path.join(directory, 'enum_full_v5.json');
  fs.writeFileSync(outputFile, JSON.stringify({ buildings: [{ building: '6号', stale: true }] }));

  const store = createEnumerationOutputStore({ outputFile, now: () => timestamp });
  store.saveBuilding({ building: '2号', cards: 2 });
  store.saveBuilding({ building: '1号', cards: 1 });
  const output = JSON.parse(fs.readFileSync(outputFile, 'utf8'));

  assert.deepEqual(output.buildings.map(building => building.building), ['1号', '2号']);
  assert.equal(output.completedAt, timestamp.toISOString());
  assert.deepEqual(fs.readdirSync(directory), ['enum_full_v5.json']);
  fs.rmSync(directory, { recursive: true, force: true });
});

test('append output preserves other buildings and replaces the selected building', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-enum-output-'));
  const outputFile = path.join(directory, 'enum_full_v5.json');
  fs.writeFileSync(outputFile, JSON.stringify({
    buildings: [
      { building: '1号', version: 'old' },
      { building: '3号', version: 'keep' },
    ],
  }));

  const store = createEnumerationOutputStore({ outputFile, append: true, now: () => timestamp });
  store.saveBuilding({ building: '1号', version: 'new' });
  const output = JSON.parse(fs.readFileSync(outputFile, 'utf8'));

  assert.deepEqual(output.buildings, [
    { building: '1号', version: 'new' },
    { building: '3号', version: 'keep' },
  ]);
  fs.rmSync(directory, { recursive: true, force: true });
});
