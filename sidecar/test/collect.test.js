'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { adaptCollectedFile, collectionPaths, collectionScope, runCollection } = require('../collect');
const { validateSchema } = require('../../scripts/audit-contracts');
const snapshotSchema = require('../../contracts/collection-snapshot-v1.schema.json');

test('collection paths use the isolated output directory and explicit snapshot override', () => {
  const root = path.join(path.sep, 'application');
  assert.deepEqual(
    collectionPaths(['--edge', '--out-dir=/isolated/run'], {}, root),
    {
      legacyPath: path.resolve('/isolated/run/enum_full_v5.json'),
      snapshotPath: path.resolve('/isolated/run/collection_snapshot_v1.json'),
    });
  assert.equal(
    collectionPaths([], { EMS_OUT_DIR: '/data', EMS_SNAPSHOT_PATH: '/contract/snapshot.json' }, root).snapshotPath,
    path.resolve('/contract/snapshot.json'));
});

test('collection scope distinguishes a single-building capture from a full capture', () => {
  assert.equal(collectionScope(['--edge']), 'full');
  assert.equal(collectionScope(['--edge', '--bldg=1号']), 'building');
});

test('successful collection adaptation emits a valid workflow-bound snapshot', t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-collect-sidecar-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const legacyPath = path.join(directory, 'enum_full_v5.json');
  const snapshotPath = path.join(directory, 'collection_snapshot_v1.json');
  fs.writeFileSync(legacyPath, JSON.stringify({
    completedAt: '2026-07-11T08:00:00.000Z',
    buildings: [{
      building: '1号',
      subAreaCount: 1,
      subAreas: [{
        idx: 0,
        text: '1F',
        floor: 1,
        pages: [{
          page: 'default',
          rawCount: 1,
          uniqueCount: 1,
          qualityReason: 'quality_pass',
          cards: [{ name: '1-0101-KT', switch: 'OFF', comm: '关机' }],
        }],
      }],
    }],
  }), 'utf8');

  const snapshot = adaptCollectedFile({
    legacyPath,
    snapshotPath,
    workflowId: 'collect-fixture-1',
    env: {
      EMS_RULES_VERSION: 'rules-test',
      EMS_SOURCE_REVISION: 'revision-test',
    },
  });

  assert.equal(snapshot.workflowId, 'collect-fixture-1');
  assert.equal(snapshot.counts.uniqueCardCount, 1);
  assert.equal(snapshot.versions.rules, 'rules-test');
  assert.deepEqual(validateSchema(snapshot, snapshotSchema), []);
  assert.deepEqual(JSON.parse(fs.readFileSync(snapshotPath, 'utf8')), snapshot);
  assert.ok(fs.existsSync(legacyPath));
});

test('adapter refuses to replace its legacy evidence input', () => {
  assert.throws(() => adaptCollectedFile({
    legacyPath: '/same/file.json',
    snapshotPath: '/same/file.json',
    workflowId: 'collect-fixture-2',
  }), /must not overwrite/);
});

test('collection pipeline runs a collector before writing the v1 artifact', async t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-collect-pipeline-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const collector = path.join(directory, 'fixture-collector.js');
  fs.writeFileSync(collector, `
    const fs = require('node:fs');
    const path = require('node:path');
    const arg = process.argv.find(value => value.startsWith('--out-dir='));
    const out = arg.slice('--out-dir='.length);
    fs.mkdirSync(out, { recursive: true });
    fs.writeFileSync(path.join(out, 'enum_full_v5.json'), JSON.stringify({
      completedAt: '2026-07-11T08:00:00.000Z',
      buildings: [{ building: '1号', subAreaCount: 0, subAreas: [] }]
    }));
    console.log('[PROGRESS]' + JSON.stringify({ percent: 100 }));
  `, 'utf8');

  const code = await runCollection([`--out-dir=${directory}`, '--bldg=1号'], {
    collectorScript: collector,
    root: path.join(__dirname, '..', '..'),
    env: {
      ...process.env,
      EMS_WORKFLOW_ID: 'pipeline-fixture-1',
      EMS_SNAPSHOT_PATH: path.join(directory, 'snapshot.json'),
    },
  });

  assert.equal(code, 0);
  const snapshot = JSON.parse(fs.readFileSync(path.join(directory, 'snapshot.json'), 'utf8'));
  assert.equal(snapshot.workflowId, 'pipeline-fixture-1');
  assert.equal(snapshot.counts.buildingCount, 1);
  assert.equal(snapshot.scope.mode, 'building');
});
