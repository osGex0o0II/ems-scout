'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { validateEnumData } = require('../../src/enum-validator');

function cards(count = 107) {
  return Array.from({ length: count }, (_, index) => ({
    name: `2-${String(index + 1).padStart(4, '0')}-KT`,
    switch: 'OFF',
    comm: '关机',
  }));
}

function result(subAreas) {
  return { buildings: [{ building: '2号', subAreas }] };
}

test('validator rejects an explicitly failed subarea even when aggregate counts match', () => {
  const subAreas = [{ floor: 1, text: '1F', pages: [{ page: '一页', cards: cards() }] }];
  while (subAreas.length < 5) subAreas.push({ floor: subAreas.length + 1, err: 'click failed', pages: [] });

  const validation = validateEnumData(result(subAreas), { buildings: ['2号'] });

  assert.equal(validation.ok, false);
  assert.match(validation.errors.join('\n'), /click failed/);
});

test('validator rejects stale and page error markers', () => {
  const validation = validateEnumData(result([
    { floor: 1, text: '1F', pages: [{ page: '一页', stale: true, cards: cards(54) }] },
    { floor: 2, text: '2F', pages: [{ page: '一页', err: 'page failed', cards: cards(53) }] },
  ]), { buildings: ['2号'] });

  assert.equal(validation.ok, false);
  assert.match(validation.errors.join('\n'), /stale/);
  assert.match(validation.errors.join('\n'), /page failed/);
});

test('validator requires every requested building and preserves the historical bm-inline marker', () => {
  const accepted = validateEnumData(result([
    { floor: -2, err: 'bm inline', pages: [] },
    { floor: 1, text: '1F', pages: [{ page: '一页', cards: cards() }] },
    { floor: 2, text: '2F', pages: [] },
    { floor: 3, text: '3F', pages: [] },
    { floor: 4, text: '4F', pages: [] },
  ]), { buildings: ['2号'] });
  const missing = validateEnumData(result([
    { floor: 1, text: '1F', pages: [{ page: '一页', cards: cards() }] },
  ]), { buildings: ['1号', '2号'] });

  assert.equal(accepted.ok, true);
  assert.equal(missing.ok, false);
  assert.match(missing.errors.join('\n'), /1号/);
});
