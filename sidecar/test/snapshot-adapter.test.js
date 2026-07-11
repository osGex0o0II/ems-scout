'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');
const {
  adaptLegacySnapshot,
  buildDeviceSourceKey,
  parseArguments,
  serializeSnapshot,
} = require('../snapshot-adapter');
const { stableStringify, validateSchema } = require('../../scripts/audit-contracts');
const snapshotSchema = require('../../contracts/collection-snapshot-v1.schema.json');

const ROOT = path.join(__dirname, '..', '..');
const ADAPTER = path.join(ROOT, 'sidecar', 'snapshot-adapter.js');
const AUDITOR = path.join(ROOT, 'scripts', 'audit-contracts.js');
const RUN17 = path.join(ROOT, 'out', 'enum_full_v5.json');
const ALL_VERSIONS = Object.freeze({
  collector: 'legacy-enum-v5',
  playwright: '1.60.0',
  rules: '2026-07-10',
  databaseSchema: 'legacy-v0',
  sourceRevision: 'fixture-revision',
});

test('adapter normalizes missing measurements and retains exact legacy source evidence', () => {
  const legacy = legacyFixture();
  const snapshot = adaptLegacySnapshot(legacy, {
    workflowId: 'fixture-workflow',
    versions: ALL_VERSIONS,
  });

  assert.deepEqual(validateSchema(snapshot, snapshotSchema), []);
  assert.deepEqual(snapshot.counts, {
    buildingCount: 1,
    subAreaCount: 1,
    pageCount: 1,
    rawCardCount: 3,
    uniqueCardCount: 2,
  });
  assert.equal(snapshot.quality.decision, 'accepted_with_findings');

  const subArea = snapshot.buildings[0].subAreas[0];
  assert.deepEqual(subArea.sourceEvidence, { err: null });
  const page = subArea.pages[0];
  assert.deepEqual(page.sourceEvidence, {
    count: 2,
    onHref: 'on.png',
    offHref: 'off.png',
    qualityReason: 'device_anomalies_preserved',
    duplicateNames: [{ name: 'A/1-KT', copies: 2 }],
    err: null,
  });
  assert.equal(page.duplicates.length, 1);
  assert.equal(page.duplicates[0].copies, 2);
  assert.equal(page.duplicates[0].sourceKeys.length, 2);
  assert.equal(new Set(page.duplicates[0].sourceKeys).size, 2);

  const missing = page.cards[0];
  assert.deepEqual({
    switch: missing.switch,
    mode: missing.mode,
    indoor: missing.indoor,
    setTemp: missing.setTemp,
    fan: missing.fan,
    indicator: missing.indicator,
    comm: missing.comm,
  }, {
    switch: null,
    mode: null,
    indoor: null,
    setTemp: null,
    fan: null,
    indicator: null,
    comm: '未知',
  });
  assert.deepEqual(missing.sourceEvidence, {
    raw: {
      name: 'A/1-KT',
      switch: '-',
      mode: '',
      indoor: '0',
      setTemp: '-',
      fan: '0',
      indicator: '',
      comm: '',
    },
    nameFloor: 1,
  });
  assert.equal(Object.hasOwn(missing, '_nameFloor'), false);
  assert.equal(serializeSnapshot(snapshot).includes('"_nameFloor"'), false);

  const keys = page.cards.map(card => card.sourceKey);
  assert.equal(new Set(keys).size, keys.length);
  assert.match(keys[0], /^sk1_[a-f0-9]{64}$/);
  assert.equal(page.duplicates[0].sourceKeys[0], keys[0]);
  assert.equal(page.duplicates[0].sourceKeys[1], buildDeviceSourceKey({
    building: '1号',
    subAreaIndex: 0,
    pageName: 'default',
    deviceName: 'A/1-KT',
    occurrence: 2,
  }));
  assert.equal(page.cards[1].indoor, 24.5);
  assert.equal(page.cards[1].setTemp, 26);
});

