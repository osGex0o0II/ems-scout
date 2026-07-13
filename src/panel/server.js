#!/usr/bin/env node
'use strict';

const fs = require('fs');
const http = require('http');
const path = require('path');
const { spawn, execFile } = require('child_process');
const { BLDG_ORDER, BLDG_META, isPublic } = require('../rules');
const { DB_PATH, openDb, openReadonlyDb, databaseExists } = require('./repositories/panel-db');
const { createSummaryService } = require('./services/summary-service');
const { createQualityService } = require('./services/quality-service');
const { createRealtimeService } = require('./services/realtime-service');
const { createCardsService } = require('./services/cards-service');
const { createTaskService } = require('./services/task-service');
const { createReconcileService } = require('./services/reconcile-service');
const { createHealthRoutes } = require('./routes/health-routes');
const { createSummaryRoutes } = require('./routes/summary-routes');
const { createQualityRoutes } = require('./routes/quality-routes');
const { createCardsRoutes } = require('./routes/cards-routes');
const { createRunsRoutes } = require('./routes/runs-routes');
const { createReportsRoutes } = require('./routes/reports-routes');
const { createAboutRoutes } = require('./routes/about-routes');
const { createRealtimeRoutes } = require('./routes/realtime-routes');
const { createTasksRoutes } = require('./routes/tasks-routes');
const { createFloorsRoutes } = require('./routes/floors-routes');
const { createAreasRoutes } = require('./routes/areas-routes');
const { createMonitorRoutes } = require('./routes/monitor-routes');
const { createDeviceRoutes } = require('./routes/device-routes');
const { createRealtimeMatchRoutes } = require('./routes/realtime-match-routes');
const { createReconcileRoutes } = require('./routes/reconcile-routes');
const {
  HttpRequestError,
  authorizeApiRequest,
  createSessionToken,
  isLoopbackHost,
  readJsonBody,
  resolveStaticPath,
} = require('./http-security');
const realtimeBrowser = require('../../scripts/realtime-browser');
const {
  REALTIME_NORMAL_VALUES,
  realtimeFieldValue,
  rowStateCompareMeta,
  isRealtimePointsComplete,
  isRealtimeFieldAbnormal,
  isRealtimeDetailInvalid,
  realtimeFieldIssueCount,
  numericField,
  isTempAbnormal,
  isSetTempAbnormal,
  isTempMissing,
  isIgnoredRealtimeRow,
  isManagedRealtimeRow,
  isUnresolvedRealtimeRow,
  isRealtimeDbUnmatchedRow,
  isOfflineOrCommAbnormal,
  commState,
  commStateValue,
  isCommUnknown,
  hasReviewIssue,
  isCleanDeviceRow,
  hasDbRealtimeMismatch,
  stateCompareSummary,
  matchesDbStatus,
  matchesIssueFilter,
  matchesTempState,
  cardFacetPower,
  computeCardFacets,
} = require('./rules/device-health-rules');
const {
  ensureMonitorSchema,
  loadMonitors,
  loadMonitorGroups,
  saveMonitorGroup,
  deleteMonitorGroup,
  loadMonitorGroupItems,
  saveMonitorGroupItem,
  deleteMonitorGroupItem,
  groupFilterClause,
  computeMonitorGroupStats,
  saveMonitor,
  deleteMonitor,
  computeMonitorStatuses,
  compareMonitorStatuses,
  refreshMonitorSnapshots,
  loadMonitorEvents,
  loadAvailableFloors,
} = require('./monitor');
const {
  ensureHistorySchema,
  parseFloorValue,
  normalizeFloorLabel,
  floorLabelFromValue,
  resolveRunId,
  sourceForRun,
  listRuns,
  setRunAnomaly,
  restoreCurrentFromRun,
  deleteRun,
  loadFloorCatalog,
  saveFloorCatalog,
  seedCurrentRun,
} = require('./history');

const ROOT = path.join(__dirname, '..', '..');
const WEB_ROOT = path.join(ROOT, 'web', 'panel');
const OUT_DIR = path.join(ROOT, 'out');
const ENUM_JSON_PATH = path.join(OUT_DIR, 'enum_full_v5.json');
const PORT = Number(process.env.EMS_PANEL_PORT || (process.argv.find(a => a.startsWith('--port=')) || '').split('=')[1]) || 17777;
const REALTIME_FIELDS = [
  '温控器程序版本号',
  '当前开关机状态',
  '高风速阀门开关状态',
  '中风速阀门开关状态',
  '低风速阀门开关状态',
  '室内温度',
  '设定温度',
  '设定风速',
  '系统模式设置',
  '达温风机状态',
  '设定温度上限',
  '设定温度下限',
  '集控锁定',
  '系统类型',
  '制冷/通风温度补偿',
  '制热/地暖温度补偿',
  '待机显示温度',
  '温度回差',
  '防冷风时间',
  '防冻设置温度',
  '防冻保护是否开启',
  '室温显示精度',
  '温度单位选择',
  '掉电记忆',
  '通讯地址 (Modbus)',
  '恢复出厂设置',
];
const REALTIME_QUERY_FIELDS = {
  realtime_power: '当前开关机状态',
  realtime_fan_setting: '设定风速',
  realtime_mode_setting: '系统模式设置',
  realtime_lock: '集控锁定',
  realtime_system_type: '系统类型',
  realtime_modbus: '通讯地址 (Modbus)',
};
const ISSUE_FILTERS = [
  { value: 'exclude_abnormal', label: '排除异常/离线' },
  { value: 'needs_review', label: '需排查' },
  { value: 'offline', label: '离线/通讯异常' },
  { value: 'comm_unknown', label: '通讯未知' },
  { value: 'unmatched', label: '需人工分类' },
  { value: 'field_invalid', label: '字段异常' },
  { value: 'points_incomplete', label: '点位不完整' },
  { value: 'detail_error', label: '详情错误' },
  { value: 'default_like', label: '默认值疑似' },
  { value: 'temp_abnormal', label: '温度异常' },
];
const TEMP_STATE_FILTERS = [
  { value: 'abnormal', label: '温度异常' },
  { value: 'indoor_high', label: '室温偏高' },
  { value: 'indoor_low', label: '室温偏低' },
  { value: 'set_temp_abnormal', label: '设定异常' },
];
const CARD_SHORTCUT_FILTERS = [
  { key: 'all', apply: {} },
  { key: 'normal', apply: { issue: 'exclude_abnormal' } },
  { key: 'needs_review', apply: { issue: 'needs_review' } },
  { key: 'public_on', apply: { area: 'public', realtime_power: '开机' } },
  { key: 'private_on', apply: { area: 'private', realtime_power: '开机' } },
  { key: 'public_review', apply: { area: 'public', issue: 'needs_review' } },
  { key: 'private_offline', apply: { area: 'private', issue: 'offline' } },
  { key: 'temp_abnormal', apply: { temp_state: 'abnormal' } },
  { key: 'points_incomplete', apply: { issue: 'points_incomplete' } },
  { key: 'locked', apply: { realtime_lock: '开启' } },
];
const MATCH_OVERRIDE_ACTIONS = new Set(['classify_only', 'map_to_db', 'create_virtual', 'ignore_duplicate']);
const AREA_TYPE_OVERRIDES = new Set(['', '公区', '非公区', '未匹配']);
const ZUO_BY_BUILDING = {
  '5号': ['A座', 'B座', 'C座', 'D座', 'E座', 'F座'],
  '6号': ['A座', 'B座', 'C座'],
};
const ZUO_SOURCE_LABELS = {
  db: '入库坐标',
  manual: '人工设置',
  coordinate: '实时坐标',
  name: '同名共识',
  unknown: '未识别',
};

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
};

let currentTask = null;
const taskHistory = [];
const qualityService = createQualityService({
  root: ROOT,
  outDir: OUT_DIR,
  dbPath: DB_PATH,
  openReadonlyDb,
  resolveRunId,
});
const { loadQualityReport } = qualityService;
const realtimeService = createRealtimeService({
  root: ROOT,
  outDir: OUT_DIR,
  buildings: BLDG_ORDER,
});
const { loadRealtimeDetails } = realtimeService;
const reconcileService = createReconcileService({
  root: ROOT,
  outDir: OUT_DIR,
  openReadonlyDb,
  realtimeService,
});
const summaryService = createSummaryService({
  BLDG_ORDER,
  BLDG_META,
  openReadonlyDb,
  resolveRunId,
  loadCards,
  loadQualityReport,
  realtimeCoverage,
  realtimeFieldValue,
  commStateValue,
  isManagedRealtimeRow,
  isUnresolvedRealtimeRow,
  isRealtimeDbUnmatchedRow,
});
const { loadSummary } = summaryService;
const cardsService = createCardsService({
  loadCards,
  loadFilterOptions,
});
const taskService = createTaskService({
  root: ROOT,
  outDir: OUT_DIR,
  buildings: BLDG_ORDER,
  realtimeService,
  qualityService,
  realtimeBrowser,
  loadSummary,
  latestCollectionMeta,
  realtimeSummary,
});
const apiRouteHandlers = [
  createHealthRoutes({ json, root: ROOT, dbPath: DB_PATH, databaseExists }),
  createSummaryRoutes({ json, loadSummary }),
  createQualityRoutes({ json, loadQualityReport }),
  createCardsRoutes({ json, cardsService }),
  createRunsRoutes({
    json,
    fail,
    readBody,
    openDb,
    decorateRuns,
    listRuns,
    latestCollectionMeta,
    realtimeSummary,
    setRunAnomaly,
    restoreCurrentFromRun,
    deleteRun,
  }),
  createReportsRoutes({ json, readLatestLog }),
  createAboutRoutes({ json, loadAboutInfo }),
  createRealtimeRoutes({ json, realtimeSummary, realtimeCoverage }),
  createReconcileRoutes({ json, fail, readBody, reconcileService }),
  createFloorsRoutes({
    json,
    readBody,
    openDb,
    loadFloorCatalog,
    loadAvailableFloors,
    saveFloorCatalog,
  }),
  createAreasRoutes({
    json,
    fail,
    readBody,
    openDb,
    loadAreaOptions,
    loadMonitorGroups,
    saveMonitorGroup,
    deleteMonitorGroup,
    loadMonitorGroupItems,
    saveMonitorGroupItem,
    deleteMonitorGroupItem,
    computeMonitorGroupStats,
  }),
  createMonitorRoutes({
    json,
    readBody,
    openDb,
    ensureMonitorSchema,
    loadMonitors,
    saveMonitor,
    deleteMonitor,
    computeMonitorStatuses,
    compareMonitorStatuses,
    refreshMonitorSnapshots,
    loadMonitorEvents,
  }),
  createDeviceRoutes({ json, readBody, saveNote, saveTag, deleteTag }),
  createRealtimeMatchRoutes({ json, readBody, saveRealtimeMatchOverride }),
  createTasksRoutes({
    json,
    readBody,
    getCurrentTask: () => currentTask,
    getTaskHistory: () => taskHistory,
    publicTask,
    startTask,
    stopTask,
    buildPreflight: taskService.buildPreflight,
  }),
];

