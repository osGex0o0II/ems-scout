'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { BLDG_ORDER } = require('../../rules');
const {
  floorLabelFromValue,
  normalizeFloorLabel,
  resolveRunId,
  sourceForRun,
} = require('../history');
const {
  isRealtimeDetailInvalid,
  isRealtimePointsComplete,
} = require('../rules/device-health-rules');
const {
  RULE_VERSION,
  explainDiff,
  ruleCatalog,
} = require('./reconcile-explain.service');

const DIFF_TYPES = {
  NEW_DEVICE: 'NEW_DEVICE',
  MISSING_IN_REALTIME: 'MISSING_IN_REALTIME',
  MATCH_FAILED: 'MATCH_FAILED',
  DUPLICATE_RENDER: 'DUPLICATE_RENDER',
  VIRTUAL_OVERRIDE: 'VIRTUAL_OVERRIDE',
  DATA_NOISE: 'DATA_NOISE',
  UNKNOWN: 'UNKNOWN',
};

const ALGORITHM_VERSION = RULE_VERSION;

function createReconcileService(options) {
  const {
    root,
    outDir,
    openReadonlyDb,
    realtimeService,
  } = options;

  function diff(query = {}) {
    return run(query);
  }

  function run(query = {}) {
    const db = openReadonlyDb();
    try {
      const runId = resolveRunId(db, query.run_id || query.runId);
      const buildings = selectedBuildings(query.building);
      const dbRows = loadDbRows(db, runId, buildings);
      const realtimeDetails = realtimeService.loadRealtimeDetails(buildings);
      const realtimeRows = realtimeDetails.rows.map(normalizeRealtimeRow);
      const realtimeOverrides = loadRealtimeOverrides(db);
      const manualOverrides = loadManualOverrides(db);
      const quality = loadQualityReport(outDir);
      const qualityIndex = buildQualityIndex(quality);

      const indexes = buildIndexes(dbRows, realtimeRows);
      const state = {
        dbRows,
        realtimeRows,
        dbById: indexes.dbById,
        dbBySourceId: indexes.dbBySourceId,
        dbByNameFloor: indexes.dbByNameFloor,
        dbByExact: indexes.dbByExact,
        dbConsumed: new Set(),
        rtConsumed: new Set(),
        diffItems: [],
        realtimeOverrides,
        manualOverrides,
        qualityIndex,
      };

      consumeExactMatches(state);
      applyRealtimeOverrides(state);
      consumeRelaxedMatches(state);
      addUnmatchedRealtimeItems(state);
      addMissingDbItems(state);

      const diffItems = sortDiffItems(dedupeDiffItems(state.diffItems));
      const byType = countBy(diffItems, item => item.type);

      return {
        summary: {
          dbCount: dbRows.length,
          realtimeCount: realtimeRows.length,
          diff: realtimeRows.length - dbRows.length,
          diffItemCount: diffItems.length,
          byType,
          overrideCount: realtimeOverrides.rows.length,
          manualOverrideCount: manualOverrides.rows.length,
          qualityIssueCount: Number(quality && quality.summary && quality.summary.issue_count || 0),
          ruleVersion: RULE_VERSION,
        },
        diffItems,
        sources: {
          runId,
          buildings,
          db: 'out/ac.db',
          realtimeFiles: realtimeDetails.files,
          qualityReport: quality ? path.relative(root, path.join(outDir, 'quality_report.json')) : '',
          algorithm: ALGORITHM_VERSION,
          generatedAt: new Date().toISOString(),
        },
        rules: ruleCatalog(),
      };
    } finally {
      db.close();
    }
  }

  function replay(runId, ruleVersion = RULE_VERSION, query = {}) {
    if (ruleVersion !== RULE_VERSION) {
      throw new Error(`Unsupported reconcile ruleVersion: ${ruleVersion}`);
    }
    const output = run({ ...query, run_id: runId || query.run_id || query.runId || '' });
    return {
      ...output,
      replay: {
        runId: output.sources.runId,
        requestedRunId: runId || null,
        ruleVersion,
        deterministic: true,
      },
    };
  }

  function audit(query = {}) {
    const output = run(query);
    const critical = output.diffItems.filter(item =>
      item.type === DIFF_TYPES.NEW_DEVICE ||
      item.type === DIFF_TYPES.MISSING_IN_REALTIME ||
      item.type === DIFF_TYPES.UNKNOWN ||
      item.confidence < 0.5
    );
    return {
      ok: critical.length === 0,
      summary: output.summary,
      drift: {
        absoluteDiff: Math.abs(output.summary.diff),
        diffItemCount: output.summary.diffItemCount,
        criticalCount: critical.length,
        byType: output.summary.byType,
      },
      criticalItems: critical,
      ruleVersion: RULE_VERSION,
    };
  }

  return { diff, run, replay, audit };
}

