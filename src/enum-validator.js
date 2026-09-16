'use strict';

const crypto = require('crypto');
const { BLDG_ORDER, assessBuildingIdentity, isAcceptedCaptureQualityReason } = require('./rules');

function cardCountForPage(page) {
  return Array.isArray(page && page.cards) ? page.cards.length : 0;
}

function flattenBuilding(building) {
  const pages = [];
  const cards = [];
  for (const sa of building.subAreas || []) {
    for (const p of sa.pages || []) {
      pages.push({ sa, page: p, count: cardCountForPage(p) });
      for (const c of p.cards || []) cards.push({ sa, page: p, card: c });
    }
  }
  return { pages, cards };
}

function hash(value) {
  return crypto.createHash('sha1').update(JSON.stringify(value)).digest('hex').slice(0, 16);
}

function buildingStats(building) {
  const flat = flattenBuilding(building);
  const subAreas = Array.isArray(building.subAreas) ? building.subAreas : [];
  const cardRows = flat.cards.map(r => r.card || {});
  const pageRows = flat.pages.map(r => r.page || {});
  const stat = {
    building: building.building,
    subAreas: subAreas.length,
    pages: flat.pages.length,
    cards: cardRows.length,
    on: cardRows.filter(c => c.switch === 'ON' || c.comm === '开机').length,
    off: cardRows.filter(c => c.switch === 'OFF' || c.comm === '关机').length,
    offline: cardRows.filter(c => c.comm === '离线').length,
    firstCard: cardRows[0] && cardRows[0].name ? cardRows[0].name : '',
    firstSubArea: subAreas[0] ? `${subAreas[0].floor}|${subAreas[0].text}|${subAreas[0].x}|${subAreas[0].y}` : '',
  };

  stat.signature = hash({
    subAreas: subAreas.map(sa => [sa.floor, sa.text, sa.x, sa.y]),
    pageCounts: pageRows.map(p => [p.page, cardCountForPage(p), p.layout || '']),
    cards: cardRows.map(c => [c.name || '', c.switch || '', c.comm || '']),
  });
  return stat;
}

function normalizeSelection(data, selectedBuildings) {
  const wanted = Array.isArray(selectedBuildings) && selectedBuildings.length
    ? new Set(selectedBuildings)
    : null;
  const buildings = Array.isArray(data && data.buildings) ? data.buildings : [];
  return wanted ? buildings.filter(b => wanted.has(b.building)) : buildings;
}

function isInlinePlaceholderSubArea(building, subArea) {
  return building === '6号' && Number(subArea && subArea.floor) === -2 &&
    String(subArea && subArea.text || '').trim() === 'BM';
}