function json(res, status, body) {
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    'content-security-policy': "default-src 'none'; frame-ancestors 'none'; base-uri 'none'",
    'x-content-type-options': 'nosniff',
    'x-frame-options': 'DENY',
  });
  res.end(JSON.stringify(body));
}

function listParam(value) {
  if (Array.isArray(value)) return value.flatMap(v => listParam(v));
  return String(value ?? '')
    .split(',')
    .map(v => v.trim())
    .filter(Boolean);
}

function uniqueListParam(value) {
  return [...new Set(listParam(value))];
}

function placeholders(values) {
  return values.map(() => '?').join(',');
}

function fail(res, status, error) {
  json(res, status, { ok: false, error: error && error.message ? error.message : String(error) });
}

function readBody(req) {
  return readJsonBody(req);
}

function clamp(n, min, max) {
  const value = Number(n);
  if (!Number.isFinite(value)) return min;
  return Math.max(min, Math.min(max, value));
}

function countEnumCards(data) {
  return (data.buildings || []).reduce((sum, b) => sum + (b.subAreas || []).reduce((saSum, sa) => {
    return saSum + (sa.pages || []).reduce((pageSum, p) => pageSum + ((p.cards || []).length), 0);
  }, 0), 0);
}

function latestImportedRunMeta() {
  if (!databaseExists()) return null;
  const db = openReadonlyDb();
  try {
    return db.prepare(`
      SELECT id, completed_at, card_count, is_anomaly
      FROM collection_runs
      ORDER BY datetime(completed_at) DESC, id DESC
      LIMIT 1
    `).get() || null;
  } catch {
    return null;
  } finally {
    db.close();
  }
}

function expectedCardCountForBuildings(buildings = BLDG_ORDER) {
  const selected = Array.isArray(buildings) && buildings.length ? buildings : BLDG_ORDER;
  return selected.reduce((sum, b) => sum + Number(BLDG_META[b] && BLDG_META[b].baselineCards || 0), 0);
}

function decorateRuns(runs) {
  return (runs || []).map(r => {
    const expected = expectedCardCountForBuildings(r.buildings);
    const delta = Number(r.card_count || 0) - expected;
    const ratio = expected ? Math.abs(delta) / expected : 0;
    const autoAnomaly = r.scope === 'full' && (r.buildings || []).length >= BLDG_ORDER.length && ratio > 0.02;
    let qualitySummary = null;
    try {
      qualitySummary = r.quality_summary && r.quality_summary !== '{}'
        ? JSON.parse(r.quality_summary)
        : null;
    } catch {
      qualitySummary = null;
    }
    return {
      ...r,
      quality_summary: qualitySummary,
      quality_issue_count: qualitySummary && qualitySummary.summary
        ? Number(qualitySummary.summary.issue_count || 0)
        : null,
      expected_card_count: expected,
      card_delta: delta,
      anomaly_reason: Number(r.is_anomaly || 0)
        ? '已标记异常'
        : autoAnomaly
          ? `卡片数偏离基线 ${delta > 0 ? '+' : ''}${delta}`
          : '',
      suggested_anomaly: autoAnomaly ? 1 : 0,
    };
  });
}

function latestCollectionMeta() {
  if (!fs.existsSync(ENUM_JSON_PATH)) return null;
  const stat = fs.statSync(ENUM_JSON_PATH);
  const meta = {
    path: path.relative(ROOT, ENUM_JSON_PATH),
    completed_at: stat.mtime.toISOString(),
    mtime: stat.mtime.toISOString(),
    card_count: null,
    building_count: null,
    pending_import: false,
    parse_error: false,
  };

  try {
    const data = JSON.parse(fs.readFileSync(ENUM_JSON_PATH, 'utf8'));
    meta.completed_at = data.completedAt || meta.completed_at;
    meta.card_count = countEnumCards(data);
    meta.building_count = Array.isArray(data.buildings) ? data.buildings.length : null;
  } catch {
    meta.parse_error = true;
  }

  const imported = latestImportedRunMeta();
  meta.latest_imported_at = imported ? imported.completed_at : null;
  if (meta.completed_at) {
    const collectedAt = new Date(meta.completed_at).getTime();
    const importedAt = imported && imported.completed_at ? new Date(imported.completed_at).getTime() : 0;
    meta.pending_import = Number.isFinite(collectedAt) && (!importedAt || collectedAt > importedAt + 1000);
  }
  return meta;
}

function ensurePanelSchemaIfPossible() {
  if (!fs.existsSync(DB_PATH)) return;
  const db = openDb();
  try {
    ensureMonitorSchema(db);
    ensureHistorySchema(db);
    seedCurrentRun(db);
  }
  finally { db.close(); }
}

function buildingZuo(building, x) {
  const meta = BLDG_META[building];
  const fn = meta && (meta.zuoFn || meta.getZuo);
  if (!fn || x === null || x === undefined) return '';
  const n = Number(x);
  return Number.isFinite(n) ? fn(n) : '';
}

function normalizeZuoValue(value, building = '') {
  const raw = String(value || '').trim().toUpperCase();
  if (!raw) return '';
  const normalized = raw.match(/^[A-Z]$/) ? `${raw}座` : raw.replace(/^([A-Z])$/i, '$1座');
  const allowed = ZUO_BY_BUILDING[building] || [];
  if (allowed.length && !allowed.includes(normalized)) {
    throw new Error(`${building} 不支持座号：${value}`);
  }
  return normalized;
}

function normalizeAreaTypeOverride(value) {
  const raw = String(value || '').trim();
  if (!AREA_TYPE_OVERRIDES.has(raw)) throw new Error('区域只能设置为公区、非公区或未匹配');
  return raw;
}

function realtimeOverrideDevKey(building, devId) {
  const dev = keyPart(devId);
  return dev ? `${keyPart(building)}::${dev}` : '';
}

function realtimeOverrideIdentityKey({ building, floor_label, sub_area, page_name, name }) {
  return [
    keyPart(building),
    normalizeFloorLabel(floor_label),
    keyPart(sub_area),
    keyPart(page_name),
    keyPart(name),
  ].join('::');
}

function detailIdentityParts(detail) {
  const building = keyPart(detail.source_building);
  const floorLabel = displayFloorLabelFromSubArea(detail.source_sub_area || '', detail.source_floor);
  return {
    building,
    floor_label: floorLabel,
    sub_area: detail.source_sub_area || '',
    page_name: detail.source_page_name || detail.source_tab || 'default',
    name: keyPart(detail.source_name),
  };
}

function loadRealtimeMatchOverrides() {
  const byDev = new Map();
  const byIdentity = new Map();
  if (!fs.existsSync(DB_PATH)) return { byDev, byIdentity };
  const db = openReadonlyDb();
  try {
    let rows = [];
    try {
      rows = db.prepare('SELECT * FROM realtime_match_overrides ORDER BY id').all();
    } catch {
      rows = [];
    }
    for (const row of rows) {
      const devKey = realtimeOverrideDevKey(row.building, row.dev_id);
      if (devKey) {
        byDev.set(devKey, row);
      } else {
        const identityKey = realtimeOverrideIdentityKey({
          building: row.building,
          floor_label: row.floor_label,
          sub_area: row.sub_area,
          page_name: row.page_name,
          name: row.realtime_name,
        });
        byIdentity.set(identityKey, row);
      }
    }
  } finally {
    db.close();
  }
  return { byDev, byIdentity };
}

function findRealtimeMatchOverride(detail, maps) {
  if (!maps) return null;
  const parts = detailIdentityParts(detail);
  const devKey = realtimeOverrideDevKey(parts.building, detail.dev_id);
  if (devKey && maps.byDev.has(devKey)) return maps.byDev.get(devKey);
  return maps.byIdentity.get(realtimeOverrideIdentityKey(parts)) || null;
}

function addZuoConsensus(map, key, zuo) {
  if (!key || !zuo) return;
  const set = map.get(key) || new Set();
  set.add(zuo);
  map.set(key, set);
}

function buildZuoInferenceIndexes(dbRows) {
  const byFloorName = new Map();
  const byName = new Map();
  for (const row of dbRows || []) {
    if (!row || !row.zuo) continue;
    addZuoConsensus(byFloorName, realtimeOverrideIdentityKey({
      building: row.building,
      floor_label: row.floor_label,
      sub_area: '',
      page_name: '',
      name: row.name,
    }), row.zuo);
    addZuoConsensus(byName, `${keyPart(row.building)}::${keyPart(row.name)}`, row.zuo);
  }
  return { byFloorName, byName };
}

function uniqueConsensusValue(map, key) {
  const set = map.get(key);
  return set && set.size === 1 ? Array.from(set)[0] : '';
}