function selectedBuildings(value) {
  const raw = Array.isArray(value) ? value : String(value || '').split(',');
  const list = raw.map(v => String(v || '').trim()).filter(Boolean);
  const filtered = list.filter(b => BLDG_ORDER.includes(b));
  return filtered.length ? filtered : BLDG_ORDER;
}

function loadDbRows(db, runId, buildings) {
  const source = sourceForRun(runId);
  const params = [];
  const where = [];
  if (source.runWhere) {
    where.push(source.runWhere);
    params.push(...source.runParams);
  }
  if (buildings.length) {
    where.push(`sa.building IN (${buildings.map(() => '?').join(',')})`);
    params.push(...buildings);
  }
  const whereSql = where.length ? `WHERE ${where.join(' AND ')}` : '';
  const isRun = !!runId;
  const sql = isRun ? `
    SELECT c.id AS card_id, c.source_card_id AS source_card_id,
           sa.building, sa.floor, sa.floor_label AS stored_floor_label,
           sa.text AS sub_area, sa.x, sa.y,
           p.page_name, p.layout, p.raw_count, p.unique_count, p.duplicate_names,
           c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan,
           COALESCE(c.indicator, '') AS indicator,
           COALESCE(c.comm, '') AS comm
    FROM ${source.subAreas} sa
    JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
    ${whereSql}
    ORDER BY sa.building, sa.floor, sa.x, p.id, c.name
  ` : `
    SELECT c.id AS card_id, c.id AS source_card_id,
           sa.building, sa.floor, NULL AS stored_floor_label,
           sa.text AS sub_area, sa.x, sa.y,
           p.page_name, p.layout, p.raw_count, p.unique_count, p.duplicate_names,
           c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan,
           COALESCE(c.indicator, '') AS indicator,
           COALESCE(c.comm, '') AS comm
    FROM ${source.subAreas} sa
    JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
    ${whereSql}
    ORDER BY sa.building, sa.floor, sa.x, p.id, c.name
  `;
  return db.prepare(sql).all(...params).map(normalizeDbRow);
}

function normalizeDbRow(row) {
  const floorLabel = normalizeFloor(row.stored_floor_label || floorLabelFromValue(row.floor));
  const out = {
    source: 'db',
    card_id: row.card_id,
    source_card_id: row.source_card_id,
    building: clean(row.building),
    floor: row.floor,
    floor_label: floorLabel,
    sub_area: clean(row.sub_area),
    x: row.x,
    y: row.y,
    page_name: clean(row.page_name || 'default') || 'default',
    layout: clean(row.layout),
    raw_count: row.raw_count,
    unique_count: row.unique_count,
    duplicate_names: row.duplicate_names || '',
    name: clean(row.name),
    switch: row.switch,
    mode: row.mode,
    indoor: row.indoor,
    set_temp: row.set_temp,
    fan: row.fan,
    indicator: row.indicator,
    comm: row.comm,
  };
  out.key = deviceKey(out);
  out.exactIdentity = exactIdentity(out);
  out.nameFloorIdentity = nameFloorIdentity(out);
  out.nameIdentity = nameIdentity(out);
  return out;
}