function validateEnumData(data, options = {}) {
  const buildings = normalizeSelection(data, options.buildings || options.selectedBuildings);
  const errors = [];
  const warnings = [];
  const stats = buildings.map(buildingStats).sort((a, b) => BLDG_ORDER.indexOf(a.building) - BLDG_ORDER.indexOf(b.building));

  if (!stats.length) {
    errors.push('采集结果为空，未找到可导入的楼栋数据。');
    return { ok: false, errors, warnings, stats };
  }

  for (const s of stats) {
    const building = buildings.find(item => item.building === s.building);
    const flatCards = building ? flattenBuilding(building).cards.map(row => row.card || {}) : [];
    const identity = assessBuildingIdentity(s.building, flatCards, s.subAreas);
    if (!identity.ok) {
      errors.push(`${s.building}: 楼栋身份校验失败 (${identity.details})。`);
    }
    if (s.cards === 0) {
      errors.push(`${s.building}: 采集卡片数为 0。`);
      continue;
    }

    const emptySubAreas = (building.subAreas || []).filter(subArea => {
      if (isInlinePlaceholderSubArea(s.building, subArea)) return false;
      const pages = Array.isArray(subArea.pages) ? subArea.pages : [];
      const cards = pages.reduce((total, page) => total + cardCountForPage(page), 0);
      return pages.length === 0 || cards === 0;
    });
    for (const subArea of emptySubAreas) {
      errors.push(`${s.building}/${subArea.text || subArea.floor || '-'}: 存在空子区，未采集到任何页面或卡片。`);
    }

    const erroredSubAreas = (building.subAreas || []).filter(subArea =>
      !isInlinePlaceholderSubArea(s.building, subArea) && String(subArea.err || '').trim());
    for (const subArea of erroredSubAreas) {
      errors.push(`${s.building}/${subArea.text || subArea.floor || '-'}: 子区采集失败 (${subArea.err})。`);
    }

    const flat = building ? flattenBuilding(building) : { pages: [], cards: [] };
    if (options.requireDevId) {
      const missingDevIds = flat.cards.filter(row => !row.card || row.card.devId === undefined || row.card.devId === null || row.card.devId === '').length;
      const seenDevIds = new Set();
      let duplicateDevIds = 0;
      for (const row of flat.cards) {
        const id = row.card && row.card.devId;
        if (id === undefined || id === null || id === '') continue;
        const key = String(id);
        if (seenDevIds.has(key)) duplicateDevIds++;
        seenDevIds.add(key);
      }
      if (missingDevIds > 0) {
        errors.push(`${s.building}: ${missingDevIds} 张卡片缺少 devId，不能作为实时采集目标。`);
      }
      if (duplicateDevIds > 0) {
        errors.push(`${s.building}: 存在 ${duplicateDevIds} 个重复 devId，不能作为实时采集目标。`);
      }
    }
    for (const row of flat.pages) {
      const page = row.page || {};
      const cards = Array.isArray(page.cards) ? page.cards : [];
      const names = cards.map(card => String(card && card.name || '').trim()).filter(Boolean);
      if (new Set(names).size !== names.length) {
        errors.push(`${s.building}/${row.sa.text}/${page.page || 'default'}: 同页重名卡片尚未编号。`);
      }
      const sourceGroups = new Map();
      for (const card of cards) {
        const sourceName = String(card && card.sourceName || '').trim();
        if (!sourceName) continue;
        if (!sourceGroups.has(sourceName)) sourceGroups.set(sourceName, []);
        sourceGroups.get(sourceName).push(String(card.name || '').trim());
      }
      for (const [sourceName, labeledNames] of sourceGroups) {
        if (labeledNames.length < 2) continue;
        const expected = labeledNames.map((_, index) => `${sourceName}#${index + 1}`).sort();
        const actual = [...labeledNames].sort();
        if (actual.join('|') !== expected.join('|')) {
          errors.push(`${s.building}/${row.sa.text}/${page.page || 'default'}: 重名卡片 ${sourceName} 的编号不连续。`);
        }
      }
      const rejectedCount = Number(page.rejectedCount || 0);
      if (rejectedCount > 0) {
        warnings.push(`${s.building}/${row.sa.text}/${page.page || 'default'}: 已过滤 ${rejectedCount} 个无卡片结构的 SVG 名称候选。`);
      }

      const qualityReason = String(page.qualityReason || page.quality_reason || '').trim();
      if (qualityReason && !isAcceptedCaptureQualityReason(qualityReason)) {
        errors.push(`${s.building}/${row.sa.text}/${page.page || 'default'}: 质量原因“${qualityReason}”未达到可导入条件。`);
      }
      if (String(page.err || '').trim()) {
        errors.push(`${s.building}/${row.sa.text}/${page.page || 'default'}: 页面采集失败 (${page.err})。`);
      }
    }

  }

  const bySignature = new Map();
  for (const s of stats) {
    if (!s.cards) continue;
    if (!bySignature.has(s.signature)) bySignature.set(s.signature, []);
    bySignature.get(s.signature).push(s);
  }
  for (const group of bySignature.values()) {
    if (group.length >= 3 && group[0].cards >= 100) {
      errors.push(`疑似楼栋数据串页: ${group.map(g => g.building).join(', ')} 的采集签名完全相同 (${group[0].cards} 张，首卡 ${group[0].firstCard || '-'})。`);
    } else if (group.length === 2 && group[0].cards >= 100) {
      warnings.push(`疑似楼栋数据重复: ${group.map(g => g.building).join(', ')} 的采集签名相同。`);
    }
  }

  return { ok: errors.length === 0, errors, warnings, stats };
}

function formatValidation(result) {
  const lines = [];
  for (const s of result.stats || []) {
    lines.push(`${s.building}: ${s.subAreas} 子区, ${s.pages} 页, ${s.cards} 张, 首卡 ${s.firstCard || '-'}`);
  }
  for (const w of result.warnings || []) lines.push('WARN ' + w);
  for (const e of result.errors || []) lines.push('ERROR ' + e);
  return lines;
}

module.exports = {
  validateEnumData,
  formatValidation,
  buildingStats,
};