function inferZuoForDetail(detail, indexes) {
  const parts = detailIdentityParts(detail);
  const coordinateZuo = buildingZuo(parts.building, detail.source_x);
  if (coordinateZuo) return { value: coordinateZuo, source: 'coordinate' };
  const floorNameZuo = uniqueConsensusValue(indexes.byFloorName, realtimeOverrideIdentityKey({
    building: parts.building,
    floor_label: parts.floor_label,
    sub_area: '',
    page_name: '',
    name: parts.name,
  }));
  if (floorNameZuo) return { value: floorNameZuo, source: 'name' };
  const nameZuo = uniqueConsensusValue(indexes.byName, `${keyPart(parts.building)}::${keyPart(parts.name)}`);
  if (nameZuo) return { value: nameZuo, source: 'name' };
  return { value: '', source: 'unknown' };
}

function zuoInfoForDevice(building, dbRow, detail, override, indexes) {
  if (override && override.zuo_override) {
    return { value: normalizeZuoValue(override.zuo_override, building), source: 'manual' };
  }
  if (dbRow && dbRow.zuo) return { value: dbRow.zuo, source: 'db' };
  return inferZuoForDetail(detail, indexes);
}

function areaTypeInfoForDevice(detail, dbRow, override) {
  const overrideValue = override ? normalizeAreaTypeOverride(override.area_type_override || '') : '';
  if (overrideValue && overrideValue !== '未匹配') return { value: overrideValue, source: 'manual' };
  if (dbRow) return { value: dbRow.area_type, source: 'db' };
  if (detail && detail.source_name) {
    return { value: isPublic(detail.source_name, detail.source_page_name || detail.source_tab || '') ? '公区' : '非公区', source: 'rule' };
  }
  return { value: overrideValue || '未匹配', source: overrideValue ? 'manual' : 'unknown' };
}

function keyPart(value) {
  return String(value ?? '').trim();
}

function floorKey(value) {
  if (value === null || value === undefined || value === '') return '';
  const n = Number(value);
  if (!Number.isFinite(n)) return keyPart(value);
  return String(Number.isInteger(n) ? n : Number(n.toFixed(3)));
}

function realtimePageKey(row) {
  const tab = keyPart(row.tab);
  const page = keyPart(row.pageName ?? row.page_name);
  return tab && page ? `${tab}/${page}` : page;
}

function realtimeExactKey(row, fallbackBuilding) {
  const building = keyPart(row.building || fallbackBuilding);
  const name = keyPart(row.name);
  if (!building || !name) return '';
  return [
    building,
    floorKey(row.floor),
    keyPart(row.subAreaText ?? row.sub_area),
    realtimePageKey(row),
    name,
  ].join('::');
}

function realtimeNameKey(row, fallbackBuilding) {
  const building = keyPart(row.building || fallbackBuilding);
  const name = keyPart(row.name);
  return building && name ? `${building}::${name}` : '';
}

function addRealtimeIndex(map, key, value) {
  if (!key) return;
  const existing = map.get(key);
  if (!existing) map.set(key, value);
  else if (Array.isArray(existing)) existing.push(value);
  else map.set(key, [existing, value]);
}

function takeRealtimeIndex(map, key, usage) {
  if (!key) return null;
  const value = map.get(key);
  if (!value) return null;
  if (!Array.isArray(value)) return value;
  const idx = usage.get(key) || 0;
  usage.set(key, idx + 1);
  return value[idx] || null;
}

function uniqueRealtimeIndex(map, key) {
  if (!key) return null;
  const value = map.get(key);
  if (!value || Array.isArray(value)) return null;
  return value;
}

function attachRealtimeRows(rows, enabled) {
  if (!enabled) return rows;
  const buildings = [...new Set(rows.map(r => r.building).filter(Boolean))];
  const details = loadRealtimeDetails(buildings);
  const exactUsage = new Map();
  return rows.map(r => ({
    ...r,
    realtime: takeRealtimeIndex(details.byExactKey, realtimeExactKey({
      building: r.building,
      floor: r.floor,
      subAreaText: r.sub_area,
      pageName: r.page_name,
      name: r.name,
    }), exactUsage) || uniqueRealtimeIndex(details.byNameKey, realtimeNameKey(r)) || null,
  }));
}

function hasRealtimeFilters(query) {
  return !!(
    query.realtime_match ||
    query.realtime_points ||
    Object.keys(REALTIME_QUERY_FIELDS).some(key => query[key])
  );
}

function applyRealtimeFilters(rows, query) {
  if (!hasRealtimeFilters(query)) return rows;
  let filtered = attachRealtimeRows(rows, true);
  for (const [queryKey, fieldName] of Object.entries(REALTIME_QUERY_FIELDS)) {
    if (!query[queryKey]) continue;
    const expected = String(query[queryKey]).trim();
    if (expected === '__abnormal') {
      filtered = filtered.filter(r => isRealtimeFieldAbnormal(fieldName, realtimeFieldValue(r, fieldName)));
    } else {
      filtered = filtered.filter(r => realtimeFieldValue(r, fieldName) === expected);
    }
  }
  if (query.realtime_match === 'matched') {
    filtered = filtered.filter(r => !!r.realtime);
  }
  if (query.realtime_match === 'missing') {
    filtered = filtered.filter(r => !r.realtime);
  }
  if (query.realtime_match === 'invalid') {
    filtered = filtered.filter(r => isRealtimeDetailInvalid(r.realtime));
  }
  if (query.realtime_points === 'complete') {
    filtered = filtered.filter(r => isRealtimePointsComplete(r.realtime));
  }
  if (query.realtime_points === 'incomplete') {
    filtered = filtered.filter(r => !isRealtimePointsComplete(r.realtime));
  }
  if (query.realtime_points === 'missing') {
    filtered = filtered.filter(r => !r.realtime);
  }
  return filtered;
}

function loadDbCards(query) {
  const db = openReadonlyDb();
  try {
    const runId = resolveRunId(db, query.run_id || query.runId);
    const source = sourceForRun(runId);
    const dbSourceRow = runId
      ? db.prepare('SELECT completed_at FROM collection_runs WHERE id = ?').get(runId)
      : db.prepare('SELECT completed_at FROM collection_runs ORDER BY datetime(completed_at) DESC, id DESC LIMIT 1').get();
    const dbSourceMtime = dbSourceRow ? dbSourceRow.completed_at || '' : '';
    const params = [];
    const where = [];
    if (source.runWhere) {
      where.push(source.runWhere);
      params.push(...source.runParams);
    }
    const buildingValues = uniqueListParam(query.building);
    const floorLabels = uniqueListParam(query.floor).map(normalizeFloorLabel).filter(Boolean);
    if (buildingValues.length) {
      where.push(`sa.building IN (${placeholders(buildingValues)})`);
      params.push(...buildingValues);
    }
    if (query.status === 'on') where.push("(c.comm = '开机' OR c.switch = 'ON')");
    if (query.status === 'off') where.push("(c.comm = '关机' OR c.switch = 'OFF')");
    if (query.status === 'offline') where.push("c.comm = '离线'");
    if (query.status === 'unknown') where.push("(COALESCE(c.comm, '') = '' OR COALESCE(c.switch, '') NOT IN ('ON', 'OFF', '-'))");
    if (query.mode) {
      if (query.mode === '__unknown') where.push("(COALESCE(c.mode, '') = '' OR c.mode = '-')");
      else {
        where.push('c.mode = ?');
        params.push(query.mode);
      }
    }
    if (query.fan) {
      if (query.fan === '__unknown') where.push("(COALESCE(c.fan, '') = '' OR c.fan = '-')");
      else {
        where.push('c.fan = ?');
        params.push(query.fan);
      }
    }
    if (query.tag) {
      where.push(`EXISTS (
        SELECT 1
        FROM device_tags dt_filter
        WHERE dt_filter.card_name = c.name
          AND IFNULL(dt_filter.building, '') = IFNULL(sa.building, '')
          AND dt_filter.tag = ?
      )`);
      params.push(query.tag);
    }
    if (query.groups) {
      const gf = groupFilterClause(query.groups);
      if (gf.clause) {
        where.push(gf.clause);
        params.push(...gf.params);
      }
    }
    const sql = `
      SELECT c.id, sa.building, sa.floor, sa.text AS sub_area, sa.x, sa.y,
             p.page_name, p.layout, c.name, c.switch, c.mode, c.indoor,
             c.set_temp, c.fan, c.indicator, c.comm,
             dn.note,
             GROUP_CONCAT(dt.tag, ',') AS tags
      FROM ${source.subAreas} sa
      JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
      JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
      LEFT JOIN device_notes dn ON dn.card_name = c.name AND IFNULL(dn.building, '') = IFNULL(sa.building, '')
      LEFT JOIN device_tags dt ON dt.card_name = c.name AND IFNULL(dt.building, '') = IFNULL(sa.building, '')
      ${where.length ? 'WHERE ' + where.join(' AND ') : ''}
      GROUP BY c.id
      ORDER BY sa.building, sa.floor, sa.y, sa.x, p.id, c.name
      LIMIT 20000
    `;
    let rows = db.prepare(sql).all(...params).map(r => ({
      ...r,
      floor_label: displayFloorLabelFromSubArea(r.sub_area, r.floor),
      zuo: buildingZuo(r.building, r.x),
      area_type: isPublic(r.name, r.layout) ? '公区' : '非公区',
      tags: r.tags ? r.tags.split(',').filter(Boolean) : [],
    }));

    const zuoValues = uniqueListParam(query.zuo);
    if (floorLabels.length) {
      const allowedFloors = new Set(floorLabels);
      rows = rows.filter(r => allowedFloors.has(r.floor_label));
    }
    if (zuoValues.length) {
      const allowedZuos = new Set(zuoValues);
      rows = rows.filter(r => allowedZuos.has(r.zuo));
    }
    if (query.q) {
      const q = String(query.q).trim().toLowerCase();
      rows = rows.filter(r => {
        if (!q) return true;
        return [r.name, r.floor_label, r.sub_area, r.page_name, r.mode, r.zuo]
          .some(v => String(v || '').toLowerCase().includes(q));
      });
    }
    if (query.area === 'public') rows = rows.filter(r => r.area_type === '公区');
    if (query.area === 'private') rows = rows.filter(r => r.area_type === '非公区');

    const realtimeFilterActive = hasRealtimeFilters(query);
    rows = applyRealtimeFilters(rows, query);
    if (!realtimeFilterActive) {
      rows = attachRealtimeRows(rows, query.include_realtime === '1' || query.includeRealtime === '1');
    } else if (!(query.include_realtime === '1' || query.includeRealtime === '1')) {
      rows = rows.map(({ realtime, ...row }) => row);
    }
    rows = rows.map(r => ({
      ...r,
      ...rowStateCompareMeta(r, dbSourceMtime),
    }));
    const offset = clamp(query.offset, 0, rows.length);
    const maxLimit = Number.isFinite(Number(query._maxLimit)) ? clamp(query._maxLimit, 1, 20000) : 1000;
    const limit = clamp(query.limit, 1, maxLimit);
    return { run_id: runId, total: rows.length, rows: rows.slice(offset, offset + limit) };
  } finally {
    db.close();
  }
}