function normalizeRealtimeRow(row) {
  const building = clean(row.source_building || row.building);
  const sourceName = clean(row.source_name || row.name);
  const floorLabel = displayFloorLabel(row.source_sub_area || row.subAreaText || row.sub_area, row.source_floor ?? row.floor);
  const out = {
    source: 'realtime',
    row_id: row.row_id,
    source_file: row.source_file,
    source_mtime: row.source_mtime,
    building,
    floor: row.source_floor ?? row.floor,
    floor_label: floorLabel,
    sub_area: clean(row.source_sub_area || row.subAreaText || row.sub_area),
    page_name: clean(row.source_page_name || row.pageName || row.page_name || 'default') || 'default',
    tab: clean(row.source_tab || row.tab),
    name: sourceName,
    dev_id: clean(row.dev_id || row.devId || row.meter_id || row.meterId),
    device_id: clean(row.device_id || row.deviceId),
    meter_id: clean(row.meter_id || row.meterId),
    rtu_id: clean(row.rtu_id || row.rtuId),
    field_count: Number(row.field_count ?? row.fieldCount ?? 0),
    realtime_tag_count: Number(row.realtime_tag_count ?? row.realtimeTagCount ?? 0),
    realtime_valid_tag_count: Number(row.realtime_valid_tag_count ?? row.realtimeValidTagCount ?? 0),
    default_like: !!(row.default_like || row.defaultLike),
    error: clean(row.error),
    card_comm: clean(row.card_comm || row.cardComm),
    card_switch: clean(row.card_switch || row.cardSwitch),
    card_indicator: clean(row.card_indicator || row.cardIndicator),
    fields: row.fields || {},
    valid_fields: row.valid_fields || row.validFields || {},
  };
  out.key = deviceKey(out);
  out.exactIdentity = exactIdentity(out);
  out.nameFloorIdentity = nameFloorIdentity(out);
  out.nameIdentity = nameIdentity(out);
  return out;
}

function loadRealtimeOverrides(db) {
  let rows = [];
  try {
    rows = db.prepare('SELECT * FROM realtime_match_overrides ORDER BY id').all();
  } catch {}
  const byDev = new Map();
  const byIdentity = new Map();
  for (const row of rows) {
    const normalized = {
      ...row,
      building: clean(row.building),
      dev_id: clean(row.dev_id),
      floor_label: normalizeFloor(row.floor_label),
      sub_area: clean(row.sub_area),
      page_name: clean(row.page_name || 'default') || 'default',
      realtime_name: clean(row.realtime_name),
      action: clean(row.action || 'classify_only') || 'classify_only',
    };
    if (normalized.dev_id) byDev.set(devOverrideKey(normalized.building, normalized.dev_id), normalized);
    else byIdentity.set(overrideIdentity(normalized), normalized);
  }
  return { rows, byDev, byIdentity };
}

function loadManualOverrides(db) {
  let rows = [];
  try {
    rows = db.prepare('SELECT * FROM manual_overrides ORDER BY id').all();
  } catch {}
  const byName = new Map();
  for (const row of rows) {
    const key = `${clean(row.building)}|${clean(row.card_name)}`;
    const list = byName.get(key) || [];
    list.push(row);
    byName.set(key, list);
  }
  return { rows, byName };
}

function loadQualityReport(outDir) {
  const file = path.join(outDir, 'quality_report.json');
  if (!fs.existsSync(file)) return null;
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}

function buildQualityIndex(report) {
  const duplicatePages = new Set();
  const duplicateNames = new Set();
  if (!report || !report.samples) return { duplicatePages, duplicateNames, report };
  for (const row of report.samples.duplicate_rendered_pages || []) {
    const pageKey = pageIdentity({
      building: row.building,
      floor_label: displayFloorLabel(row.sub_area, row.floor),
      sub_area: row.sub_area,
      page_name: row.page_name || 'default',
    });
    duplicatePages.add(pageKey);
    for (const item of parseDuplicateNames(row.duplicate_names)) {
      duplicateNames.add(`${pageKey}|${norm(item.name)}`);
    }
  }
  return { duplicatePages, duplicateNames, report };
}

