'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { auditCollectedOutput, pageFromData, summarizeCardStates } = require('../../src/capture-result');

function card(overrides = {}) {
  return {
    name: '1-0101-KT',
    switch: 'OFF',
    indoor: '26',
    setTemp: '24',
    mode: '制冷',
    fan: '中',
    indicator: 'green.png',
    comm: '关机',
    ...overrides,
  };
}

function outputWith(pageRow) {
  return {
    buildings: [{
      building: '2号',
      subAreas: [{ floor: 2.5, text: '2.5F', pages: [pageRow] }],
    }],
  };
}

test('page result normalizes the exact known missing-indicator devices', () => {
  const sourceCards = [
    card({ name: '2-2BC-2M001-KT-1', switch: 'ON', indoor: '25', indicator: 'red.png', comm: '开机' }),
    card({ name: '2-2BC-2M001-KT-2', indoor: '27', setTemp: '25', indicator: 'green.png', comm: '关机' }),
  ];

  const result = pageFromData('一页', {
    count: 2,
    rawCount: 2,
    uniqueCount: 2,
    duplicateNames: null,
    cards: sourceCards,
  });

  assert.equal(result.qualityReason, 'known_source_indicator_missing');
  assert.deepEqual(result.cards.map(item => [item.name, item.indicator, item.comm]), [
    ['2-2BC-2M001-KT-1', '', ''],
    ['2-2BC-2M001-KT-2', '', ''],
  ]);
  assert.deepEqual(result.duplicateNames, []);
  assert.equal(sourceCards[0].indicator, 'red.png');
});

test('final audit accepts evidence-backed exceptions and rejects stale_partial', () => {
  const knownPage = pageFromData('known', {
    count: 2,
    cards: [
      card({ name: '2-2BC-2M001-KT-1', switch: 'ON', indoor: '25' }),
      card({ name: '2-2BC-2M001-KT-2', indoor: '27', setTemp: '25' }),
    ],
  });
  const rejectedPage = {
    page: 'rejected',
    count: 1,
    qualityReason: 'stable_partial',
    cards: [card({ name: '', comm: '' })],
  };

  assert.deepEqual(auditCollectedOutput(outputWith(knownPage)), []);
  assert.equal(auditCollectedOutput(outputWith(rejectedPage)).length, 1);
});

test('final audit does not trust a quality label that contradicts the cards', () => {
  const invalidQualityPass = {
    page: 'invalid-pass',
    count: 1,
    qualityReason: 'quality_pass',
    cards: [card({ name: '0-0001-KT', switch: '-', indicator: '', comm: '' })],
  };
  const invalidOfflineEvidence = {
    page: 'invalid-offline',
    count: 2,
    qualityReason: 'offline_template_stable',
    cards: [
      card({ name: 'A', switch: 'OFF', indoor: '0', setTemp: '0', indicator: '', comm: '离线' }),
      card({ name: 'A', switch: 'OFF', indoor: '0', setTemp: '0', indicator: '', comm: '离线' }),
    ],
  };

  assert.equal(auditCollectedOutput(outputWith(invalidQualityPass)).length, 1);
  assert.equal(auditCollectedOutput(outputWith(invalidOfflineEvidence)).length, 1);
});

test('final audit reports the source location and quality details', () => {
  const [issue] = auditCollectedOutput(outputWith({
    page: '二页',
    count: 1,
    cards: [card({ name: '0-0001-KT', switch: '-', indicator: '', comm: '' })],
  }));

  assert.deepEqual({
    building: issue.building,
    floor: issue.floor,
    subArea: issue.subArea,
    page: issue.page,
    reason: issue.reason,
  }, {
    building: '2号',
    floor: 2.5,
    subArea: '2.5F',
    page: '二页',
    reason: 'missing_quality_reason',
  });
  assert.match(issue.details, /ph=1\/1/);
});

test('final audit rejects structural errors and stale pages before card quality', () => {
  const validPage = { page: '二页', count: 1, stale: true, cards: [card()] };
  const output = outputWith(validPage);
  output.buildings[0].subAreas[0].err = 'tab click failed';

  const issues = auditCollectedOutput(output);

  assert.equal(issues.some(issue => issue.reason === 'subarea_error'), true);
  assert.equal(issues.some(issue => issue.reason === 'stale_page'), true);
});

test('verify summary uses authoritative communication state without href inference', () => {
  const summary = summarizeCardStates([
    card({ name: 'A', comm: '开机', indicator: 'red.png' }),
    card({ name: 'B', comm: '关机', indicator: 'green.png' }),
    card({ name: 'C', comm: '离线', indicator: 'gray.png' }),
    card({ name: 'D', comm: '', indicator: 'mystery.png' }),
  ]);

  assert.deepEqual(summary.counts, { 开机: 1, 关机: 1, 离线: 1, 未知: 1 });
  assert.deepEqual(Object.fromEntries(
    Object.entries(summary.cardsByState).map(([state, cards]) => [state, cards.map(item => item.name)])), {
    开机: ['A'], 关机: ['B'], 离线: ['C'], 未知: ['D'],
  });
});