function annotationKey(building, name) {
  return `${keyPart(building)}::${keyPart(name)}`;
}

function loadAnnotationMaps() {
  const notes = new Map();
  const tags = new Map();
  if (!fs.existsSync(DB_PATH)) return { notes, tags };
  const db = openReadonlyDb();
  try {
    try {
      for (const r of db.prepare('SELECT building, card_name, note FROM device_notes').all()) {
        notes.set(annotationKey(r.building, r.card_name), r.note || '');
      }
    } catch {}
    try {
      for (const r of db.prepare('SELECT building, card_name, tag FROM device_tags ORDER BY tag').all()) {
        const key = annotationKey(r.building, r.card_name);
        const list = tags.get(key) || [];
        if (r.tag && !list.includes(r.tag)) list.push(r.tag);
        tags.set(key, list);
      }
    } catch {}
  } finally {
    db.close();
  }
  return { notes, tags };
}

function dbExactKey(row) {
  return realtimeExactKey({
    building: row.building,
    floor: row.floor,
    subAreaText: row.sub_area,
    pageName: row.page_name,
    name: row.name,
  });
}

function buildDbCardIndexes(rows) {
  const byExactKey = new Map();
  const byNameKey = new Map();
  for (const row of rows) {
    addRealtimeIndex(byExactKey, dbExactKey(row), row);
    addRealtimeIndex(byNameKey, realtimeNameKey(row), row);
  }
  return { byExactKey, byNameKey };
}

function resolveOverrideDbRow(detail, override, dbById, dbIndexes) {
  if (!detail || !override || override.action !== 'map_to_db') return null;
  if (override.target_card_id) {
    const byId = dbById && dbById.get(String(override.target_card_id));
    if (byId) return byId;
  }
  const exact = dbIndexes && uniqueRealtimeIndex(dbIndexes.byExactKey, detail.exact_key);
  if (exact) return exact;
  return dbIndexes ? uniqueRealtimeIndex(dbIndexes.byNameKey, detail.name_key) : null;
}

function detailPowerToSwitch(power) {
  if (power === '开机') return 'ON';
  if (power === '关机') return 'OFF';
  return '';
}

function makeUnifiedDeviceRow(detail, dbRow, annotations, override = null, zuoIndexes = { byFloorName: new Map(), byName: new Map() }) {
  const building = keyPart(detail.source_building);
  const name = keyPart(detail.source_name);
  const noteKey = annotationKey(building, name);
  const fields = detail.fields || {};
  const power = String(fields['当前开关机状态'] || '').trim();
  const mode = String(fields['系统模式设置'] || '').trim();
  const fan = String(fields['设定风速'] || '').trim();
  const action = override && MATCH_OVERRIDE_ACTIONS.has(override.action) ? override.action : '';
  const zuoInfo = zuoInfoForDevice(building, dbRow, detail, override, zuoIndexes);
  const areaInfo = areaTypeInfoForDevice(detail, dbRow, override);
  let matchStatus = '详情设备';
  if (action === 'map_to_db' && dbRow) matchStatus = '手动匹配';
  else if (action === 'create_virtual') matchStatus = '虚拟纳管';
  else if (action === 'ignore_duplicate') matchStatus = '已忽略重复';
  else if (override && (override.zuo_override || override.area_type_override)) matchStatus = '人工分类';
  const communication = commState({ realtime: detail });
  return {
    id: dbRow ? dbRow.id : `rt:${detail.row_id}`,
    building,
    floor: detail.source_floor,
    floor_label: displayFloorLabelFromSubArea(detail.source_sub_area || '', detail.source_floor),
    sub_area: detail.source_sub_area || '',
    x: dbRow ? dbRow.x : null,
    y: dbRow ? dbRow.y : null,
    page_name: detail.source_page_name || detail.source_tab || 'default',
    layout: dbRow ? dbRow.layout : '',
    name,
    switch: detailPowerToSwitch(power),
    mode,
    indoor: fields['室内温度'] || '',
    set_temp: fields['设定温度'] || '',
    fan,
    indicator: detail.card_indicator || (dbRow ? dbRow.indicator : ''),
    comm: communication.state,
    comm_state: communication.state,
    comm_state_source: communication.source,
    card_comm: detail.card_comm || '',
    card_switch: detail.card_switch || '',
    card_indicator: detail.card_indicator || '',
    card_switch_indicator: detail.card_switch_indicator || '',
    card_state_source: detail.card_state_source || '',
    db_switch: dbRow ? dbRow.switch : '',
    db_mode: dbRow ? dbRow.mode : '',
    db_indoor: dbRow ? dbRow.indoor : '',
    db_set_temp: dbRow ? dbRow.set_temp : '',
    db_fan: dbRow ? dbRow.fan : '',
    db_indicator: dbRow ? dbRow.indicator : '',
    db_comm: dbRow ? dbRow.comm : '',
    db_source_mtime: dbRow ? dbRow.db_source_mtime || '' : '',
    realtime_source_mtime: detail.source_mtime || '',
    db_match: !!dbRow,
    managed_match: !!dbRow || action === 'create_virtual',
    virtual_match: action === 'create_virtual',
    match_status: matchStatus,
    dev_id: detail.dev_id || '',
    meter_id: detail.meter_id || '',
    rtu_id: detail.rtu_id || '',
    realtime: detail,
    note: annotations.notes.get(noteKey) || (dbRow ? dbRow.note || '' : ''),
    tags: annotations.tags.get(noteKey) || (dbRow ? dbRow.tags || [] : []),
    zuo: zuoInfo.value || '',
    zuo_source: zuoInfo.source || 'unknown',
    zuo_source_label: ZUO_SOURCE_LABELS[zuoInfo.source] || ZUO_SOURCE_LABELS.unknown,
    area_type: areaInfo.value,
    area_type_source: areaInfo.source,
    realtime_override_id: override ? override.id : null,
    match_override_action: action,
  };
}

function applyUnifiedFilters(rows, query) {
  let filtered = rows;
  const floorLabels = uniqueListParam(query.floor).map(normalizeFloorLabel).filter(Boolean);
  if (floorLabels.length) {
    const allowedFloors = new Set(floorLabels);
    filtered = filtered.filter(r => allowedFloors.has(r.floor_label || displayFloorLabelFromSubArea(r.sub_area, r.floor)));
  }
  const zuoValues = uniqueListParam(query.zuo);
  if (zuoValues.length) {
    const allowedZuos = new Set(zuoValues);
    filtered = filtered.filter(r => allowedZuos.has(r.zuo));
  }
  if (query.tag) filtered = filtered.filter(r => (r.tags || []).includes(query.tag));
  if (query.area === 'public') filtered = filtered.filter(r => r.area_type === '公区');
  if (query.area === 'private') filtered = filtered.filter(r => r.area_type === '非公区');
  if (query.area === 'unmatched') filtered = filtered.filter(r => r.area_type === '未匹配');
  if (query.status) filtered = filtered.filter(r => matchesDbStatus(r, query.status));
  if (query.comm_state) filtered = filtered.filter(r => commStateValue(r) === query.comm_state);
  if (query.issue) filtered = filtered.filter(r => matchesIssueFilter(r, query.issue));
  if (query.temp_state) filtered = filtered.filter(r => matchesTempState(r, query.temp_state));
  for (const [queryKey, fieldName] of Object.entries(REALTIME_QUERY_FIELDS)) {
    if (!query[queryKey]) continue;
    const expected = String(query[queryKey]).trim();
    if (expected === '__abnormal') {
      filtered = filtered.filter(r => isRealtimeFieldAbnormal(fieldName, realtimeFieldValue(r, fieldName)));
    } else {
      filtered = filtered.filter(r => realtimeFieldValue(r, fieldName) === expected);
    }
  }
  if (query.realtime_match === 'matched') filtered = filtered.filter(r => isManagedRealtimeRow(r));
  if (query.realtime_match === 'missing') filtered = filtered.filter(r => isUnresolvedRealtimeRow(r));
  if (query.realtime_match === 'ignored') filtered = filtered.filter(r => r.match_override_action === 'ignore_duplicate');
  if (query.realtime_match === 'invalid') filtered = filtered.filter(r => isRealtimeDetailInvalid(r.realtime));
  if (query.realtime_points === 'complete') filtered = filtered.filter(r => isRealtimePointsComplete(r.realtime));
  if (query.realtime_points === 'incomplete') filtered = filtered.filter(r => !isRealtimePointsComplete(r.realtime));
  if (query.realtime_points === 'missing') filtered = filtered.filter(r => !r.realtime);
  if (query.q) {
    const q = String(query.q).trim().toLowerCase();
    filtered = filtered.filter(r => {
      if (!q) return true;
      return [
        r.name,
        r.floor_label,
        r.sub_area,
        r.page_name,
        r.mode,
        r.fan,
        r.zuo,
        r.db_comm,
        r.comm_state,
        r.card_comm,
        r.db_switch,
        r.dev_id,
        realtimeFieldValue(r, '通讯地址 (Modbus)'),
      ].some(v => String(v || '').toLowerCase().includes(q));
    });
  }
  return filtered;
}