function buildIndexes(dbRows, realtimeRows) {
  const dbById = new Map();
  const dbBySourceId = new Map();
  const dbByExact = new Map();
  const dbByNameFloor = new Map();
  const dbByName = new Map();
  const rtByExact = new Map();
  const rtByNameFloor = new Map();
  const rtByKey = new Map();
  for (const row of dbRows) {
    addIndex(dbById, String(row.card_id), row);
    addIndex(dbBySourceId, String(row.source_card_id), row);
    addIndex(dbByExact, row.exactIdentity, row);
    addIndex(dbByNameFloor, row.nameFloorIdentity, row);
    addIndex(dbByName, row.nameIdentity, row);
  }
  for (const row of realtimeRows) {
    addIndex(rtByExact, row.exactIdentity, row);
    addIndex(rtByNameFloor, row.nameFloorIdentity, row);
    addIndex(rtByKey, row.key, row);
  }
  return {
    dbById,
    dbBySourceId,
    dbByExact,
    dbByNameFloor,
    dbByName,
    rtByExact,
    rtByNameFloor,
    rtByKey,
  };
}

function consumeExactMatches(state) {
  const rtByExact = indexAvailable(state.realtimeRows, row => row.exactIdentity);
  for (const dbRow of state.dbRows) {
    const rt = takeAvailable(rtByExact, dbRow.exactIdentity, state.rtConsumed);
    if (!rt) continue;
    state.dbConsumed.add(dbRow.card_id);
    state.rtConsumed.add(rt.row_id);
  }
}

function applyRealtimeOverrides(state) {
  for (const rt of state.realtimeRows) {
    const override = findOverride(rt, state.realtimeOverrides);
    if (!override) continue;
    const manual = manualForRealtime(rt, state.manualOverrides);
    if (override.action === 'create_virtual') {
      state.rtConsumed.add(rt.row_id);
      state.diffItems.push(makeDiffItem({
        type: DIFF_TYPES.VIRTUAL_OVERRIDE,
        reason: '人工覆盖为虚拟纳管：实时详情存在有效点位，当前 SQLite 基线无实体卡片。',
        confidence: rtIsNoisy(rt) ? 'medium' : 'high',
        source: 'overrides',
        realtime: rt,
        db: null,
        override,
        manual,
        evidence: evidenceFor(rt, null, state.qualityIndex),
      }));
      continue;
    }
    if (override.action === 'ignore_duplicate') {
      state.rtConsumed.add(rt.row_id);
      state.diffItems.push(makeDiffItem({
        type: DIFF_TYPES.DUPLICATE_RENDER,
        reason: '人工覆盖为忽略重复实时行。',
        confidence: 'high',
        source: 'overrides',
        realtime: rt,
        db: null,
        override,
        manual,
        evidence: evidenceFor(rt, null, state.qualityIndex),
      }));
      continue;
    }
    if (override.action !== 'map_to_db') continue;

    const dbRow = resolveOverrideDbRow(override, rt, state);
    if (dbRow) {
      state.rtConsumed.add(rt.row_id);
      state.dbConsumed.add(dbRow.card_id);
    }
    const duplicate = dbRow && (state.dbConsumed.has(dbRow.card_id) || isDuplicateRender(rt, dbRow, state));
    const exactMatch = dbRow && rt.exactIdentity === dbRow.exactIdentity;
    const type = duplicate && exactMatch ? DIFF_TYPES.DUPLICATE_RENDER : DIFF_TYPES.MATCH_FAILED;
    const reason = type === DIFF_TYPES.DUPLICATE_RENDER
      ? '实时详情存在同名同页重复行，人工映射到现有 DB 卡片。'
      : '实时详情与 DB 的精确键未命中，由人工 override 映射到 DB 卡片。';
    state.diffItems.push(makeDiffItem({
      type,
      reason,
      confidence: dbRow ? 'high' : 'medium',
      source: 'overrides',
      realtime: rt,
      db: dbRow,
      override,
      manual,
      evidence: evidenceFor(rt, dbRow, state.qualityIndex),
    }));
  }
}

