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

function auditCollectedOutput(output) {
  const issues = [];
  for (const building of output.buildings || []) {
    for (const subArea of building.subAreas || []) {
      for (const pageRow of subArea.pages || []) {
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

module.exports = { auditCollectedOutput, pageFromData };