function applyGroupFilter(rows, query, buildings, runId) {
  if (!query.groups) return rows;
  const groupRows = loadDbCards({
    building: buildings.join(','),
    run_id: runId || '',
    groups: query.groups,
    limit: 20000,
    _maxLimit: 20000,
  }).rows || [];
  const allowed = new Set(groupRows.map(r => String(r.id)));
  return rows.filter(r => r.db_match && allowed.has(String(r.id)));
}

function shortcutScopeQuery(query) {
  return {
    floor: query.floor || '',
    zuo: query.zuo || '',
    tag: query.tag || '',
    q: query.q || '',
    comm_state: query.comm_state || '',
  };
}

function computeShortcutCounts(rows, query, buildings, runId) {
  const scoped = applyGroupFilter(applyUnifiedFilters(rows, shortcutScopeQuery(query)), query, buildings, runId);
  const counts = {};
  for (const item of CARD_SHORTCUT_FILTERS) {
    counts[item.key] = applyUnifiedFilters(scoped, item.apply).length;
  }
  return counts;
}

function loadCards(query) {
  const db = openReadonlyDb();
  let runId = null;
  try {
    runId = resolveRunId(db, query.run_id || query.runId);
  } finally {
    db.close();
  }
  const buildingValues = uniqueListParam(query.building).filter(b => BLDG_ORDER.includes(b));
  const buildings = buildingValues.length ? buildingValues : BLDG_ORDER;
  const details = loadRealtimeDetails(buildings);
  const dbRows = loadDbCards({
    building: buildings.join(','),
    run_id: runId || '',
    include_realtime: '1',
    limit: 20000,
    _maxLimit: 20000,
  }).rows || [];
  const dbByRealtimeRowId = new Map();
  const dbById = new Map();
  for (const row of dbRows) {
    dbById.set(String(row.id), row);
    if (row.realtime && row.realtime.row_id && !dbByRealtimeRowId.has(row.realtime.row_id)) {
      dbByRealtimeRowId.set(row.realtime.row_id, row);
    }
  }
  const dbIndexes = buildDbCardIndexes(dbRows);
  const annotations = loadAnnotationMaps();
  const overrides = loadRealtimeMatchOverrides();
  const zuoIndexes = buildZuoInferenceIndexes(dbRows);
  const rows = details.rows.map(detail => {
    const override = findRealtimeMatchOverride(detail, overrides);
    let dbRow = dbByRealtimeRowId.get(detail.row_id) || null;
    if (!dbRow) dbRow = resolveOverrideDbRow(detail, override, dbById, dbIndexes);
    return makeUnifiedDeviceRow(detail, dbRow, annotations, override, zuoIndexes);
  });

  const shortcutCounts = computeShortcutCounts(rows, query, buildings, runId);
  let filtered = applyUnifiedFilters(rows, query);
  filtered = applyGroupFilter(filtered, query, buildings, runId);
  const offset = clamp(query.offset, 0, filtered.length);
  const maxLimit = Number.isFinite(Number(query._maxLimit)) ? clamp(query._maxLimit, 1, 20000) : 1000;
  const limit = clamp(query.limit, 1, maxLimit);
  return {
    run_id: runId,
    total: filtered.length,
    rows: filtered.slice(offset, offset + limit),
    facets: computeCardFacets(filtered),
    shortcut_counts: shortcutCounts,
  };
}

function filterValueLabel(kind, value) {
  const v = String(value ?? '');
  if (!v || v === '-') return '未知';
  if (kind === 'fan' && v === '0') return '默认0';
  if (kind === 'realtime' && /^(?:\d{3,}|[3-9]\d)$/.test(v)) return `异常值：${v}`;
  return v;
}

function displayFloorLabelFromSubArea(subArea, fallbackFloor) {
  const raw = String(subArea || '').trim().toUpperCase();
  if (raw === 'BM') return 'BM';
  const leading = raw.match(/^B\d+(?:\.\d+)?F\b|^\d+(?:\.\d+)?F\b/);
  if (leading) return normalizeFloorLabel(leading[0]);
  const exact = raw.match(/\bB\d+(?:\.\d+)?F\b|\b\d+(?:\.\d+)?F\b/);
  if (exact) return normalizeFloorLabel(exact[0]);
  return floorLabelFromValue(fallbackFloor);
}

function loadFilterOptions(query = {}) {
  const db = fs.existsSync(DB_PATH) ? openReadonlyDb() : null;
  try {
    const runId = db ? resolveRunId(db, query.run_id || query.runId) : null;
    const buildingValues = uniqueListParam(query.building).filter(b => BLDG_ORDER.includes(b));
    const realtimeRows = loadCards({
      building: buildingValues.join(','),
      run_id: query.run_id || query.runId || '',
      include_realtime: '1',
      limit: 20000,
      _maxLimit: 20000,
    }).rows || [];
    const zuoCounts = new Map();
    for (const row of realtimeRows) {
      if (!row.zuo) continue;
      zuoCounts.set(row.zuo, (zuoCounts.get(row.zuo) || 0) + 1);
    }
    const zuos = Array.from(zuoCounts.entries())
      .sort((a, b) => a[0].localeCompare(b[0], 'zh-CN', { numeric: true }))
      .map(([value, count]) => ({ value, raw: value, label: value, count }));
    const floorCounts = new Map();
    for (const row of realtimeRows) {
      const label = row.floor_label || displayFloorLabelFromSubArea(row.sub_area, row.floor);
      if (!label) continue;
      floorCounts.set(label, (floorCounts.get(label) || 0) + 1);
    }
    const floors = Array.from(floorCounts.entries())
      .map(([value, count]) => ({
        value,
        raw: value,
        label: value,
        count,
        floor_value: parseFloorValue(value),
      }))
      .sort((a, b) => {
        const av = Number.isFinite(Number(a.floor_value)) ? Number(a.floor_value) : 999999;
        const bv = Number.isFinite(Number(b.floor_value)) ? Number(b.floor_value) : 999999;
        return av - bv || a.label.localeCompare(b.label, 'zh-CN', { numeric: true });
      });
    const realtimeValues = fieldName => {
      const counts = new Map();
      for (const row of realtimeRows) {
        const value = realtimeFieldValue(row, fieldName);
        if (!value) continue;
        counts.set(value, (counts.get(value) || 0) + 1);
      }
      const preferred = REALTIME_NORMAL_VALUES[fieldName] || [];
      const options = [];
      let abnormalCount = 0;
      for (const value of preferred) {
        const count = counts.get(value) || 0;
        if (!count) continue;
        options.push({
          value,
          raw: value,
          label: filterValueLabel('realtime', value),
          count,
          abnormal: false,
        });
      }
      for (const [value, count] of counts.entries()) {
        if (preferred.includes(value)) continue;
        abnormalCount += Number(count || 0);
      }
      if (abnormalCount > 0) {
        options.push({
          value: '__abnormal',
          raw: '__abnormal',
          label: '异常',
          count: abnormalCount,
          abnormal: true,
        });
      }
      if (preferred.length) return options;
      return Array.from(counts.entries())
        .sort((a, b) => a[0].localeCompare(b[0], 'zh-CN', { numeric: true }))
        .map(([value, count]) => ({ value, raw: value, label: value, count, abnormal: false }));
    };
    const countRows = predicate => realtimeRows.reduce((sum, row) => sum + (predicate(row) ? 1 : 0), 0);
    const commStateOptions = ['在线', '离线', '未知']
      .map(value => ({ value, raw: value, label: value === '未知' ? '通讯未知' : `通讯${value}`, count: countRows(row => commStateValue(row) === value) }))
      .filter(opt => opt.count > 0);
    const issueOptions = ISSUE_FILTERS
      .map(opt => ({ ...opt, count: countRows(row => matchesIssueFilter(row, opt.value)) }))
      .filter(opt => opt.count > 0 || opt.value === 'needs_review' || opt.value === 'exclude_abnormal');
    const tempOptions = TEMP_STATE_FILTERS
      .map(opt => ({ ...opt, count: countRows(row => matchesTempState(row, opt.value)) }))
      .filter(opt => opt.count > 0);
    const areaCounts = new Map();
    for (const row of realtimeRows) {
      const value = row.area_type || '未匹配';
      areaCounts.set(value, (areaCounts.get(value) || 0) + 1);
    }
    const areaTypes = [
      { value: 'public', raw: '公区', label: '公区', count: areaCounts.get('公区') || 0 },
      { value: 'private', raw: '非公区', label: '非公区', count: areaCounts.get('非公区') || 0 },
      { value: 'unmatched', raw: '未匹配', label: '未匹配区域', count: areaCounts.get('未匹配') || 0 },
    ].filter(opt => opt.count > 0);
    return {
      run_id: runId,
      modes: realtimeValues('系统模式设置'),
      fans: realtimeValues('设定风速'),
      zuos,
      floors,
      issues: issueOptions,
      temp_states: tempOptions,
      area_types: areaTypes,
      db_statuses: [],
      comm_states: commStateOptions,
      realtime_power: realtimeValues('当前开关机状态'),
      realtime_fan_settings: realtimeValues('设定风速'),
      realtime_mode_settings: realtimeValues('系统模式设置'),
      realtime_locks: realtimeValues('集控锁定'),
      realtime_system_types: realtimeValues('系统类型'),
      realtime_modbus: realtimeValues('通讯地址 (Modbus)'),
    };
  } finally {
    if (db) db.close();
  }
}

