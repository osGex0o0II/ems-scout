'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { cardIdentity, filterFloorGroups, tabIsActive } = require('../../src/page-navigation');

test('floor filtering removes action-row duplicates and preserves BM', () => {
  const groups = [
    { id: 'main-1', floor: 1, text: '1F', y: 100 },
    { id: 'main-2', floor: 2, text: '2F', y: 104 },
    { id: 'action-1', floor: 1, text: '1F', y: 226 },
    { id: 'bm', floor: -2, text: 'BM', y: 244 },
  ];

  assert.deepEqual(filterFloorGroups(groups).map(item => item.id), ['main-1', 'main-2', 'bm']);
});

test('page and tab identity checks use accepted cards and active target text', () => {
  assert.equal(cardIdentity([{ name: 'B' }, { name: 'A' }]), 'A\u0000B');
  assert.equal(tabIsActive([{ txt: '塔楼', isActive: true }], { txt: '塔楼' }), true);
  assert.equal(tabIsActive([{ txt: '裙楼', isActive: true }], { txt: '塔楼' }), false);
});
