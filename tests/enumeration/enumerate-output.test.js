'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { createEnumerationOutputStore } = require('../../src/enumerate-output');

const timestamp = new Date('2026-07-11T12:00:00.000Z');

test('non-append output publishes the whole run atomically in building order', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-enum-output-'));
  const outputFile = path.join(directory, 'enum_full_v5.json');
  fs.writeFileSync(outputFile, JSON.stringify({ buildings: [{ building: '6号', stale: true }] }));

  const store = createEnumerationOutputStore({ outputFile, now: () => timestamp });
  store.saveRun([
    { building: '2号', cards: 2 },
    { building: '1号', cards: 1 },
  ]);
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
  store.saveRun([{ building: '1号', version: 'new' }]);
  const output = JSON.parse(fs.readFileSync(outputFile, 'utf8'));

  assert.deepEqual(output.buildings, [
    { building: '1号', version: 'new' },
    { building: '3号', version: 'keep' },
  ]);
  fs.rmSync(directory, { recursive: true, force: true });
});

test('recapture output replaces only matching sub-areas and preserves the formal inventory', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-enum-output-'));
  const outputFile = path.join(directory, 'enum_full_v5.json');
  fs.writeFileSync(outputFile, JSON.stringify({
    buildings: [
      {
        building: '1号',
        menuClicked: '1号',
        subAreas: [
          { floor: 1, x: 10, y: 20, pages: [{ cards: [{ name: 'replace-old' }] }] },
          { floor: 2, x: 30, y: 40, pages: [{ cards: [{ name: 'keep-sub-area' }] }] },
        ],
      },
      { building: '2号', subAreas: [{ floor: 1, x: 50, y: 60, keep: true }] },
    ],
  }));

  const store = createEnumerationOutputStore({ outputFile, now: () => timestamp });
  const output = store.saveRecapture([{
    building: '1号',
    subAreas: [{ floor: 1, x: 10, y: 20, pages: [{ cards: [{ name: 'replace-new' }] }] }],
  }]);

  assert.equal(output.buildings[0].menuClicked, '1号');
  assert.equal(output.buildings[0].subAreas[0].pages[0].cards[0].name, 'replace-new');
  assert.equal(output.buildings[0].subAreas[1].pages[0].cards[0].name, 'keep-sub-area');
  assert.equal(output.buildings[1].subAreas[0].keep, true);
  assert.equal(output.completedAt, timestamp.toISOString());
  assert.deepEqual(JSON.parse(fs.readFileSync(outputFile, 'utf8')), output);
  fs.rmSync(directory, { recursive: true, force: true });
});

test('recapture output is not published when a target is absent or merged validation fails', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-enum-output-'));
  const outputFile = path.join(directory, 'enum_full_v5.json');
  const original = JSON.stringify({
    buildings: [{
      building: '1号',
      subAreas: [{ floor: 1, x: 10, y: 20, pages: [{ cards: [{ name: 'formal' }] }] }],
    }],
  });
  fs.writeFileSync(outputFile, original);
  const store = createEnumerationOutputStore({ outputFile, now: () => timestamp });

  assert.throws(() => store.saveRecapture([{
    building: '1号',
    subAreas: [{ floor: 9, x: 90, y: 90, pages: [] }],
  }]), /not found/i);
  assert.equal(fs.readFileSync(outputFile, 'utf8'), original);

  assert.throws(() => store.saveRecapture([{
    building: '1号',
    subAreas: [{ floor: 1, x: 10, y: 20, pages: [{ cards: [{ name: 'rejected' }] }] }],
  }], { validate: () => ({ ok: false }) }), /validation/i);
  assert.equal(fs.readFileSync(outputFile, 'utf8'), original);
  fs.rmSync(directory, { recursive: true, force: true });
});

test('enumerator publishes recapture through merged full-building validation', () => {
  const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'enumerate.js'), 'utf8');

  assert.match(source, /outputStore\.saveRecapture\(capturedBuildings/);
  assert.match(source, /validateEnumData\(mergedOutput, \{ buildings: selectedBuildings \}\)/);
  assert.match(source, /auditCollectedOutput\(\{\s*buildings: mergedOutput\.buildings\.filter/);
});

test('recovery output never replaces the formal output', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-enum-output-'));
  const outputFile = path.join(directory, 'enum_full_v5.json');
  const original = JSON.stringify({ buildings: [{ building: '6号', formal: true }] });
  fs.writeFileSync(outputFile, original);

  const store = createEnumerationOutputStore({ outputFile, now: () => timestamp });
  const recoveryFile = store.saveRecovery({ building: '2号', partial: true });

  assert.equal(fs.readFileSync(outputFile, 'utf8'), original);
  assert.equal(path.basename(recoveryFile), 'enum_full_v5.recovery.json');
  assert.deepEqual(JSON.parse(fs.readFileSync(recoveryFile, 'utf8')).buildings, [
    { building: '2号', partial: true },
  ]);
  fs.rmSync(directory, { recursive: true, force: true });
});

test('append refuses a corrupted formal output instead of replacing it', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-enum-output-'));
  const outputFile = path.join(directory, 'enum_full_v5.json');
  fs.writeFileSync(outputFile, '{broken');
  const store = createEnumerationOutputStore({ outputFile, append: true, now: () => timestamp });

  assert.throws(() => store.saveRun([{ building: '1号' }]), /JSON/);
  assert.equal(fs.readFileSync(outputFile, 'utf8'), '{broken');
  fs.rmSync(directory, { recursive: true, force: true });
});

test('append refuses a parseable formal output with an invalid buildings shape', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-enum-output-'));
  const outputFile = path.join(directory, 'enum_full_v5.json');
  const original = JSON.stringify({ buildings: {}, metadata: 'preserve' });
  fs.writeFileSync(outputFile, original);
  const store = createEnumerationOutputStore({ outputFile, append: true, now: () => timestamp });

  assert.throws(() => store.saveRun([{ building: '1号' }]), /buildings array/);
  assert.equal(fs.readFileSync(outputFile, 'utf8'), original);
  fs.rmSync(directory, { recursive: true, force: true });
});