function realtimeCoverage(query = {}) {
  const db = fs.existsSync(DB_PATH) ? openReadonlyDb() : null;
  try {
    const runId = db ? resolveRunId(db, query.run_id || query.runId) : null;
    const buildingValues = uniqueListParam(query.building).filter(b => BLDG_ORDER.includes(b));
    const buildings = buildingValues.length ? buildingValues : BLDG_ORDER;
    const dbRows = db ? loadDbCards({
      building: buildings.join(','),
      run_id: runId || '',
      include_realtime: '1',
      limit: 20000,
      _maxLimit: 20000,
    }).rows || [] : [];
    const allRows = loadCards({
      building: buildings.join(','),
      run_id: runId || '',
      include_realtime: '1',
      limit: 20000,
      _maxLimit: 20000,
    }).rows || [];
    const details = loadRealtimeDetails(buildings);
    const dbByBuilding = new Map();
    for (const row of dbRows) {
      dbByBuilding.set(row.building, (dbByBuilding.get(row.building) || 0) + 1);
    }
    const realtimeByBuilding = new Map();
    const reviewByBuilding = new Map();
    const unresolvedByBuilding = new Map();
    const invalidByBuilding = new Map();
    const ignoredRows = allRows.filter(isIgnoredRealtimeRow);
    const realtimeUnmatchedRows = allRows.filter(isRealtimeDbUnmatchedRow);
    const reviewRows = allRows.filter(row => !isIgnoredRealtimeRow(row) && hasReviewIssue(row));
    const invalidRows = allRows.filter(row => isRealtimeDetailInvalid(row.realtime));
    const unmatchedSamples = [];
    for (const row of allRows) {
      const building = row.building || '';
      realtimeByBuilding.set(building, (realtimeByBuilding.get(building) || 0) + 1);
      if (hasReviewIssue(row)) reviewByBuilding.set(building, (reviewByBuilding.get(building) || 0) + 1);
      if (isRealtimeDbUnmatchedRow(row)) {
        unresolvedByBuilding.set(building, (unresolvedByBuilding.get(building) || 0) + 1);
      }
      if (isRealtimeDetailInvalid(row.realtime)) {
        invalidByBuilding.set(building, (invalidByBuilding.get(building) || 0) + 1);
      }
      if (isRealtimeDbUnmatchedRow(row) && unmatchedSamples.length < 20) {
        unmatchedSamples.push({
          building,
          floor: row.floor_label || row.floor,
          sub_area: row.sub_area,
          page_name: row.page_name,
          name: row.name,
          dev_id: row.dev_id,
          source_file: row.realtime && row.realtime.source_file,
        });
      }
    }
    const byBuilding = buildings.map(building => {
      const dbCount = Number(dbByBuilding.get(building) || 0);
      const realtimeCount = Number(realtimeByBuilding.get(building) || 0);
      const unmatchedCount = Number(unresolvedByBuilding.get(building) || 0);
      return {
        building,
        db_rows: dbCount,
        realtime_rows: realtimeCount,
        matched: realtimeCount,
        db_missing_realtime: 0,
        realtime_unmatched: unmatchedCount,
        needs_review: Number(reviewByBuilding.get(building) || 0),
        invalid: Number(invalidByBuilding.get(building) || 0),
        delta: realtimeCount - dbCount,
      };
    });
    return {
      run_id: runId,
      fields: REALTIME_FIELDS,
      files: details.files,
      db_rows: dbRows.length,
      realtime_rows: allRows.length,
      matched: allRows.length,
      matched_realtime_rows: allRows.length,
      db_missing_realtime: 0,
      realtime_unmatched: realtimeUnmatchedRows.length,
      realtime_ignored: ignoredRows.length,
      realtime_handled: allRows.filter(row => row.match_override_action === 'create_virtual' || row.match_override_action === 'classify_only').length,
      needs_review: reviewRows.length,
      invalid: invalidRows.length,
      delta: allRows.length - dbRows.length,
      by_building: byBuilding,
      samples: {
        db_missing_realtime: [],
        realtime_unmatched: unmatchedSamples,
      },
    };
  } finally {
    if (db) db.close();
  }
}

function loadAreaOptions(query = {}) {
  const db = openReadonlyDb();
  try {
    const runId = resolveRunId(db, query.run_id || query.runId);
    const source = sourceForRun(runId);
    const building = String(query.building || '').trim();
    const floorValue = parseFloorValue(query.floor || query.floor_label || query.floorLabel || '');
    const params = [];
    const where = [];
    if (source.runWhere) {
      where.push(source.runWhere);
      params.push(...source.runParams);
    }
    if (building) {
      where.push('sa.building = ?');
      params.push(building);
    }
    if (Number.isFinite(floorValue)) {
      where.push('ABS(COALESCE(sa.floor, -999999) - ?) < 0.001');
      params.push(floorValue);
    }
    const whereSql = where.length ? 'WHERE ' + where.join(' AND ') : '';
    const subAreas = db.prepare(`
      SELECT sa.building, sa.floor, sa.text AS sub_area_text,
             COUNT(DISTINCT c.id) AS card_count,
             SUM(c.comm = '开机' OR c.switch = 'ON') AS on_count,
             SUM(c.comm = '关机' OR c.switch = 'OFF') AS off_count,
             SUM(c.comm = '离线') AS offline_count
      FROM ${source.subAreas} sa
      JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
      JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
      ${whereSql}
      GROUP BY sa.building, sa.floor, sa.text
      ORDER BY sa.building, sa.floor, sa.text
      LIMIT 1000
    `).all(...params).map(r => ({
      building: r.building,
      floor_value: r.floor,
      floor_label: floorLabelFromValue(r.floor),
      sub_area_text: r.sub_area_text || '',
      card_count: r.card_count || 0,
      on_count: r.on_count || 0,
      off_count: r.off_count || 0,
      offline_count: r.offline_count || 0,
    }));

    const deviceWhere = [...where];
    const deviceParams = [...params];
    if (query.sub_area_text || query.subArea) {
      deviceWhere.push("IFNULL(sa.text, '') = ?");
      deviceParams.push(String(query.sub_area_text || query.subArea || '').trim());
    }
    if (query.q) {
      deviceWhere.push('c.name LIKE ?');
      deviceParams.push(`%${query.q}%`);
    }
    const deviceWhereSql = deviceWhere.length ? 'WHERE ' + deviceWhere.join(' AND ') : '';
    const devices = db.prepare(`
      SELECT sa.building, sa.floor, sa.text AS sub_area_text,
             c.name, c.comm, c.switch, c.mode, c.fan
      FROM ${source.subAreas} sa
      JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
      JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
      ${deviceWhereSql}
      GROUP BY sa.building, sa.floor, sa.text, c.name
      ORDER BY sa.building, sa.floor, sa.text, c.name
      LIMIT 1000
    `).all(...deviceParams).map(r => ({
      building: r.building,
      floor_value: r.floor,
      floor_label: floorLabelFromValue(r.floor),
      sub_area_text: r.sub_area_text || '',
      name: r.name || '',
      comm: r.comm || '',
      switch: r.switch || '',
      mode: r.mode || '',
      fan: r.fan || '',
    }));

    return { run_id: runId, sub_areas: subAreas, devices };
  } finally {
    db.close();
  }
}

function readMarkdownExcerpt(fileName, maxChars = 6000) {
  const full = path.join(ROOT, fileName);
  if (!fs.existsSync(full)) return '';
  const text = fs.readFileSync(full, 'utf8');
  return text.length > maxChars ? text.slice(0, maxChars) + '\n\n...' : text;
}

function loadAboutInfo() {
  const summary = loadSummary({});
  const coverage = realtimeCoverage({});
  return {
    name: 'EMS HVAC 控制台',
    root: ROOT,
    db_path: DB_PATH,
    out_dir: path.relative(ROOT, OUT_DIR),
    port: PORT,
    tech_stack: ['Node.js', '原生 HTML/CSS/JS', 'SQLite', 'Playwright'],
    current_data: {
      total: summary.total || 0,
      db_total: summary.db_total || 0,
      detail_total: summary.detail_total || 0,
      matched: coverage.matched || 0,
      db_missing_realtime: coverage.db_missing_realtime || 0,
      realtime_unmatched: coverage.realtime_unmatched || 0,
      delta: coverage.delta || 0,
    },
    docs: {
      stage1: readMarkdownExcerpt('UI_REFACTOR_STAGE_1.md', 5000),
      stage2: readMarkdownExcerpt('UI_REFACTOR_STAGE_2.md', 5000),
      changelog: readMarkdownExcerpt('CHANGELOG.md', 7000),
    },
  };
}

function realtimeSummary() {
  const details = loadRealtimeDetails(BLDG_ORDER);
  const totalRows = details.files.reduce((sum, file) => sum + Number(file.rows || 0), 0);
  const latestMtime = details.files
    .map(file => file.mtime)
    .filter(Boolean)
    .sort()
    .at(-1) || '';
  return {
    fields: REALTIME_FIELDS,
    total_rows: totalRows,
    file_count: details.files.length,
    latest_mtime: latestMtime,
    files: details.files,
  };
}

function readLatestLog() {
  if (!fs.existsSync(OUT_DIR)) return { files: [], content: '' };
  const files = fs.readdirSync(OUT_DIR)
    .filter(n => /^enum_.*\.log$/i.test(n))
    .map(n => {
      const full = path.join(OUT_DIR, n);
      return { name: n, path: path.relative(ROOT, full), mtime: fs.statSync(full).mtimeMs };
    })
    .sort((a, b) => b.mtime - a.mtime);
  if (!files.length) return { files, content: '' };
  const content = tailFile(path.join(ROOT, files[0].path), 350);
  return { files, content };
}

function tailFile(file, maxLines) {
  try {
    const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
    return lines.slice(Math.max(0, lines.length - maxLines)).join('\n');
  } catch {
    return '';
  }
}

function saveNote(body) {
  const db = openDb();
  try {
    ensureMonitorSchema(db);
    const now = new Date().toISOString();
    db.prepare(`
      INSERT INTO device_notes (card_name, building, note, created_at, updated_at)
      VALUES (?, ?, ?, ?, ?)
      ON CONFLICT(card_name, building) DO UPDATE SET note = excluded.note, updated_at = excluded.updated_at
    `).run(body.card_name, body.building || null, String(body.note || ''), now, now);
    return { ok: true };
  } finally {
    db.close();
  }
}