function consumeRelaxedMatches(state) {
  const rtByNameFloor = indexAvailable(state.realtimeRows, row => row.nameFloorIdentity);
  for (const dbRow of state.dbRows) {
    if (state.dbConsumed.has(dbRow.card_id)) continue;
    const rt = takeAvailable(rtByNameFloor, dbRow.nameFloorIdentity, state.rtConsumed);
    if (!rt) continue;
    state.dbConsumed.add(dbRow.card_id);
    state.rtConsumed.add(rt.row_id);
  }
}

function addUnmatchedRealtimeItems(state) {
  for (const rt of state.realtimeRows) {
    if (state.rtConsumed.has(rt.row_id)) continue;
    const dbRow = firstFromIndex(state.dbByNameFloor, rt.nameFloorIdentity) || firstFromIndex(state.dbByExact, rt.exactIdentity);
    const manual = manualForRealtime(rt, state.manualOverrides);
    const type = classifyUnmatchedRealtime(rt, dbRow, state);
    state.diffItems.push(makeDiffItem({
      type,
      reason: reasonForUnmatchedRealtime(type, rt, dbRow),
      confidence: confidenceFor(type, rt, dbRow),
      source: 'realtime',
      realtime: rt,
      db: dbRow,
      override: findOverride(rt, state.realtimeOverrides),
      manual,
      evidence: evidenceFor(rt, dbRow, state.qualityIndex),
    }));
  }
}

function addMissingDbItems(state) {
  for (const dbRow of state.dbRows) {
    if (state.dbConsumed.has(dbRow.card_id)) continue;
    const manual = manualForDb(dbRow, state.manualOverrides);
    state.diffItems.push(makeDiffItem({
      type: DIFF_TYPES.MISSING_IN_REALTIME,
      reason: 'DB 中存在该卡片，但最新实时详情文件没有可匹配设备行。',
      confidence: manual.length ? 'medium' : 'high',
      source: 'db',
      realtime: null,
      db: dbRow,
      override: null,
      manual,
      evidence: evidenceFor(null, dbRow, state.qualityIndex),
    }));
  }
}

function resolveOverrideDbRow(override, rt, state) {
  if (override.target_card_id) {
    const id = String(override.target_card_id);
    const byId = firstFromIndex(state.dbById, id) || firstFromIndex(state.dbBySourceId, id);
    if (byId) return byId;
  }
  const candidate = {
    building: override.building || rt.building,
    floor_label: normalizeFloor(override.floor_label || rt.floor_label),
    name: override.realtime_name || rt.name,
  };
  candidate.nameFloorIdentity = nameFloorIdentity(candidate);
  candidate.nameIdentity = nameIdentity(candidate);
  return uniqueFromIndex(state.dbByNameFloor, candidate.nameFloorIdentity) ||
    uniqueFromIndex(state.dbByName, candidate.nameIdentity) ||
    firstFromIndex(state.dbByNameFloor, candidate.nameFloorIdentity) ||
    firstFromIndex(state.dbByName, candidate.nameIdentity) ||
    null;
}

function classifyUnmatchedRealtime(rt, dbRow, state) {
  if (rtIsNoisy(rt)) return DIFF_TYPES.DATA_NOISE;
  if (isDuplicateRender(rt, dbRow, state)) return DIFF_TYPES.DUPLICATE_RENDER;
  if (dbRow) return DIFF_TYPES.MATCH_FAILED;
  return DIFF_TYPES.NEW_DEVICE;
}

function reasonForUnmatchedRealtime(type, rt, dbRow) {
  if (type === DIFF_TYPES.DATA_NOISE) return '实时详情行存在采集错误、默认模板或点位不完整，按噪声处理。';
  if (type === DIFF_TYPES.DUPLICATE_RENDER) return '实时详情存在重复 DOM/重复采集行，DB 已按唯一卡片保留。';
  if (type === DIFF_TYPES.MATCH_FAILED) {
    const dbPage = dbRow ? `${dbRow.sub_area}/${dbRow.page_name}` : '-';
    return `DB 存在同名同楼层卡片，但精确位置不一致；DB=${dbPage}，实时=${rt.sub_area}/${rt.page_name}。`;
  }
  if (type === DIFF_TYPES.NEW_DEVICE) return 'DB 中未找到同楼栋同楼层同名卡片，实时详情存在有效设备行。';
  return '无法归类的实时详情差异。';
}

