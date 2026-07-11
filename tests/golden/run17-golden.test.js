'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { adaptLegacySnapshot } = require('../../sidecar/snapshot-adapter');

const ROOT = path.resolve(__dirname, '../..');
const ENUMERATION = path.join(ROOT, 'out', 'enum_full_v5.json');
const GOLDEN = require('../fixtures/run17/golden-v1.json');

test('run17 enumeration and CollectionSnapshot conversion match the golden manifest', t => {
  if (!fs.existsSync(ENUMERATION)) {
    t.skip('run17 enumeration fixture is not available');
    return;
  }

  const before = fileIdentity(ENUMERATION);
  assert.equal(before.sha256, GOLDEN.sources.enumerationSha256);
  const enumeration = JSON.parse(fs.readFileSync(ENUMERATION, 'utf8'));
  assert.equal(enumeration.completedAt, GOLDEN.completedAt);
  const metrics = enumerationMetrics(enumeration);
  assert.deepEqual(metrics.status, GOLDEN.status);
  assert.deepEqual(metrics.qualityReasons, GOLDEN.qualityReasons);
  assert.deepEqual(metrics.buildings, GOLDEN.buildings);

  const snapshot = adaptLegacySnapshot(enumeration, {
    workflowId: 'run17-golden-v1',
    versions: {
      collector: 'legacy-enum-v5',
      playwright: '1.60.0',
      rules: '2026-07-10',
      databaseSchema: 'legacy-v0',
      sourceRevision: 'run17-golden-v1',
    },
  });
  assert.deepEqual(snapshot.counts, GOLDEN.snapshotCounts);
  assert.deepEqual(fileIdentity(ENUMERATION), before);
});

function enumerationMetrics(enumeration) {
  const metrics = { status: {}, qualityReasons: {}, buildings: {} };
  for (const building of enumeration.buildings || []) {
    const current = {
      subAreas: (building.subAreas || []).length,
      pages: 0,
      rawCards: 0,
      uniqueCards: 0,
      status: {},
    };
    for (const subArea of building.subAreas || []) {
      for (const page of subArea.pages || []) {
        const cards = page.cards || [];
        current.pages++;
        current.rawCards += page.rawCount ?? page.count ?? cards.length;
        current.uniqueCards += cards.length;
        increment(metrics.qualityReasons, page.qualityReason || '');
        for (const card of cards) {
          const status = card.comm || '未知';
          increment(current.status, status);
          increment(metrics.status, status);
        }
      }
    }
    current.status = orderedStatus(current.status);
    metrics.buildings[building.building] = current;
  }
  metrics.status = orderedStatus(metrics.status);
  return metrics;
}

function increment(target, key) {
  target[key] = (target[key] || 0) + 1;
}

function orderedStatus(value) {
  return Object.fromEntries(['开机', '关机', '离线', '未知']
    .filter(key => value[key] !== undefined)
    .map(key => [key, value[key]]));
}

function fileIdentity(filePath) {
  const stat = fs.statSync(filePath, { bigint: true });
  return {
    sha256: crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex'),
    size: stat.size,
    mtimeNs: stat.mtimeNs,
  };
}
