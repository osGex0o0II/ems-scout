'use strict';

const {
  checkCardQuality,
  classifyPersistentDeviceAnomalyPage,
  normalizeKnownSourceDefects,
  classifyKnownMissingIndicatorPage,
  classifyStableOfflineTemplatePage,
} = require('./rules');

function pageFromData(pageName, data, extra = {}) {
  const normalizedCards = normalizeKnownSourceDefects(data.cards || []);
  const normalizedData = { ...data, cards: normalizedCards };
  const duplicateNames = Array.isArray(data.duplicateNames) ? data.duplicateNames : [];
  const qc = checkCardQuality(normalizedCards, normalizedData);
  const knownMissingIndicator = classifyKnownMissingIndicatorPage(normalizedCards, normalizedData);
  const qualityReason = knownMissingIndicator.eligible
    ? 'known_source_indicator_missing'
    : (extra.qualityReason || data.qualityReason || (qc.ok ? 'quality_pass' : ''));
  return {
    page: pageName,
    count: data.count,
    rawCount: data.rawCount ?? data.count,
    uniqueCount: data.uniqueCount ?? data.count,
    duplicateNames,
    onHref: data.onHref,
    offHref: data.offHref,
    layout: data.layout,
    qualityReason,
    cards: normalizedCards,
    ...extra,
  };
}

function summarizeCardStates(cards = []) {
  const counts = { 开机: 0, 关机: 0, 离线: 0, 未知: 0 };
  const cardsByState = { 开机: [], 关机: [], 离线: [], 未知: [] };
  for (const card of cards) {
    const state = ['开机', '关机', '离线'].includes(card && card.comm) ? card.comm : '未知';
    counts[state]++;
    cardsByState[state].push(card);
  }
  return { counts, cardsByState };
}

function auditCollectedOutput(output) {
  const issues = [];
  for (const building of output.buildings || []) {
    if (building.err) {
      issues.push({ building: building.building, reason: 'building_error', details: String(building.err) });
    }
    for (const subArea of building.subAreas || []) {
      if (subArea.err && subArea.err !== 'bm inline') {
        issues.push({
          building: building.building,
          floor: subArea.floor,
          subArea: subArea.text,
          reason: 'subarea_error',
          details: String(subArea.err),
        });
      }
      for (const pageRow of subArea.pages || []) {
        if (pageRow.err) {
          issues.push({
            building: building.building,
            floor: subArea.floor,
            subArea: subArea.text,
            page: pageRow.page,
            reason: 'page_error',
            details: String(pageRow.err),
          });
        }
        if (pageRow.stale) {
          issues.push({
            building: building.building,
            floor: subArea.floor,
            subArea: subArea.text,
            page: pageRow.page,
            reason: 'stale_page',
            details: '页面内容与前一页相同，未确认翻页完成。',
          });
        }
        const cards = Array.isArray(pageRow.cards) ? pageRow.cards : [];
        const qc = checkCardQuality(cards, pageRow);
        const reason = pageRow.qualityReason || pageRow.quality_reason || '';
        const allowedNonPass = reason === 'device_anomalies_preserved'
          ? classifyPersistentDeviceAnomalyPage(cards, pageRow).eligible
          : reason === 'known_source_indicator_missing'
            ? classifyKnownMissingIndicatorPage(cards, pageRow).eligible
            : reason === 'offline_template_stable'
              ? classifyStableOfflineTemplatePage(cards, pageRow).eligible
              : false;
        if (!qc.ok && !allowedNonPass) {
          issues.push({
            building: building.building,
            floor: subArea.floor,
            subArea: subArea.text,
            page: pageRow.page,
            reason: reason || 'missing_quality_reason',
            details: qc.details,
          });
        }
      }
    }
  }
  return issues;
}

module.exports = { auditCollectedOutput, pageFromData, summarizeCardStates };