function confidenceFor(type, rt, dbRow) {
  if (type === DIFF_TYPES.DATA_NOISE) return rt.error || rt.default_like ? 'high' : 'medium';
  if (type === DIFF_TYPES.DUPLICATE_RENDER) return 'high';
  if (type === DIFF_TYPES.MATCH_FAILED) return dbRow ? 'high' : 'medium';
  if (type === DIFF_TYPES.NEW_DEVICE) return rtIsNoisy(rt) ? 'low' : 'high';
  return 'low';
}

function makeDiffItem(input) {
  const rt = input.realtime;
  const db = input.db;
  const name = (rt && rt.name) || (db && db.name) || '';
  const building = (rt && rt.building) || (db && db.building) || '';
  const floorLabel = (rt && rt.floor_label) || (db && db.floor_label) || '';
  const key = (rt && rt.key) || (db && db.key) || deviceKey({ building, floor_label: floorLabel, name });
  const explanation = explainDiff({
    ...input,
    key,
    realtime: rt,
    db,
    evidenceBase: input.evidence || {},
  });
  return {
    key,
    displayKey: name,
    type: input.type || DIFF_TYPES.UNKNOWN,
    confidence: explanation.confidence,
    reason: explanation.reason,
    source: input.source || 'unknown',
    ruleVersion: explanation.ruleVersion,
    building,
    floor: floorLabel,
    name,
    db: db ? compactDb(db) : null,
    realtime: rt ? compactRealtime(rt) : null,
    override: input.override ? compactOverride(input.override) : null,
    manualOverrides: input.manual || [],
    evidence: explanation.evidence,
    decisionPath: explanation.decisionPath,
    rule: explanation.rule,
  };
}

function compactDb(row) {
  return {
    cardId: row.card_id,
    sourceCardId: row.source_card_id,
    building: row.building,
    floor: row.floor_label,
    subArea: row.sub_area,
    pageName: row.page_name,
    name: row.name,
    switch: row.switch,
    comm: row.comm,
  };
}

function compactRealtime(row) {
  return {
    rowId: row.row_id,
    sourceFile: row.source_file,
    building: row.building,
    floor: row.floor_label,
    subArea: row.sub_area,
    pageName: row.page_name,
    name: row.name,
    devId: row.dev_id,
    deviceId: row.device_id,
    fieldCount: row.field_count,
    realtimeTagCount: row.realtime_tag_count,
    realtimeValidTagCount: row.realtime_valid_tag_count,
    defaultLike: row.default_like,
    error: row.error,
  };
}

function compactOverride(row) {
  return {
    id: row.id,
    action: row.action,
    targetCardId: row.target_card_id,
    devId: row.dev_id,
    areaTypeOverride: row.area_type_override,
    note: row.note || '',
  };
}

function evidenceFor(rt, db, qualityIndex) {
  const pageKey = pageIdentity(rt || db || {});
  const name = norm((rt && rt.name) || (db && db.name));
  const duplicateByQuality = qualityIndex.duplicatePages.has(pageKey) ||
    qualityIndex.duplicateNames.has(`${pageKey}|${name}`);
  return {
    exactIdentity: rt ? rt.exactIdentity : db ? db.exactIdentity : '',
    nameFloorIdentity: rt ? rt.nameFloorIdentity : db ? db.nameFloorIdentity : '',
    dbExactIdentity: db ? db.exactIdentity : '',
    realtimeExactIdentity: rt ? rt.exactIdentity : '',
    qualityDuplicateRenderedPage: duplicateByQuality,
    dbDuplicateRenderedPage: db ? hasDbDuplicateMeta(db) : false,
    realtimeNoisy: rt ? rtIsNoisy(rt) : false,
  };
}