test('device source keys match C# v1 cross-language identity vectors', () => {
  const identity = {
    building: '1号',
    subAreaIndex: 22,
    pageName: '三页',
    deviceName: '22F-2201-KT',
  };
  assert.equal(
    buildDeviceSourceKey(identity),
    'sk1_008f1bd84aaf9b34bc00e0b7ed9c93c17b0b6018e8f2868922aff630ff07c6d7');
  assert.equal(
    buildDeviceSourceKey({ ...identity, occurrence: 2 }),
    'sk1_815705cef655f8ddc212194d9b0d240b0445df44894e81201d2461df1da60a1e');

  const composed = buildDeviceSourceKey({
    building: ' b1 ', subAreaIndex: 3, pageName: ' Page-A ', deviceName: 'CAFÉ-KT',
  });
  const decomposed = buildDeviceSourceKey({
    building: 'B1', subAreaIndex: 3, pageName: 'page-a', deviceName: 'CAFE\u0301-KT',
  });
  assert.equal(composed, decomposed);
});

test('canonical payload hash and complete output are byte deterministic', () => {
  const options = { workflowId: 'deterministic-workflow', versions: ALL_VERSIONS };
  const first = adaptLegacySnapshot(legacyFixture(), options);
  const second = adaptLegacySnapshot(legacyFixture(), options);
  const canonicalBuildings = Buffer.from(stableStringify(first.buildings, 0), 'utf8');

  assert.equal(serializeSnapshot(first), serializeSnapshot(second));
  assert.equal(first.artifact.bytes, canonicalBuildings.length);
  assert.equal(
    first.artifact.sha256,
    crypto.createHash('sha256').update(canonicalBuildings).digest('hex'));

  const changedTime = adaptLegacySnapshot(legacyFixture(), {
    ...options,
    completedAt: '2026-07-11T01:02:03.000Z',
  });
  assert.notEqual(serializeSnapshot(first), serializeSnapshot(changedTime));
  assert.equal(first.artifact.sha256, changedTime.artifact.sha256);
  assert.equal(first.artifact.bytes, changedTime.artifact.bytes);
});

test('CLI accepts scope, lineage, and version metadata without modifying its input', t => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-snapshot-adapter-'));
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const input = path.join(temp, 'legacy.json');
  const firstOutput = path.join(temp, 'snapshot-1.json');
  const secondOutput = path.join(temp, 'snapshot-2.json');
  fs.writeFileSync(input, JSON.stringify(legacyFixture()), 'utf8');
  const before = fileIdentity(input);
  const baseHash = 'a'.repeat(64);
  const common = [
    '--input', input,
    '--workflow-id', 'cli-workflow',
    '--scope', JSON.stringify({ mode: 'building', buildings: ['1号'], targets: [] }),
    '--lineage', JSON.stringify({ baseArtifactSha256: baseHash, parentWorkflowId: 'parent-1' }),
    '--version', JSON.stringify(ALL_VERSIONS),
  ];

  const first = spawnSync(process.execPath, [ADAPTER, ...common, '--output', firstOutput], {
    cwd: ROOT,
    encoding: 'utf8',
  });
  const second = spawnSync(process.execPath, [ADAPTER, ...common, '--output', secondOutput], {
    cwd: ROOT,
    encoding: 'utf8',
  });
  assert.equal(first.status, 0, first.stderr);
  assert.equal(second.status, 0, second.stderr);
  assert.deepEqual(fileIdentity(input), before);
  assert.deepEqual(fs.readFileSync(firstOutput), fs.readFileSync(secondOutput));

  const snapshot = JSON.parse(fs.readFileSync(firstOutput, 'utf8'));
  assert.deepEqual(snapshot.scope, { mode: 'building', buildings: ['1号'], targets: [] });
  assert.deepEqual(snapshot.lineage, {
    baseArtifactSha256: baseHash,
    parentWorkflowId: 'parent-1',
  });
  assert.deepEqual(snapshot.versions, ALL_VERSIONS);

  const audit = spawnSync(process.execPath, [AUDITOR, firstOutput], {
    cwd: ROOT,
    encoding: 'utf8',
  });
  assert.equal(audit.status, 0, audit.stderr);
  const audited = JSON.parse(audit.stdout).files[0];
  assert.equal(audited.shape, 'collection-snapshot/v1');
  assert.equal(audited.contractValidation.valid, true);
});

