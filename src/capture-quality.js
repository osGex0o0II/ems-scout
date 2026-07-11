'use strict';

const {
  checkCardQuality,
  classifyPersistentDeviceAnomalyPage,
  classifyStableOfflineTemplatePage,
} = require('./rules');

function assessDataQuality(cards) {
  const n = cards.length;
  if (n === 0) return { isGood: false, score: 0, details: 'no cards' };

  const activeCards = cards.filter(c => c.comm === '开机' || c.comm === '关机');
  const activeN = activeCards.length;
  const switchRate = activeN > 0 ? activeCards.filter(c => c.switch === 'ON' || c.switch === 'OFF').length / activeN : 1;
  const tempRate = activeN > 0 ? activeCards.filter(c => {
    const indoor = parseFloat(c.indoor);
    const setTemp = parseFloat(c.setTemp);
    return Number.isFinite(indoor) && indoor > 0 && indoor <= 60 &&
      Number.isFinite(setTemp) && setTemp >= 5 && setTemp <= 40;
  }).length / activeN : 1;
  const commRate = cards.filter(c => c.comm).length / n;
  const modeRate = activeN > 0 ? activeCards.filter(c => c.mode !== '-' && c.fan !== '-' && c.fan !== '0').length / activeN : 1;
  const allOffline = cards.every(c => c.comm === '离线');
  const commComplete = cards.every(c => c.comm === '开机' || c.comm === '关机' || c.comm === '离线');

  const qualityScore = switchRate * 0.4 + tempRate * 0.3 + commRate * 0.2 + modeRate * 0.1;
  return {
    isGood: (allOffline || commComplete) && qualityScore >= 0.7,
    score: qualityScore,
    details: `score=${qualityScore.toFixed(2)} active=${activeN}/${n} switch=${switchRate.toFixed(2)} temp=${tempRate.toFixed(2)} comm=${commRate.toFixed(2)} mode=${modeRate.toFixed(2)}${allOffline ? ' allOffline' : ''}`,
  };
}

function buildPartialSignature(cards = []) {
  return cards.map(c => [
    c.name || '',
    c.switch || '',
    c.indoor || '',
    c.setTemp || '',
    c.mode || '',
    c.fan || '',
    c.indicator || '',
    c.comm || '',
  ].join('|')).join('||');
}

function isOfflineTemplateStable(cards, qc, prev = {}, elapsedMs = 0) {
  const classification = classifyStableOfflineTemplatePage(cards, {}, qc);
  if (!qc || !classification.eligible) {
    return { accept: false, signature: '', rounds: 0 };
  }
  const signature = buildPartialSignature(cards);
  const rounds = signature && signature === prev.signature ? (prev.rounds || 0) + 1 : 1;
  const accept = rounds >= 3 && elapsedMs >= 600;
  return { accept, signature, rounds };
}

function persistentDeviceAnomalyState(cards, meta, prev = {}) {
  const classification = classifyPersistentDeviceAnomalyPage(cards, meta);
  const rounds = classification.signature && classification.signature === prev.signature
    ? (prev.rounds || 0) + 1
    : (classification.eligible ? 1 : 0);
  return {
    ...classification,
    accept: classification.eligible && rounds >= 3,
    rounds,
  };
}

function isAcceptableCapture(data, qc = null, quality = null) {
  const cards = data && Array.isArray(data.cards) ? data.cards : [];
  const currentQc = qc || checkCardQuality(cards, data || {});
  const currentQuality = quality || assessDataQuality(cards);
  return currentQc.ok && currentQuality.isGood;
}

module.exports = {
  assessDataQuality,
  buildPartialSignature,
  isAcceptableCapture,
  isOfflineTemplateStable,
  persistentDeviceAnomalyState,
};