function rtIsNoisy(rt) {
  if (!rt) return false;
  if (rt.error) return true;
  if (rt.default_like) return true;
  if (rt.field_count > 0 && rt.field_count < 20) return true;
  if (rt.realtime_tag_count > 0 && !isRealtimePointsComplete({
    realtime_tag_count: rt.realtime_tag_count,
    realtime_valid_tag_count: rt.realtime_valid_tag_count,
  }) && rt.realtime_valid_tag_count === 0 && rt.card_comm !== '离线') return true;
  return isRealtimeDetailInvalid({
    error: rt.error,
    default_like: rt.default_like,
    fields: rt.fields,
    valid_fields: rt.valid_fields,
  });
}

function isDuplicateRender(rt, dbRow, state) {
  if (!rt) return false;
  const sameExact = state.realtimeRows.filter(row => row.exactIdentity === rt.exactIdentity);
  if (sameExact.length > 1) return true;
  const sameNameFloor = state.realtimeRows.filter(row => row.nameFloorIdentity === rt.nameFloorIdentity);
  const dbSameNameFloor = state.dbRows.filter(row => row.nameFloorIdentity === rt.nameFloorIdentity);
  if (sameNameFloor.length > Math.max(1, dbSameNameFloor.length)) return true;
  if (dbRow && hasDbDuplicateMeta(dbRow)) return true;
  const pageKey = pageIdentity(rt);
  return state.qualityIndex.duplicatePages.has(pageKey) ||
    state.qualityIndex.duplicateNames.has(`${pageKey}|${norm(rt.name)}`);
}

function hasDbDuplicateMeta(dbRow) {
  if (!dbRow) return false;
  const raw = Number(dbRow.raw_count || 0);
  const unique = Number(dbRow.unique_count || 0);
  if (raw > unique && unique > 0) return true;
  return parseDuplicateNames(dbRow.duplicate_names).some(item => norm(item.name) === norm(dbRow.name));
}

function findOverride(rt, overrides) {
  if (!rt || !overrides) return null;
  if (rt.dev_id) {
    const byDev = overrides.byDev.get(devOverrideKey(rt.building, rt.dev_id));
    if (byDev) return byDev;
  }
  return overrides.byIdentity.get(overrideIdentity({
    building: rt.building,
    floor_label: rt.floor_label,
    sub_area: rt.sub_area,
    page_name: rt.page_name,
    realtime_name: rt.name,
  })) || null;
}

function manualForRealtime(rt, manualOverrides) {
  if (!rt || !manualOverrides) return [];
  return manualOverrides.byName.get(`${rt.building}|${rt.name}`) || [];
}

function manualForDb(dbRow, manualOverrides) {
  if (!dbRow || !manualOverrides) return [];
  return manualOverrides.byName.get(`${dbRow.building}|${dbRow.name}`) || [];
}

function indexAvailable(rows, keyFn) {
  const map = new Map();
  for (const row of rows) addIndex(map, keyFn(row), row);
  return map;
}

function takeAvailable(index, key, consumed) {
  const rows = listFromIndex(index, key);
  return rows.find(row => !consumed.has(row.row_id)) || null;
}

function addIndex(map, key, row) {
  if (!key) return;
  const list = map.get(key) || [];
  list.push(row);
  map.set(key, list);
}

function firstFromIndex(map, key) {
  const rows = listFromIndex(map, key);
  return rows[0] || null;
}

function uniqueFromIndex(map, key) {
  const rows = listFromIndex(map, key);
  return rows.length === 1 ? rows[0] : null;
}

function listFromIndex(map, key) {
  if (!key || !map.has(key)) return [];
  const value = map.get(key);
  return Array.isArray(value) ? value : [value];
}

function dedupeDiffItems(items) {
  const seen = new Set();
  const out = [];
  for (const item of items) {
    const rowId = item.realtime && item.realtime.rowId ? item.realtime.rowId : '';
    const cardId = item.db && item.db.cardId ? item.db.cardId : '';
    const key = `${item.type}|${rowId}|${cardId}|${item.key}`;
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(item);
  }
  return out;
}

