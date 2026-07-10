'use strict';

const fs = require('fs');
const path = require('path');

function keyPart(value) {
  return String(value ?? '').trim().toUpperCase();
}

function floorKey(value) {
  const raw = keyPart(value);
  if (!raw) return '';
  return raw.endsWith('F') || raw === 'BM' ? raw : raw + 'F';
}

function realtimePageKey(row) {
  const floor = floorKey(row.floor_label || row.floor || row.subAreaText || row.sub_area);
  const subArea = keyPart(row.subAreaText || row.sub_area || row.sub_area_text || floor);
  const pageName = keyPart(row.pageName || row.page_name || 'default');
  return `${floor}|${subArea}|${pageName}`;
}

function realtimeExactKey(row, fallbackBuilding) {
  const building = keyPart(row.building || row.source_building || fallbackBuilding);
  const name = keyPart(row.name || row.source_name);
  if (!building || !name) return '';
  return `${building}|${realtimePageKey(row)}|${name}`;
}

function realtimeNameKey(row, fallbackBuilding) {
  const building = keyPart(row.building || row.source_building || fallbackBuilding);
  const name = keyPart(row.name || row.source_name);
  return building && name ? `${building}|${name}` : '';
}

function addRealtimeIndex(map, key, value) {
  if (!key) return;
  const existing = map.get(key);
  if (!existing) map.set(key, value);
  else if (Array.isArray(existing)) existing.push(value);
  else map.set(key, [existing, value]);
}

function createRealtimeService(options) {
  const ROOT = options.root;
  const OUT_DIR = options.outDir;
  const BLDG_ORDER = options.buildings;

  function isRealtimeDetailFileName(name, building) {
    const escaped = String(building).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return new RegExp(`^realtime_${escaped}_(?:batch_)?\\d{8}_\\d{6}\\.json$`).test(name);
  }

  function latestRealtimeFile(building) {
    const latest = path.join(OUT_DIR, `realtime_${building}_latest.json`);
    if (fs.existsSync(latest)) return latest;
    if (!fs.existsSync(OUT_DIR)) return '';
    return fs.readdirSync(OUT_DIR)
      .filter(name => isRealtimeDetailFileName(name, building))
      .map(name => {
        const full = path.join(OUT_DIR, name);
        return { full, mtime: fs.statSync(full).mtimeMs };
      })
      .sort((a, b) => b.mtime - a.mtime)[0]?.full || '';
  }

  function loadRealtimeDetails(buildings = BLDG_ORDER) {
    const files = [];
    const allRows = [];
    const byExactKey = new Map();
    const byNameKey = new Map();
    const normalized = Array.isArray(buildings) && buildings.length ? buildings : BLDG_ORDER;
    for (const building of normalized) {
      const file = latestRealtimeFile(building);
      if (!file) continue;
      let data = null;
      try {
        data = JSON.parse(fs.readFileSync(file, 'utf8'));
      } catch {
        continue;
      }
      const stat = fs.statSync(file);
      const fileRows = Array.isArray(data.rows) ? data.rows : [];
      files.push({
        building,
        path: path.relative(ROOT, file),
        mtime: stat.mtime.toISOString(),
        summary: data.summary || null,
        rows: fileRows.length,
      });
      for (const [index, row] of fileRows.entries()) {
        const exactKey = realtimeExactKey(row, building);
        const nameKey = realtimeNameKey(row, building);
        const detail = {
          row_id: `${path.relative(ROOT, file)}#${index}`,
          source_file: path.relative(ROOT, file),
          source_mtime: stat.mtime.toISOString(),
          source_building: row.building || building,
          source_floor: row.floor,
          source_sub_area: row.subAreaText || row.sub_area || '',
          source_page_name: row.pageName || row.page_name || '',
          source_tab: row.tab || '',
          source_name: row.name || '',
          source_x: row.x ?? row.cardX ?? row.source_x ?? null,
          source_y: row.y ?? row.cardY ?? row.source_y ?? null,
          exact_key: exactKey,
          name_key: nameKey,
          dev_id: row.devId || row.meterId || '',
          meter_id: row.meterId || row.devId || '',
          rtu_id: row.rtuId || '',
          field_count: row.fieldCount || Object.keys(row.fields || {}).length,
          realtime_tag_count: row.realtimeTagCount || 0,
          realtime_valid_tag_count: row.realtimeValidTagCount || 0,
          default_like: !!row.defaultLike,
          error: row.error || '',
          card_comm: row.cardComm || row.card_comm || '',
          card_switch: row.cardSwitch || row.card_switch || '',
          card_indicator: row.cardIndicator || row.card_indicator || '',
          card_switch_indicator: row.cardSwitchIndicator || row.card_switch_indicator || '',
          card_state_source: row.cardStateSource || row.card_state_source || '',
          fields: row.fields || {},
          raw_fields: row.rawFields || {},
          valid_fields: row.validFields || {},
        };
        allRows.push(detail);
        addRealtimeIndex(byExactKey, exactKey, detail);
        addRealtimeIndex(byNameKey, nameKey, detail);
      }
    }
    return { files, rows: allRows, byExactKey, byNameKey };
  }

  return {
    isRealtimeDetailFileName,
    latestRealtimeFile,
    loadRealtimeDetails,
  };
}

module.exports = {
  createRealtimeService,
};
