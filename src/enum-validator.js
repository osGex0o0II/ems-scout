'use strict';

const crypto = require('crypto');
const { BLDG_META, BLDG_ORDER } = require('./rules');

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

function fmtRatio(value) {
  if (!Number.isFinite(value)) return '--';
  return value.toFixed(value >= 10 ? 1 : 2) + 'x';
}

function validateEnumData(data, options = {}) {
  const buildings = normalizeSelection(data, options.buildings || options.selectedBuildings);
  const errors = [];
  const warnings = [];
  const requested = options.buildings || options.selectedBuildings || [];
  const present = new Set(buildings.map(building => building.building));
  for (const building of requested) {
    if (!present.has(building)) errors.push(`${building}: 请求采集的楼栋未出现在结果中。`);
  }
  for (const building of buildings) {
    if (building.err) errors.push(`${building.building}: 楼栋采集失败: ${building.err}`);
    for (const subArea of building.subAreas || []) {
      if (subArea.err && subArea.err !== 'bm inline') {
        errors.push(`${building.building} F${subArea.floor ?? '-'} ${subArea.text || '-'}: 子区采集失败: ${subArea.err}`);
      }
      for (const page of subArea.pages || []) {
        if (page.err) {
          errors.push(`${building.building} F${subArea.floor ?? '-'} ${subArea.text || '-'} ${page.page || '-'}: 页面采集失败: ${page.err}`);
        }
        if (page.stale) {
          errors.push(`${building.building} F${subArea.floor ?? '-'} ${subArea.text || '-'} ${page.page || '-'}: stale 页面未确认更新。`);
        }
      }
    }
  }
  const stats = buildings.map(buildingStats).sort((a, b) => BLDG_ORDER.indexOf(a.building) - BLDG_ORDER.indexOf(b.building));

  if (!stats.length) {
    errors.push('采集结果为空，未找到可导入的楼栋数据。');
    return { ok: false, errors, warnings, stats };
  }

  for (const s of stats) {
    const meta = BLDG_META[s.building] || {};
    if (s.cards === 0) {
      errors.push(`${s.building}: 采集卡片数为 0。`);
      continue;
    }

    if (meta.baselineCards) {
      const ratio = s.cards / meta.baselineCards;
      if (s.cards > meta.baselineCards * 2.5 + 50) {
        errors.push(`${s.building}: 卡片数 ${s.cards} 明显高于基准 ${meta.baselineCards} (${fmtRatio(ratio)})。`);
      } else if (s.cards > meta.baselineCards * 1.25 + 50) {
        warnings.push(`${s.building}: 卡片数 ${s.cards} 高于基准 ${meta.baselineCards} (${fmtRatio(ratio)})。`);
      } else if (s.cards < meta.baselineCards * 0.35) {
        errors.push(`${s.building}: 卡片数 ${s.cards} 明显低于基准 ${meta.baselineCards} (${fmtRatio(ratio)})。`);
      } else if (s.cards < meta.baselineCards * 0.75) {
        warnings.push(`${s.building}: 卡片数 ${s.cards} 低于基准 ${meta.baselineCards} (${fmtRatio(ratio)})。`);
      }
    }

    if (meta.baselineSubAreas) {
      const ratio = s.subAreas / meta.baselineSubAreas;
      if (s.subAreas > meta.baselineSubAreas * 2 + 2) {
        errors.push(`${s.building}: 子区数 ${s.subAreas} 明显高于基准 ${meta.baselineSubAreas} (${fmtRatio(ratio)})。`);
      } else if (s.subAreas > meta.baselineSubAreas * 1.4 + 2) {
        warnings.push(`${s.building}: 子区数 ${s.subAreas} 高于基准 ${meta.baselineSubAreas} (${fmtRatio(ratio)})。`);
      } else if (s.subAreas < meta.baselineSubAreas * 0.45) {
        errors.push(`${s.building}: 子区数 ${s.subAreas} 明显低于基准 ${meta.baselineSubAreas} (${fmtRatio(ratio)})。`);
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