function saveTag(body) {
  const db = openDb();
  try {
    ensureMonitorSchema(db);
    const tag = String(body.tag || '').trim();
    if (!tag) throw new Error('tag is required');
    db.prepare(`
      INSERT OR IGNORE INTO device_tags (card_name, building, tag)
      VALUES (?, ?, ?)
    `).run(body.card_name, body.building || null, tag);
    return { ok: true };
  } finally {
    db.close();
  }
}

function deleteTag(body) {
  const db = openDb();
  try {
    ensureMonitorSchema(db);
    db.prepare(`
      DELETE FROM device_tags
      WHERE card_name = ? AND IFNULL(building, '') = IFNULL(?, '') AND tag = ?
    `).run(body.card_name, body.building || null, String(body.tag || '').trim());
    return { ok: true };
  } finally {
    db.close();
  }
}

function existingRealtimeOverride(db, body) {
  const building = keyPart(body.building);
  const devId = keyPart(body.dev_id || body.devId);
  if (devId) {
    const row = db.prepare(`
      SELECT * FROM realtime_match_overrides
      WHERE building = ? AND IFNULL(dev_id, '') = ?
      ORDER BY id DESC
      LIMIT 1
    `).get(building, devId);
    if (row) return row;
  }
  const floorLabel = normalizeFloorLabel(body.floor_label || body.floor || '');
  const subArea = String(body.sub_area || body.subArea || '').trim();
  const pageName = String(body.page_name || body.pageName || body.tab || 'default').trim() || 'default';
  const name = keyPart(body.realtime_name || body.card_name || body.name);
  return db.prepare(`
    SELECT * FROM realtime_match_overrides
    WHERE building = ?
      AND IFNULL(floor_label, '') = IFNULL(?, '')
      AND IFNULL(sub_area, '') = IFNULL(?, '')
      AND IFNULL(page_name, '') = IFNULL(?, '')
      AND realtime_name = ?
    ORDER BY id DESC
    LIMIT 1
  `).get(building, floorLabel, subArea, pageName, name);
}

function realtimeOverridePayload(body, existing = null) {
  const building = keyPart(body.building);
  const name = keyPart(body.realtime_name || body.card_name || body.name);
  if (!building || !name) throw new Error('building and realtime_name are required');
  const actionRaw = body.action !== undefined ? String(body.action || '').trim() : (existing ? existing.action : 'classify_only');
  const action = actionRaw || 'classify_only';
  if (!MATCH_OVERRIDE_ACTIONS.has(action)) throw new Error('不支持的匹配处理动作');
  const hasZuo = Object.prototype.hasOwnProperty.call(body, 'zuo_override') || Object.prototype.hasOwnProperty.call(body, 'zuo');
  const hasArea = Object.prototype.hasOwnProperty.call(body, 'area_type_override') || Object.prototype.hasOwnProperty.call(body, 'area_type');
  const hasTarget = Object.prototype.hasOwnProperty.call(body, 'target_card_id') || Object.prototype.hasOwnProperty.call(body, 'targetCardId');
  const hasNote = Object.prototype.hasOwnProperty.call(body, 'note');
  const devId = keyPart(body.dev_id || body.devId || (existing ? existing.dev_id : ''));
  const floorLabel = normalizeFloorLabel(body.floor_label || body.floor || (existing ? existing.floor_label : ''));
  const subArea = String(body.sub_area || body.subArea || (existing ? existing.sub_area : '') || '').trim();
  const pageName = String(body.page_name || body.pageName || body.tab || (existing ? existing.page_name : 'default') || 'default').trim() || 'default';
  const zuoOverride = hasZuo
    ? normalizeZuoValue(body.zuo_override !== undefined ? body.zuo_override : body.zuo, building)
    : (existing ? existing.zuo_override || '' : '');
  const areaOverride = hasArea
    ? normalizeAreaTypeOverride(body.area_type_override !== undefined ? body.area_type_override : body.area_type)
    : (existing ? existing.area_type_override || '' : '');
  const targetCardId = hasTarget
    ? (body.target_card_id || body.targetCardId ? Number(body.target_card_id || body.targetCardId) : null)
    : (existing ? existing.target_card_id : null);
  if (targetCardId !== null && (!Number.isInteger(targetCardId) || targetCardId <= 0)) throw new Error('target_card_id 无效');
  const note = hasNote ? String(body.note || '').trim() : (existing ? existing.note || '' : '');
  return {
    building,
    dev_id: devId,
    floor_label: floorLabel,
    sub_area: subArea,
    page_name: pageName,
    realtime_name: name,
    action,
    target_card_id: targetCardId,
    zuo_override: zuoOverride,
    area_type_override: areaOverride,
    note,
  };
}

function overrideIsEmpty(payload) {
  return payload.action === 'classify_only' &&
    !payload.target_card_id &&
    !payload.zuo_override &&
    !payload.area_type_override &&
    !payload.note;
}

function saveRealtimeMatchOverride(body) {
  const db = openDb();
  try {
    ensureMonitorSchema(db);
    const existing = existingRealtimeOverride(db, body);
    const payload = realtimeOverridePayload(body, existing);
    if (existing && overrideIsEmpty(payload)) {
      db.prepare('DELETE FROM realtime_match_overrides WHERE id = ?').run(existing.id);
      return { ok: true, deleted: existing.id };
    }
    const now = new Date().toISOString();
    if (existing) {
      db.prepare(`
        UPDATE realtime_match_overrides
        SET building = ?, dev_id = ?, floor_label = ?, sub_area = ?, page_name = ?,
            realtime_name = ?, action = ?, target_card_id = ?, zuo_override = ?,
            area_type_override = ?, note = ?, updated_at = ?
        WHERE id = ?
      `).run(
        payload.building, payload.dev_id || null, payload.floor_label || null, payload.sub_area || null, payload.page_name || null,
        payload.realtime_name, payload.action, payload.target_card_id, payload.zuo_override || null,
        payload.area_type_override || null, payload.note, now, existing.id,
      );
      return { ok: true, data: db.prepare('SELECT * FROM realtime_match_overrides WHERE id = ?').get(existing.id) };
    }
    if (overrideIsEmpty(payload)) return { ok: true, data: null };
    const res = db.prepare(`
      INSERT INTO realtime_match_overrides
        (building, dev_id, floor_label, sub_area, page_name, realtime_name, action,
         target_card_id, zuo_override, area_type_override, note, created_at, updated_at)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    `).run(
      payload.building, payload.dev_id || null, payload.floor_label || null, payload.sub_area || null, payload.page_name || null,
      payload.realtime_name, payload.action, payload.target_card_id, payload.zuo_override || null,
      payload.area_type_override || null, payload.note, now, now,
    );
    return { ok: true, data: db.prepare('SELECT * FROM realtime_match_overrides WHERE id = ?').get(res.lastInsertRowid) };
  } finally {
    db.close();
  }
}

function publicTask(task) {
  if (!task) return null;
  const base = {
    id: task.id,
    kind: task.kind,
    label: task.label,
    status: task.status,
    started_at: task.started_at,
    ended_at: task.ended_at,
    exit_code: task.exit_code,
    error: task.error,
    steps: task.steps.map(s => ({ label: s.label, status: s.status, exit_code: s.exit_code })),
    progress: task.progress || null,
    logs: task.logs.slice(-500),
    log_file: task.log_file,
  };
  return taskService.decorateTask(base);
}

function updateTaskProgress(task, rawLine) {
  const match = String(rawLine || '').match(/^\[PROGRESS\]\s*(\{.*\})\s*$/);
  if (!match) return false;
  try {
    const progress = JSON.parse(match[1]);
    task.progress = {
      ...task.progress,
      ...progress,
      updated_at: new Date().toISOString(),
    };
    return true;
  } catch {
    return false;
  }
}

function addTaskLog(task, text) {
  const normalized = String(text).replace(/\r/g, '\n');
  for (const line of normalized.split(/\n/)) {
    if (!line) continue;
    updateTaskProgress(task, line);
    const row = `[${new Date().toLocaleTimeString()}] ${line}`;
    task.logs.push(row);
    if (task.logs.length > 3000) task.logs.splice(0, task.logs.length - 3000);
    if (task.log_stream) task.log_stream.write(row + '\n');
  }
}

function cleanTaskLogLine(line) {
  return String(line || '')
    .replace(/\x1b\[[0-9;]*m/g, '')
    .replace(/^\[[^\]]+\]\s*/, '')
    .trim();
}

function taskFailureHint(task) {
  const recent = (task.logs || [])
    .slice(-40)
    .map(cleanTaskLogLine)
    .filter(Boolean)
    .filter(line => !line.startsWith('at '))
    .filter(line => !line.startsWith('[LOG] '))
    .filter(line => !line.startsWith('[RUN] '))
    .filter(line => !/^Error:\s*Command failed/i.test(line));
  const preferred = [...recent].reverse().find(line =>
    /(^Error:|ECONNREFUSED|Cannot connect|No EMS page|未登录|未就绪|无法连接|未找到|失败|failed|not ready)/i.test(line)
  );
  return preferred || recent.slice(-1)[0] || '';
}