test('CLI refuses to overwrite the legacy input', () => {
  assert.throws(() => parseArguments([
    '--input', '/tmp/input.json',
    '--output', '/tmp/input.json',
    '--workflow-id', 'workflow-1',
  ]), /must not overwrite/);
});

test('current run17 converts read-only with 6568 unique card source keys', t => {
  if (!fs.existsSync(RUN17)) {
    t.skip('run17 production fixture is not available');
    return;
  }
  const before = fileIdentity(RUN17);
  const legacy = JSON.parse(fs.readFileSync(RUN17, 'utf8'));
  const snapshot = adaptLegacySnapshot(legacy, {
    workflowId: 'run17-read-only-verification',
    versions: ALL_VERSIONS,
  });
  const after = fileIdentity(RUN17);

  assert.deepEqual(after, before);
  assert.deepEqual(snapshot.counts, {
    buildingCount: 6,
    subAreaCount: 143,
    pageCount: 373,
    rawCardCount: 6571,
    uniqueCardCount: 6568,
  });
  assert.deepEqual(validateSchema(snapshot, snapshotSchema), []);

  const cards = snapshot.buildings.flatMap(building =>
    building.subAreas.flatMap(subArea => subArea.pages.flatMap(page => page.cards)));
  const pages = snapshot.buildings.flatMap(building =>
    building.subAreas.flatMap(subArea => subArea.pages));
  assert.equal(cards.length, 6568);
  assert.equal(new Set(cards.map(card => card.sourceKey)).size, 6568);
  assert.ok(cards.every(card => /^sk1_[a-f0-9]{64}$/.test(card.sourceKey)));
  const duplicateExtras = pages.flatMap(page => page.duplicates)
    .reduce((total, duplicate) => total + duplicate.copies - 1, 0);
  assert.equal(duplicateExtras, 3);
  assert.equal(cards.length + duplicateExtras, 6571);
  for (const page of pages) {
    const cardsByName = new Map(page.cards.map(card => [card.name, card]));
    for (const duplicate of page.duplicates) {
      assert.equal(duplicate.sourceKeys[0], cardsByName.get(duplicate.name).sourceKey);
      assert.ok(duplicate.sourceKeys.every(key => /^sk1_[a-f0-9]{64}$/.test(key)));
      assert.equal(new Set(duplicate.sourceKeys).size, duplicate.copies);
    }
  }
  assert.equal(cards.filter(card => card.sourceEvidence.nameFloor !== null).length, 2664);
  assert.equal(serializeSnapshot(snapshot).includes('"_nameFloor"'), false);

  const payload = Buffer.from(stableStringify(snapshot.buildings, 0), 'utf8');
  assert.equal(snapshot.artifact.bytes, payload.length);
  assert.equal(
    snapshot.artifact.sha256,
    crypto.createHash('sha256').update(payload).digest('hex'));
});

function legacyFixture() {
  return {
    buildings: [{
      building: '1号',
      menuClicked: '1号楼',
      subAreaCount: 1,
      subAreas: [{
        idx: 0,
        floor: 1,
        text: '1F',
        x: 10,
        y: 20,
        pages: [{
          page: 'default',
          count: 2,
          rawCount: 3,
          uniqueCount: 2,
          duplicateNames: [{ name: 'A/1-KT', copies: 2 }],
          onHref: 'on.png',
          offHref: 'off.png',
          layout: 'grid',
          qualityReason: 'device_anomalies_preserved',
          cards: [{
            name: 'A/1-KT',
            switch: '-',
            mode: '',
            indoor: '0',
            setTemp: '-',
            fan: '0',
            indicator: '',
            comm: '',
            _nameFloor: 1,
          }, {
            name: 'B-KT',
            switch: 'ON',
            mode: '制冷',
            indoor: '24.5',
            setTemp: '26',
            fan: '低',
            indicator: 'running.png',
            comm: '开机',
          }],
        }],
      }],
    }],
    completedAt: '2026-07-11T00:00:00.000Z',
  };
}

function fileIdentity(filePath) {
  const stat = fs.statSync(filePath, { bigint: true });
  return {
    sha256: crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex'),
    size: stat.size,
    mtimeNs: stat.mtimeNs,
  };
}