function sortDiffItems(items) {
  const rank = {
    [DIFF_TYPES.VIRTUAL_OVERRIDE]: 1,
    [DIFF_TYPES.NEW_DEVICE]: 2,
    [DIFF_TYPES.MISSING_IN_REALTIME]: 3,
    [DIFF_TYPES.MATCH_FAILED]: 4,
    [DIFF_TYPES.DUPLICATE_RENDER]: 5,
    [DIFF_TYPES.DATA_NOISE]: 6,
    [DIFF_TYPES.UNKNOWN]: 7,
  };
  return [...items].sort((a, b) =>
    (rank[a.type] || 99) - (rank[b.type] || 99) ||
    String(a.building).localeCompare(String(b.building), 'zh-CN', { numeric: true }) ||
    String(a.floor).localeCompare(String(b.floor), 'zh-CN', { numeric: true }) ||
    String(a.displayKey).localeCompare(String(b.displayKey), 'zh-CN', { numeric: true })
  );
}

function countBy(items, fn) {
  const out = {};
  for (const item of items) {
    const key = fn(item);
    out[key] = (out[key] || 0) + 1;
  }
  return out;
}

function deviceKey(row) {
  const devId = clean(row.dev_id || row.devId);
  if (devId) return `devId:${devId}`;
  const deviceId = clean(row.device_id || row.deviceId);
  if (deviceId) return `deviceId:${deviceId}`;
  return `hash:${sha1([row.building, normalizeFloor(row.floor_label || floorLabelFromValue(row.floor)), row.name].map(clean).join('|'))}`;
}

function exactIdentity(row) {
  return [
    norm(row.building),
    norm(canonicalFloorLabel(row)),
    norm(row.sub_area),
    norm(row.page_name || 'default'),
    norm(row.name),
  ].join('|');
}

function nameFloorIdentity(row) {
  return [
    norm(row.building),
    norm(canonicalFloorLabel(row)),
    norm(row.name),
  ].join('|');
}

function nameIdentity(row) {
  return [norm(row.building), norm(row.name)].join('|');
}

function pageIdentity(row) {
  return [
    norm(row.building),
    norm(canonicalFloorLabel(row)),
    norm(row.sub_area),
    norm(row.page_name || 'default'),
  ].join('|');
}

function devOverrideKey(building, devId) {
  return `${norm(building)}|${norm(devId)}`;
}

function overrideIdentity(row) {
  return [
    norm(row.building),
    norm(normalizeFloor(row.floor_label)),
    norm(row.sub_area),
    norm(row.page_name || 'default'),
    norm(row.realtime_name),
  ].join('|');
}

function displayFloorLabel(subArea, fallbackFloor) {
  const raw = clean(subArea).toUpperCase();
  if (raw === 'BM') return 'BM';
  const leading = raw.match(/^B\d+(?:\.\d+)?F\b|^\d+(?:\.\d+)?F\b/);
  if (leading) return normalizeFloor(leading[0]);
  const exact = raw.match(/\bB\d+(?:\.\d+)?F\b|\b\d+(?:\.\d+)?F\b/);
  if (exact) return normalizeFloor(exact[0]);
  return normalizeFloor(floorLabelFromValue(fallbackFloor));
}

function canonicalFloorLabel(row) {
  const name = norm(row && row.name);
  const subArea = norm(row && row.sub_area);
  const pageName = norm(row && row.page_name);
  const floor = normalizeFloor(row && (row.floor_label || floorLabelFromValue(row.floor)));
  if (name.startsWith('BM-') || subArea === 'BM' || pageName === 'BM') return 'BM';
  return floor;
}

function normalizeFloor(value) {
  const raw = clean(value);
  return raw ? normalizeFloorLabel(raw) : '';
}

function parseDuplicateNames(value) {
  if (!value) return [];
  if (Array.isArray(value)) return value;
  try {
    const parsed = JSON.parse(value);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function clean(value) {
  return String(value ?? '').trim();
}

function norm(value) {
  return clean(value).toUpperCase();
}

function sha1(value) {
  return crypto.createHash('sha1').update(String(value)).digest('hex').slice(0, 16);
}

module.exports = {
  createReconcileService,
  DIFF_TYPES,
  deviceKey,
};