function buildTaskSteps(payload) {
  const kind = payload.kind || payload.task || 'realtimeDetails';
  const buildings = Array.isArray(payload.buildings) ? payload.buildings.filter(Boolean) : [];
  const selectedBuildings = buildings.length ? buildings.join(',') : '';
  const captureMode = payload.captureMode === 'cdp' ? 'cdp' : 'autoLaunch';
  const enumArgs = ['src/enumerate.js', captureMode === 'cdp' ? '--edge' : '--auto-launch'];
  const allowAppend = kind !== 'collectSafe' || !!selectedBuildings;
  if (payload.append && allowAppend) enumArgs.push('--append');
  if (selectedBuildings) enumArgs.push(`--bldg=${selectedBuildings}`);
  if (payload.verify) enumArgs.push('--verify');
  if (payload.selfDiagnose) enumArgs.push('--self-diagnose');
  if (payload.noNetMonitor) enumArgs.push('--no-net-monitor');
  if (payload.logFile) enumArgs.push('--log-file');
  if (payload.logLevel) enumArgs.push(`--log-level=${payload.logLevel}`);
  if (payload.logCategory) enumArgs.push(`--log-category=${payload.logCategory}`);
  if (payload.recapture) enumArgs.push(`--recapture=${payload.recapture}`);

  const importArgs = ['scripts/import.js'];
  if (selectedBuildings) importArgs.push(`--bldg=${selectedBuildings}`);
  const validateArgs = ['scripts/validate-enum.js'];
  if (selectedBuildings) validateArgs.push(`--bldg=${selectedBuildings}`);

  const qualityArgs = ['scripts/audit-realtime-data.js'];
  const selectedRunId = payload.run_id || payload.runId || '';
  if (selectedRunId && !['collectImport', 'collectSafe', 'enumerate', 'import', 'quality'].includes(kind)) {
    qualityArgs.push(`--run-id=${selectedRunId}`);
  }
  const realtimeArgs = ['scripts/collect-realtime-all-batch.js'];
  realtimeArgs.push(`--buildings=${selectedBuildings || BLDG_ORDER.join(',')}`);
  realtimeArgs.push(`--browser-mode=${captureMode === 'cdp' ? 'cdp' : 'persistent'}`);
  if (payload.realtimeBatchSize) realtimeArgs.push(`--batch-size=${clamp(payload.realtimeBatchSize, 1, 100)}`);
  if (payload.realtimeReopenEvery !== undefined && payload.realtimeReopenEvery !== '') realtimeArgs.push(`--reopen-every=${clamp(payload.realtimeReopenEvery, 0, 50)}`);
  if (payload.realtimeTimeout) realtimeArgs.push(`--timeout=${clamp(payload.realtimeTimeout, 3000, 120000)}`);
  if (payload.realtimeMaxDevices) realtimeArgs.push(`--max-devices=${clamp(payload.realtimeMaxDevices, 0, 20000)}`);
  if (payload.realtimeRefreshInventory) realtimeArgs.push('--refresh-inventory');
  if (payload.realtimeSkipInventory) realtimeArgs.push('--skip-inventory');
  if (payload.realtimeWriteLatest !== false) realtimeArgs.push('--write-latest');
  realtimeArgs.push('--skip-audit');
  if (payload.logFile) realtimeArgs.push('--log-file');

  const stepsByKind = {
    collectImport: [
      { label: '采集', args: enumArgs },
      { label: '采集结果校验', args: validateArgs },
      { label: '导入数据库', args: importArgs },
      { label: '质量审计', args: ['scripts/quality-report.js', '--run-id=latest-run'] },
    ],
    collectSafe: [
      { label: '采集', args: enumArgs },
      { label: '采集结果校验', args: validateArgs },
    ],
    enumerate: [{ label: '采集', args: enumArgs }],
    validate: [{ label: '采集结果校验', args: validateArgs }],
    import: [{ label: '导入数据库', args: importArgs }],
    quality: [{ label: '实时质量审计', args: qualityArgs }],
    realtimeDetails: [
      { label: '实时详情批量采集', args: realtimeArgs },
      { label: '实时质量审计', args: ['scripts/audit-realtime-data.js'] },
    ],
  };
  const steps = stepsByKind[kind];
  if (!steps) throw new Error('Unknown task kind: ' + kind);
  return { kind, steps };
}

function startTask(payload) {
  if (currentTask && currentTask.status === 'running') throw new Error('已有任务正在运行');
  const { kind, steps } = buildTaskSteps(payload);
  const now = new Date();
  const id = `${now.getFullYear()}${String(now.getMonth() + 1).padStart(2, '0')}${String(now.getDate()).padStart(2, '0')}_${String(now.getHours()).padStart(2, '0')}${String(now.getMinutes()).padStart(2, '0')}${String(now.getSeconds()).padStart(2, '0')}`;
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const logFile = path.join(OUT_DIR, `panel_task_${id}.log`);
  const task = {
    id,
    kind,
    label: payload.label || kind,
    status: 'running',
    started_at: now.toISOString(),
    ended_at: null,
    exit_code: null,
    error: null,
    steps: steps.map(s => ({ ...s, status: 'pending', exit_code: null })),
    progress: null,
    logs: [],
    proc: null,
    log_file: path.relative(ROOT, logFile),
    log_stream: fs.createWriteStream(logFile, { flags: 'a' }),
  };
  currentTask = task;
  taskHistory.unshift(task);
  if (taskHistory.length > 10) taskHistory.pop();
  runTaskSteps(task).catch(err => {
    if (task.status === 'stopping') {
      task.status = 'stopped';
      task.error = null;
      task.exit_code = null;
      task.steps.forEach(step => {
        if (step.status === 'running') step.status = 'stopped';
      });
    } else {
      task.status = 'failed';
      task.error = err.message;
    }
    task.ended_at = new Date().toISOString();
    addTaskLog(task, task.status === 'stopped' ? '任务已停止' : '任务失败: ' + err.message);
    if (task.log_stream) task.log_stream.end();
  });
  return publicTask(task);
}

async function runTaskSteps(task) {
  addTaskLog(task, `任务开始: ${task.kind}`);
  for (const step of task.steps) {
    if (task.status === 'stopping') throw new Error('任务已停止');
    step.status = 'running';
    addTaskLog(task, `>> ${step.label}: node ${step.args.join(' ')}`);
    const exitCode = await spawnNodeStep(task, step.args);
    step.exit_code = exitCode;
    if (exitCode !== 0) {
      step.status = 'failed';
      const hint = taskFailureHint(task);
      throw new Error(`${step.label} exited with code ${exitCode}${hint ? `：${hint}` : ''}`);
    }
    step.status = 'done';
  }
  task.status = 'done';
  task.exit_code = 0;
  task.ended_at = new Date().toISOString();
  addTaskLog(task, '任务完成');
  if (task.log_stream) task.log_stream.end();
}

function spawnNodeStep(task, args) {
  return new Promise((resolve, reject) => {
    const proc = spawn(process.execPath, args, { cwd: ROOT, env: process.env, windowsHide: true });
    task.proc = proc;
    proc.stdout.on('data', chunk => addTaskLog(task, chunk.toString('utf8')));
    proc.stderr.on('data', chunk => addTaskLog(task, chunk.toString('utf8')));
    proc.on('error', reject);
    proc.on('close', code => {
      task.proc = null;
      resolve(code);
    });
  });
}

function stopTask() {
  if (!currentTask || currentTask.status !== 'running') return publicTask(currentTask);
  currentTask.status = 'stopping';
  addTaskLog(currentTask, '正在停止任务...');
  const proc = currentTask.proc;
  if (!proc || !proc.pid) return publicTask(currentTask);
  if (process.platform === 'win32') {
    execFile('taskkill', ['/PID', String(proc.pid), '/T', '/F'], { windowsHide: true }, () => {});
  } else {
    proc.kill('SIGTERM');
  }
  return publicTask(currentTask);
}

async function handleApi(req, res, url) {
  const method = req.method || 'GET';
  const pathName = url.pathname;
  const ctx = { req, res, url, method, pathName };
  for (const handler of apiRouteHandlers) {
    if (await handler(ctx)) return;
  }

  fail(res, 404, 'API not found: ' + pathName);
}

function serveStatic(req, res, url) {
  if (!['GET', 'HEAD'].includes(req.method || 'GET')) {
    res.writeHead(405, { allow: 'GET, HEAD' });
    res.end('Method Not Allowed');
    return;
  }
  let pathname = url.pathname;
  if (pathname === '/' || pathname === '/panel') pathname = '/index.html';
  if (pathname.startsWith('/panel/')) pathname = pathname.slice('/panel'.length);
  const full = resolveStaticPath(WEB_ROOT, pathname);
  const file = fs.existsSync(full) && fs.statSync(full).isFile() ? full : path.join(WEB_ROOT, 'index.html');
  const ext = path.extname(file).toLowerCase();
  res.writeHead(200, {
    'content-type': MIME[ext] || 'application/octet-stream',
    'content-security-policy': "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
    'x-content-type-options': 'nosniff',
    'x-frame-options': 'DENY',
  });
  if (req.method === 'HEAD') return res.end();
  fs.createReadStream(file).pipe(res);
}

function createServer() {
  const sessionToken = createSessionToken();
  return http.createServer(async (req, res) => {
    try {
      if (!isLoopbackHost(req.headers.host)) throw new HttpRequestError(403, 'Loopback Host required');
      const url = new URL(req.url || '/', 'http://127.0.0.1');
      if (url.pathname === '/api/session' && (req.method || 'GET') === 'GET') {
        json(res, 200, { ok: true, data: { token: sessionToken } });
      } else if (url.pathname.startsWith('/api/')) {
        authorizeApiRequest(req, sessionToken);
        await handleApi(req, res, url);
      }
      else serveStatic(req, res, url);
    } catch (e) {
      if (e instanceof HttpRequestError) fail(res, e.statusCode, e);
      else {
        console.error('Panel request failed:', e);
        fail(res, 500, 'Internal server error');
      }
    }
  });
}

function main() {
  if (process.env.EMS_ENABLE_LEGACY_PANEL !== '1') {
    console.error('legacy-web-panel is disabled by default. Set EMS_ENABLE_LEGACY_PANEL=1 to run intentionally.');
    process.exitCode = 2;
    return;
  }
  ensurePanelSchemaIfPossible();
  if (process.argv.includes('--check')) {
    console.log(JSON.stringify({ ok: true, root: ROOT, db_path: DB_PATH, web_root: WEB_ROOT }, null, 2));
    return;
  }
  createServer().listen(PORT, '127.0.0.1', () => {
    console.log(`EMS Panel: http://127.0.0.1:${PORT}`);
    console.log(`Workspace: ${ROOT}`);
  });
}

if (require.main === module) main();

module.exports = { createServer, loadSummary, loadCards, startTask };
