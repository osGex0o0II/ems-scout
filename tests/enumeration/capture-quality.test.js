'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const {
  assessDataQuality,
  buildPartialSignature,
  isAcceptableCapture,
  isOfflineTemplateStable,
} = require('../../src/capture-quality');
const { checkCardQuality } = require('../../src/rules');

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

test('weighted assessment is combined with the strict card-quality gate', () => {
  const valid = [card(), card({ name: '1-0102-KT', switch: 'ON', comm: '开机' })];
  const missingComm = valid.map((item, index) => index === 1 ? { ...item, comm: '' } : item);
  const invalidTemperature = valid.map(item => ({ ...item, indoor: '0', setTemp: '0' }));

  assert.equal(assessDataQuality(valid).isGood, true);
  assert.equal(assessDataQuality(missingComm).isGood, false);
  assert.equal(assessDataQuality(invalidTemperature).isGood, true);
  assert.equal(isAcceptableCapture({ cards: invalidTemperature }), false);
});

test('offline template acceptance requires three identical rounds and 600ms', () => {
  const cards = [
    card({ name: 'A', switch: '-', indoor: '0', setTemp: '0', indicator: 'gray.png', comm: '离线' }),
    card({ name: 'B', switch: '-', indoor: '0', setTemp: '0', indicator: 'gray.png', comm: '离线' }),
  ];
  const qc = checkCardQuality(cards);
  const first = isOfflineTemplateStable(cards, qc, {}, 100);
  const second = isOfflineTemplateStable(cards, qc, first, 400);
  const third = isOfflineTemplateStable(cards, qc, second, 600);

  assert.equal(qc.uniformTemplate, true);
  assert.equal(first.accept, false);
  assert.equal(second.accept, false);
  assert.equal(third.accept, true);
  assert.equal(third.rounds, 3);
});

test('capture signatures and final acceptance are deterministic', () => {
  const cards = [card(), card({ name: '1-0102-KT' })];
  const data = { cards, rawCount: 2, uniqueCount: 2 };

  assert.equal(buildPartialSignature(cards), buildPartialSignature(cards.map(item => ({ ...item }))));
  assert.equal(isAcceptableCapture(data), true);
  assert.equal(isAcceptableCapture({ ...data, cards: cards.map(item => ({ ...item, comm: '' })) }), false);
});

test('numeric fan values are incomplete until mapped to the supported vocabulary', () => {
  const numeric = [card({ fan: '1' }), card({ name: '1-0102-KT', fan: '1' })];

  assert.equal(checkCardQuality(numeric).ok, false);
  for (const fan of ['低', '中', '高', '自动']) {
    assert.equal(checkCardQuality(numeric.map(item => ({ ...item, fan }))).ok, true);
  }
});
