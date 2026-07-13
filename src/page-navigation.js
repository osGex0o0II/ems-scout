'use strict';

function cardIdentity(cards = []) {
  return cards.map(card => String(card && card.name || '')).filter(Boolean).sort().join('\u0000');
}

function filterFloorGroups(groups = []) {
  const floorYs = groups.filter(group => group.text !== 'BM').map(group => Number(group.y)).filter(Number.isFinite);
  if (floorYs.length === 0) return groups.slice();
  floorYs.sort((left, right) => left - right);
  const medianY = floorYs[Math.floor(floorYs.length / 2)];
  return groups.filter(group => group.text === 'BM' || Math.abs(Number(group.y) - medianY) <= 30);
}

function tabIsActive(tabs = [], target = {}) {
  return tabs.some(tab => tab.txt === target.txt && tab.isActive === true);
}

module.exports = { cardIdentity, filterFloorGroups, tabIsActive };
