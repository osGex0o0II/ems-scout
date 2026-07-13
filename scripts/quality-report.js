#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const Database = require('better-sqlite3');
const { copyStableSqliteSnapshot } = require('./sqlite-snapshot');
const { BLDG_ORDER, BLDG_META } = require('../src/rules');
const { resolveRunId, sourceForRun } = require('../src/panel/history');

const ROOT = path.join(__dirname, '..');
const DB_PATH = process.env.EMS_DB_PATH || path.join(ROOT, 'out', 'ac.db');
const OUT_DIR = path.resolve(process.env.EMS_QUALITY_OUT || process.env.EMS_OUT_DIR || path.join(ROOT, 'out'));
const JSON_OUT = path.join(OUT_DIR, 'quality_report.json');
const TXT_OUT = path.join(OUT_DIR, 'quality_report.txt');
const KNOWN_FINDINGS_PATH = path.resolve(process.env.EMS_QUALITY_KNOWN_FINDINGS || path.join(ROOT, 'config', 'quality-known-findings.json'));
const NON_BLOCKING_KNOWN_STATUSES = new Set([
  'accepted_ems_source_defect',
  'accepted_source_state',
  'accepted_long_offline',
]);

function getXlsxVersion() {
  try {
    return require('xlsx').version || '';
  } catch {
    return '';
  }
}

function versionLt(a, b) {
  const pa = String(a || '0').split('.').map(n => parseInt(n, 10) || 0);
  const pb = String(b || '0').split('.').map(n => parseInt(n, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const da = pa[i] || 0;
    const db = pb[i] || 0;
    if (da !== db) return da < db;
  }
  return false;
}

function nowLocal() {
  const d = new Date();
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

function rows(db, sql, params = []) {
  return db.prepare(sql).all(...params);
}

function hasColumn(db, table, column) {
  return db.pragma(`table_info(${table})`).some(item => item.name === column);
}

function safeJsonArray(value) {
  try {
    const parsed = JSON.parse(value || '[]');
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function loadKnownFindings() {
  if (!fs.existsSync(KNOWN_FINDINGS_PATH)) return [];
  const parsed = JSON.parse(fs.readFileSync(KNOWN_FINDINGS_PATH, 'utf8'));
  return Array.isArray(parsed.findings) ? parsed.findings : [];
}

function sameText(a, b) {
  if (b === undefined || b === null || b === '') return true;
  return String(a ?? '') === String(b);
}

function sameNumber(a, b) {
  if (b === undefined || b === null || b === '') return true;
  return Number(a) === Number(b);
}

function pageMatchesFinding(row, finding) {
  return sameText(row.building, finding.building) &&
    sameNumber(row.floor, finding.floor) &&
    sameText(row.sub_area, finding.subArea) &&
    sameText(row.page_name, finding.page) &&
    sameNumber(row.x, finding.x) &&
    sameNumber(row.y, finding.y);
}

function findingMatchesIssue(row, issueCode, finding) {
  if (!finding || !pageMatchesFinding(row, finding)) return false;
  if ((issueCode === 'offline_template_stable' || issueCode === 'offline_template_without_stability') &&
      finding.type === 'offline_template_page') {
    return true;
  }
  if (issueCode === 'invalid_card_fields' && finding.type === 'device_invalid_fields') {
    return sameText(row.name, finding.device);
  }
  if (issueCode === 'active_field_incomplete_pages' && finding.type === 'device_invalid_fields') {
    return true;
  }
  if ((issueCode === 'unknown_comm' || issueCode === 'missing_indicator') && finding.type === 'device_missing_indicator') {
    return sameText(row.name, finding.device);
  }
  return false;
}

function matchingKnownFindings(row, issueCode, findings) {
  return findings.filter(finding => findingMatchesIssue(row, issueCode, finding));
}

function splitKnownRows(rows, issueCode, findings) {
  const blocking = [];
  const nonBlocking = [];
  const known = [];
  for (const row of rows) {
    const matches = matchingKnownFindings(row, issueCode, findings);
    if (!matches.length) {
      blocking.push(row);
      continue;
    }
    known.push({
      issue_code: issueCode,
      row,
      findings: matches.map(finding => ({
        id: finding.id,
        type: finding.type,
        status: finding.status,
        reason: finding.reason,
        evidence: finding.evidence || [],
      })),
    });
    if (matches.some(finding => NON_BLOCKING_KNOWN_STATUSES.has(finding.status))) {
      nonBlocking.push(row);
    } else {
      blocking.push(row);
    }
  }
  return { blocking, nonBlocking, known };
}

function qualityOutPaths(runId) {
  if (!runId) return { jsonOut: JSON_OUT, txtOut: TXT_OUT };
  return {
    jsonOut: path.join(OUT_DIR, `quality_report_run${runId}.json`),
    txtOut: path.join(OUT_DIR, `quality_report_run${runId}.txt`),
  };
}

function resolveQualityRunArg(raw) {
  return raw || '';
}

function buildReport(options = {}) {
  if (!fs.existsSync(DB_PATH)) {
    throw new Error('Database not found: ' + DB_PATH);
  }
  const knownFindings = loadKnownFindings();
  const snapshot = copyStableSqliteSnapshot(DB_PATH);
  let db;
  try {
  db = new Database(snapshot.path, { fileMustExist: true });
  db.pragma('query_only = ON');
  const requestedRun = options.runId || options.run_id;
  const latestRun = requestedRun === 'latest-run' || requestedRun === 'latest-imported'
    ? db.prepare('SELECT id FROM collection_runs ORDER BY datetime(completed_at) DESC, id DESC LIMIT 1').get()
    : null;
  const runId = resolveRunId(db, latestRun ? latestRun.id : requestedRun);
  const source = sourceForRun(runId);
  const qualityReason = hasColumn(db, source.pages, 'quality_reason')
    ? "COALESCE(p.quality_reason, '')"
    : "''";
  const runWhere = source.runWhere ? `WHERE ${source.runWhere}` : '';
  const andRunWhere = source.runWhere ? `AND ${source.runWhere}` : '';
  const run = runId ? db.prepare('SELECT id, run_key, completed_at, imported_at, scope, buildings FROM collection_runs WHERE id = ?').get(runId) : null;
  const cardRows = rows(db, `
    SELECT sa.building, sa.floor, sa.text AS sub_area, sa.x, sa.y,
           p.page_name, p.layout, ${qualityReason} AS quality_reason,
           c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan,
           COALESCE(c.indicator, '') AS indicator,
           c.comm
    FROM ${source.subAreas} sa
    JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
    ${runWhere}
    ORDER BY sa.building, sa.floor, sa.x, p.id, c.name
  `, source.runParams);

  const buildings = rows(db, `
    SELECT sa.building,
           COUNT(*) AS cards,
           SUM(c.switch = 'ON') AS switch_on,
           SUM(c.switch = 'OFF') AS switch_off,
           SUM(c.switch = '-') AS switch_unknown,
           SUM(c.comm = '开机') AS comm_on,
           SUM(c.comm = '关机') AS comm_off,
           SUM(c.comm = '离线') AS comm_offline
    FROM ${source.subAreas} sa
    JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
    ${runWhere}
    GROUP BY sa.building
  `, source.runParams);
  const subAreaCounts = rows(db, `
    SELECT building, COUNT(*) AS sub_areas
    FROM ${source.subAreas} sa
    ${runWhere}
    GROUP BY building
  `, source.runParams);

  const byBuilding = {};
  for (const row of buildings) byBuilding[row.building] = row;
  for (const row of subAreaCounts) {
    if (!byBuilding[row.building]) byBuilding[row.building] = { building: row.building, cards: 0 };
    byBuilding[row.building].sub_areas = row.sub_areas;
  }

  const placeholderCards = cardRows.filter(r => !r.name || r.name === '0-0001-KT');
  const inconsistentState = cardRows.filter(r =>
    (r.comm === '开机' && r.switch !== 'ON') ||
    (r.comm === '关机' && r.switch !== 'OFF') ||
    (r.comm === '离线' && r.switch !== '-')
  );
  const unknownComm = cardRows.filter(r => !r.comm);
  const missingIndicator = cardRows.filter(r => !r.indicator && r.comm !== '离线');
  const unknownSwitch = cardRows.filter(r => r.switch !== 'ON' && r.switch !== 'OFF' && r.switch !== '-');
  const duplicateCardsSamePage = rows(db, `
    SELECT sa.building, sa.floor, sa.text AS sub_area, p.page_name, c.name, COUNT(*) AS copies
    FROM ${source.subAreas} sa
    JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
    WHERE c.name IS NOT NULL AND c.name <> ''
      ${andRunWhere}
    GROUP BY sa.id, p.id, c.name
    HAVING copies > 1
    ORDER BY copies DESC, sa.building, sa.floor, sa.x, p.page_name, c.name
  `, source.runParams);
  const duplicateRenderedPages = rows(db, `
    SELECT sa.building, sa.floor, sa.text AS sub_area, sa.x, sa.y,
           p.page_name, p.count, p.raw_count, p.unique_count, p.duplicate_names
    FROM ${source.subAreas} sa
    JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    WHERE COALESCE(p.raw_count, p.count) > COALESCE(p.unique_count, p.count)
      ${andRunWhere}
    ORDER BY sa.building, sa.floor, sa.x, p.page_name
  `, source.runParams);
  const pageCountMismatches = rows(db, `
    SELECT sa.building, sa.floor, sa.text AS sub_area, p.page_name,
           p.count, p.raw_count, p.unique_count, COUNT(c.id) AS actual_count
    FROM ${source.subAreas} sa
    JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    LEFT JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
    ${runWhere}
    GROUP BY p.id
    HAVING COALESCE(p.count, -1) <> actual_count
       OR COALESCE(p.unique_count, -1) <> actual_count
       OR COALESCE(p.raw_count, -1) < actual_count
    ORDER BY sa.building, sa.floor, sa.x, p.page_name
  `, source.runParams);
  const emptySubAreas = rows(db, `
    SELECT sa.building, sa.floor, sa.text AS sub_area, sa.sub_idx, sa.x, sa.y,
           COUNT(p.id) AS pages
    FROM ${source.subAreas} sa
    LEFT JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    ${runWhere}
    GROUP BY sa.id
    HAVING pages = 0
    ORDER BY sa.building, sa.sub_idx
  `, source.runParams);
  const inlineSubAreas = emptySubAreas.filter(r => r.building === '6号' && r.floor === -2 && r.sub_area === 'BM');
  const emptyNonInlineSubAreas = emptySubAreas.filter(r => !(r.building === '6号' && r.floor === -2 && r.sub_area === 'BM'));
  const suspiciousUniformPages = rows(db, `
    SELECT sa.building, sa.floor, sa.text AS sub_area, p.page_name, p.layout, ${qualityReason} AS quality_reason,
           COUNT(*) AS cards,
           COUNT(DISTINCT c.name) AS names,
           COUNT(DISTINCT c.indoor) AS indoor_vals,
           COUNT(DISTINCT c.set_temp) AS set_vals,
           COUNT(DISTINCT c.fan) AS fan_vals,
           COUNT(DISTINCT c.mode) AS mode_vals,
           SUM(c.name = '0-0001-KT') AS placeholders,
           SUM(c.comm IS NOT NULL AND c.comm <> '') AS with_comm,
           SUM(c.switch IN ('ON', 'OFF') OR c.comm = '离线') AS with_resolved_state
    FROM ${source.subAreas} sa
    JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
    ${runWhere}
    GROUP BY sa.id, p.id
    HAVING cards >= 3
       AND indoor_vals <= 1
       AND set_vals <= 1
       AND fan_vals <= 1
       AND mode_vals <= 1
       AND (placeholders > 0 OR with_comm < cards OR with_resolved_state < cards)
    ORDER BY sa.building, sa.floor, sa.x, p.id
  `, source.runParams);
  const uniformResolvedPages = rows(db, `
    SELECT sa.building, sa.floor, sa.text AS sub_area, sa.x, sa.y, p.page_name, p.layout, ${qualityReason} AS quality_reason,
           COUNT(*) AS cards,
           MIN(c.indoor) AS indoor,
           MIN(c.set_temp) AS set_temp,
           MIN(c.fan) AS fan,
           MIN(c.mode) AS mode,
           SUM(c.comm = '开机') AS comm_on,
           SUM(c.comm = '关机') AS comm_off,
           SUM(c.comm = '离线') AS comm_offline
    FROM ${source.subAreas} sa
    JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
    ${runWhere}
    GROUP BY sa.id, p.id
    HAVING cards >= 2
       AND COUNT(DISTINCT c.indoor) <= 1
       AND COUNT(DISTINCT c.set_temp) <= 1
       AND COUNT(DISTINCT c.fan) <= 1
       AND COUNT(DISTINCT c.mode) <= 1
       AND (comm_on + comm_off + comm_offline) = cards
    ORDER BY sa.building, sa.floor, sa.x, p.id
  `, source.runParams);
  const unresolvedOfflineTemplatePages = uniformResolvedPages.filter(r =>
    Number(r.comm_offline || 0) === Number(r.cards || 0) &&
    String(r.indoor) === '0' &&
    String(r.set_temp) === '0' &&
    (String(r.fan) === '0' || String(r.fan) === '中') &&
    String(r.quality_reason || '') !== 'offline_template_stable'
  );
  const stableOfflineTemplatePages = uniformResolvedPages.filter(r =>
    Number(r.comm_offline || 0) === Number(r.cards || 0) &&
    String(r.indoor) === '0' &&
    String(r.set_temp) === '0' &&
    (String(r.fan) === '0' || String(r.fan) === '中') &&
    String(r.quality_reason || '') === 'offline_template_stable'
  );
  const invalidFieldCards = cardRows.filter(r => {
    const indoor = parseFloat(r.indoor);
    const setTemp = parseFloat(r.set_temp);
    const invalidIndoor = Number.isFinite(indoor) && (indoor < 0 || indoor > 60);
    const invalidSetTemp = Number.isFinite(setTemp) && setTemp !== 0 && (setTemp < 5 || setTemp > 40);
    const active = r.comm === '开机' || r.comm === '关机';
    const missingActiveFields = active && (
      r.switch !== 'ON' && r.switch !== 'OFF' ||
      !r.mode || r.mode === '-' ||
      !r.fan || r.fan === '-' || r.fan === '0' ||
      !Number.isFinite(indoor) || indoor <= 0 || indoor > 60 ||
      !Number.isFinite(setTemp) || setTemp < 5 || setTemp > 40
    );
    return invalidIndoor || invalidSetTemp || missingActiveFields;
  });
  const lowActiveFieldPages = rows(db, `
    SELECT sa.building, sa.floor, sa.text AS sub_area, sa.x, sa.y,
           p.page_name, p.layout, ${qualityReason} AS quality_reason,
           COUNT(*) AS cards,
           SUM(c.comm = '开机' OR c.comm = '关机') AS active_cards,
           SUM((c.comm = '开机' OR c.comm = '关机') AND c.switch IN ('ON', 'OFF')) AS active_switch,
           SUM((c.comm = '开机' OR c.comm = '关机') AND c.mode IS NOT NULL AND c.mode <> '-') AS active_mode,
           SUM((c.comm = '开机' OR c.comm = '关机') AND c.fan IS NOT NULL AND c.fan <> '-' AND c.fan <> '0') AS active_fan,
           SUM((c.comm = '开机' OR c.comm = '关机') AND CAST(c.indoor AS REAL) > 0 AND CAST(c.indoor AS REAL) <= 60) AS active_indoor,
           SUM((c.comm = '开机' OR c.comm = '关机') AND CAST(c.set_temp AS REAL) >= 5 AND CAST(c.set_temp AS REAL) <= 40) AS active_set_temp
    FROM ${source.subAreas} sa
    JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
    ${runWhere}
    GROUP BY sa.id, p.id
    HAVING active_cards > 0
       AND (
         active_switch < active_cards OR
         active_mode < active_cards OR
         active_fan < active_cards OR
         active_indoor < active_cards OR
         active_set_temp < active_cards
       )
    ORDER BY sa.building, sa.floor, sa.x, p.id
  `, source.runParams);

  const runBuildings = run ? safeJsonArray(run.buildings).filter(building => BLDG_META[building]) : [];
  const baselineBuildings = runBuildings.length ? runBuildings : BLDG_ORDER;
  const buildingSummary = baselineBuildings.map(building => {
    const meta = BLDG_META[building];
    const actual = byBuilding[building] || { cards: 0, sub_areas: 0, switch_on: 0, switch_off: 0, switch_unknown: 0, comm_on: 0, comm_off: 0, comm_offline: 0 };
    return {
      building,
      name: meta.name,
      cards: actual.cards,
      baseline_cards: meta.baselineCards,
      card_delta: actual.cards - meta.baselineCards,
      sub_areas: actual.sub_areas,
      baseline_sub_areas: meta.baselineSubAreas,
      sub_area_delta: actual.sub_areas - meta.baselineSubAreas,
      switch_on: actual.switch_on || 0,
      switch_off: actual.switch_off || 0,
      switch_unknown: actual.switch_unknown || 0,
      comm_on: actual.comm_on || 0,
      comm_off: actual.comm_off || 0,
      comm_offline: actual.comm_offline || 0,
    };
  });

  const issues = [];
  if (placeholderCards.length) issues.push({ severity: 'P1', code: 'placeholder_names', count: placeholderCards.length, message: '存在 0-0001-KT 或空卡名，说明页面未完全加载即入库。' });
  if (inconsistentState.length) issues.push({ severity: 'P1', code: 'state_mismatch', count: inconsistentState.length, message: 'comm 与 switch 不一致。' });
  const baselineMisses = buildingSummary.filter(b => b.card_delta !== 0 || b.sub_area_delta !== 0);
  if (baselineMisses.length) issues.push({ severity: 'P2', code: 'baseline_delta', count: baselineMisses.length, message: '楼栋卡数或子区数与基准不一致。' });
  if (unknownSwitch.length) issues.push({ severity: 'P2', code: 'unknown_switch', count: unknownSwitch.length, message: '存在非 ON/OFF/- 的开关状态。' });
  if (duplicateCardsSamePage.length) issues.push({ severity: 'P2', code: 'duplicate_cards_same_page', count: duplicateCardsSamePage.length, message: '同一页面存在重复卡名。' });
  if (pageCountMismatches.length) issues.push({ severity: 'P1', code: 'page_count_mismatch', count: pageCountMismatches.length, message: '页面 count/raw_count/unique_count 与实际卡片数不一致。' });
  if (emptyNonInlineSubAreas.length) issues.push({ severity: 'P2', code: 'empty_sub_areas', count: emptyNonInlineSubAreas.length, message: '存在无页面/无卡片的空子区。' });
  if (suspiciousUniformPages.length) issues.push({ severity: 'P2', code: 'suspicious_uniform_pages', count: suspiciousUniformPages.length, message: '存在统一默认值且未完整加载通讯/开关的页面。' });
  const knownBuckets = [
    splitKnownRows(unknownComm, 'unknown_comm', knownFindings),
    splitKnownRows(missingIndicator, 'missing_indicator', knownFindings),
    splitKnownRows(unresolvedOfflineTemplatePages, 'offline_template_without_stability', knownFindings),
    splitKnownRows(stableOfflineTemplatePages, 'offline_template_stable', knownFindings),
    splitKnownRows(invalidFieldCards, 'invalid_card_fields', knownFindings),
    splitKnownRows(lowActiveFieldPages, 'active_field_incomplete_pages', knownFindings),
  ];
  const knownIssueAnnotations = knownBuckets.flatMap(bucket => bucket.known);
  const unknownCommBlocking = knownBuckets[0].blocking;
  const missingIndicatorBlocking = knownBuckets[1].blocking;
  const unresolvedOfflineTemplateBlocking = knownBuckets[2].blocking;
  const stableOfflineTemplateBlocking = knownBuckets[3].blocking;
  const invalidFieldBlocking = knownBuckets[4].blocking;
  const lowActiveFieldBlocking = knownBuckets[5].blocking;
  if (unknownCommBlocking.length) issues.push({ severity: 'P2', code: 'unknown_comm', count: unknownCommBlocking.length, message: '存在未知通讯状态。' });
  if (missingIndicatorBlocking.length) issues.push({ severity: 'P2', code: 'missing_indicator', count: missingIndicatorBlocking.length, message: '存在非离线卡缺少 indicator 原图。' });
  if (unresolvedOfflineTemplateBlocking.length) issues.push({ severity: 'P2', code: 'offline_template_without_stability', count: unresolvedOfflineTemplateBlocking.length, message: '存在全离线默认模板页，但缺少采集稳定窗口证据。' });
  if (stableOfflineTemplateBlocking.length) issues.push({ severity: 'P2', code: 'offline_template_stable', count: stableOfflineTemplateBlocking.length, message: '存在全离线默认模板页；虽已观察到稳定窗口，仍需人工复核 EMS 是否真实全离线。' });
  if (invalidFieldBlocking.length) issues.push({ severity: 'P1', code: 'invalid_card_fields', count: invalidFieldBlocking.length, message: '存在异常温度或开机/关机设备字段缺失。' });
  if (lowActiveFieldBlocking.length) issues.push({ severity: 'P1', code: 'active_field_incomplete_pages', count: lowActiveFieldBlocking.length, message: '存在开机/关机设备字段不完整的页面。' });
  if (knownIssueAnnotations.length) issues.push({ severity: 'INFO', code: 'known_findings', count: knownIssueAnnotations.length, message: '存在已登记的质量发现；未接受状态仍会阻断通过。' });
  if (duplicateRenderedPages.length) issues.push({ severity: 'INFO', code: 'duplicate_rendered_pages', count: duplicateRenderedPages.length, message: '存在 EMS 同页重复渲染卡，入库已按卡名去重。' });
  if (uniformResolvedPages.length) issues.push({ severity: 'INFO', code: 'uniform_resolved_pages', count: uniformResolvedPages.length, message: '存在字段完全统一但状态完整的页面，通常为全离线或模板式真实页。' });
  if (inlineSubAreas.length) issues.push({ severity: 'INFO', code: 'inline_sub_area', count: inlineSubAreas.length, message: '6号 BM 通过 A座 1F 的 BM page 采集，空 BM 子区为占位记录。' });
  const xlsxVersion = getXlsxVersion();
  if (!xlsxVersion || versionLt(xlsxVersion, '0.20.2')) {
    issues.push({ severity: 'INFO', code: 'xlsx_advisory', count: 1, message: `xlsx${xlsxVersion ? '@' + xlsxVersion : ''} 存在公开 high advisories；当前项目主要导出 Excel，避免读取不可信 xlsx。` });
  }

  return {
    generated_at: new Date().toISOString(),
    generated_at_local: nowLocal(),
    db_path: DB_PATH,
    run_id: runId,
    run: run ? {
      id: run.id,
      run_key: run.run_key,
      completed_at: run.completed_at,
      imported_at: run.imported_at,
      scope: run.scope,
      buildings: safeJsonArray(run.buildings),
    } : null,
    summary: {
      total_cards: cardRows.length,
      issue_count: issues.filter(i => i.severity !== 'INFO').length,
      known_findings: knownIssueAnnotations.length,
      placeholder_cards: placeholderCards.length,
      state_mismatch: inconsistentState.length,
      unknown_comm: unknownComm.length,
      missing_indicator: missingIndicator.length,
      unknown_switch: unknownSwitch.length,
      duplicate_cards_same_page: duplicateCardsSamePage.length,
      duplicate_rendered_pages: duplicateRenderedPages.length,
      page_count_mismatch: pageCountMismatches.length,
      empty_sub_areas: emptyNonInlineSubAreas.length,
      inline_sub_areas: inlineSubAreas.length,
      suspicious_uniform_pages: suspiciousUniformPages.length,
      offline_template_without_stability: unresolvedOfflineTemplateBlocking.length,
      offline_template_stable: stableOfflineTemplateBlocking.length,
      invalid_card_fields: invalidFieldBlocking.length,
      active_field_incomplete_pages: lowActiveFieldBlocking.length,
      uniform_resolved_pages: uniformResolvedPages.length,
    },
    buildings: buildingSummary,
    issues,
    samples: {
      placeholder_cards: placeholderCards.slice(0, 50),
      inconsistent_state: inconsistentState.slice(0, 50),
      unknown_comm: unknownComm.slice(0, 50),
      missing_indicator: missingIndicator.slice(0, 50),
      unknown_switch: unknownSwitch.slice(0, 50),
      duplicate_cards_same_page: duplicateCardsSamePage.slice(0, 50),
      duplicate_rendered_pages: duplicateRenderedPages.slice(0, 50),
      page_count_mismatch: pageCountMismatches.slice(0, 50),
      empty_sub_areas: emptyNonInlineSubAreas.slice(0, 50),
      inline_sub_areas: inlineSubAreas.slice(0, 50),
      suspicious_uniform_pages: suspiciousUniformPages.slice(0, 50),
      offline_template_without_stability: unresolvedOfflineTemplatePages.slice(0, 50),
      offline_template_stable: stableOfflineTemplatePages.slice(0, 50),
      invalid_card_fields: invalidFieldCards.slice(0, 50),
      active_field_incomplete_pages: lowActiveFieldPages.slice(0, 50),
      known_findings: knownIssueAnnotations.slice(0, 50),
      uniform_resolved_pages: uniformResolvedPages.slice(0, 50),
    },
  };
  } finally {
    try {
      db?.close();
    } finally {
      fs.rmSync(snapshot.directory, { recursive: true, force: true });
    }
  }
}

function renderText(report) {
  const lines = [];
  lines.push('EMS 采集质量审计报告');
  lines.push('生成时间: ' + report.generated_at_local);
  if (report.run_id) {
    lines.push(`数据批次: run ${report.run_id} ${report.run ? report.run.completed_at : ''}`);
  } else {
    lines.push('数据批次: 最新数据');
  }
  lines.push('数据库: ' + report.db_path);
  lines.push('');
  lines.push(`总卡数: ${report.summary.total_cards}`);
  lines.push(`问题项: ${report.summary.issue_count}`);
  lines.push(`已登记质量发现: ${report.summary.known_findings}`);
  lines.push(`占位符卡名: ${report.summary.placeholder_cards}`);
  lines.push(`状态不一致: ${report.summary.state_mismatch}`);
  lines.push(`未知通讯: ${report.summary.unknown_comm}`);
  lines.push(`缺失 indicator: ${report.summary.missing_indicator}`);
  lines.push(`未知开关: ${report.summary.unknown_switch}`);
  lines.push(`同页重复卡: ${report.summary.duplicate_cards_same_page}`);
  lines.push(`重复渲染页: ${report.summary.duplicate_rendered_pages}`);
  lines.push(`空子区: ${report.summary.empty_sub_areas}`);
  lines.push(`Inline 子区: ${report.summary.inline_sub_areas}`);
  lines.push(`疑似默认页: ${report.summary.suspicious_uniform_pages}`);
  lines.push(`全离线模板无稳定证据: ${report.summary.offline_template_without_stability}`);
  lines.push(`全离线模板已稳定: ${report.summary.offline_template_stable}`);
  lines.push(`异常/缺失卡字段: ${report.summary.invalid_card_fields}`);
  lines.push(`开关机页字段不完整: ${report.summary.active_field_incomplete_pages}`);
  lines.push(`统一完整页: ${report.summary.uniform_resolved_pages}`);
  lines.push('');
  lines.push('楼栋基准对比');
  for (const b of report.buildings) {
    const cardDelta = b.card_delta === 0 ? 'OK' : (b.card_delta > 0 ? `+${b.card_delta}` : String(b.card_delta));
    const saDelta = b.sub_area_delta === 0 ? 'OK' : (b.sub_area_delta > 0 ? `+${b.sub_area_delta}` : String(b.sub_area_delta));
    lines.push(`  ${b.building} ${b.name}: ${b.cards}/${b.baseline_cards} 卡 (${cardDelta}), ${b.sub_areas}/${b.baseline_sub_areas} 子区 (${saDelta})`);
  }
  lines.push('');
  lines.push('问题摘要');
  for (const issue of report.issues) {
    lines.push(`  [${issue.severity}] ${issue.code}: ${issue.count} - ${issue.message}`);
  }
  lines.push('');
  if (report.samples.placeholder_cards.length) {
    lines.push('占位符样例');
    for (const r of report.samples.placeholder_cards.slice(0, 20)) {
      lines.push(`  ${r.building} F${r.floor} ${r.sub_area} ${r.page_name} ${r.name} ${r.comm || '-'}`);
    }
  }
  if (report.samples.suspicious_uniform_pages.length) {
    lines.push('');
    lines.push('疑似默认页样例');
    for (const r of report.samples.suspicious_uniform_pages.slice(0, 20)) {
      lines.push(`  ${r.building} F${r.floor} ${r.sub_area} ${r.page_name}: cards=${r.cards} ph=${r.placeholders} comm=${r.with_comm}/${r.cards} state=${r.with_resolved_state}/${r.cards}`);
    }
  }
  if (report.samples.offline_template_without_stability.length) {
    lines.push('');
    lines.push('全离线模板无稳定证据样例');
    for (const r of report.samples.offline_template_without_stability.slice(0, 20)) {
      lines.push(`  ${r.building} F${r.floor} ${r.sub_area} ${r.page_name}: cards=${r.cards} ${r.indoor}/${r.set_temp}/${r.fan}/${r.mode} reason=${r.quality_reason || '-'}`);
    }
  }
  if (report.samples.offline_template_stable.length) {
    lines.push('');
    lines.push('全离线模板样例');
    for (const r of report.samples.offline_template_stable.slice(0, 20)) {
      lines.push(`  ${r.building} F${r.floor} ${r.sub_area} ${r.page_name}: cards=${r.cards} ${r.indoor}/${r.set_temp}/${r.fan}/${r.mode}`);
    }
  }
  if (report.samples.invalid_card_fields.length) {
    lines.push('');
    lines.push('异常/缺失卡字段样例');
    for (const r of report.samples.invalid_card_fields.slice(0, 20)) {
      lines.push(`  ${r.building} F${r.floor} ${r.sub_area} ${r.page_name} ${r.name}: switch=${r.switch} comm=${r.comm || '-'} indoor=${r.indoor} set=${r.set_temp} mode=${r.mode} fan=${r.fan}`);
    }
  }
  if (report.samples.active_field_incomplete_pages.length) {
    lines.push('');
    lines.push('开关机页字段不完整样例');
    for (const r of report.samples.active_field_incomplete_pages.slice(0, 20)) {
      lines.push(`  ${r.building} F${r.floor} ${r.sub_area} ${r.page_name}: active=${r.active_cards} sw=${r.active_switch} mode=${r.active_mode} fan=${r.active_fan} indoor=${r.active_indoor} set=${r.active_set_temp} reason=${r.quality_reason || '-'}`);
    }
  }
  if (report.samples.known_findings.length) {
    lines.push('');
    lines.push('已登记质量发现');
    for (const item of report.samples.known_findings.slice(0, 20)) {
      const r = item.row || {};
      const findingText = (item.findings || []).map(f => `${f.id}:${f.status}`).join(', ');
      lines.push(`  ${item.issue_code} ${r.building || '-'} F${r.floor ?? '-'} ${r.sub_area || '-'} ${r.page_name || '-'} ${r.name || ''}: ${findingText}`);
    }
  }
  if (report.samples.missing_indicator.length) {
    lines.push('');
    lines.push('缺失 indicator 样例');
    for (const r of report.samples.missing_indicator.slice(0, 20)) {
      lines.push(`  ${r.building} F${r.floor} ${r.sub_area} ${r.page_name} ${r.name} switch=${r.switch} comm=${r.comm || '-'}`);
    }
  }
  if (report.samples.duplicate_rendered_pages.length) {
    lines.push('');
    lines.push('重复渲染页样例');
    for (const r of report.samples.duplicate_rendered_pages.slice(0, 20)) {
      lines.push(`  ${r.building} F${r.floor} ${r.sub_area} ${r.page_name}: raw=${r.raw_count} unique=${r.unique_count} dup=${r.duplicate_names || '-'}`);
    }
  }
  if (report.samples.uniform_resolved_pages.length) {
    lines.push('');
    lines.push('统一完整页样例');
    for (const r of report.samples.uniform_resolved_pages.slice(0, 20)) {
      lines.push(`  ${r.building} F${r.floor} ${r.sub_area} ${r.page_name}: cards=${r.cards} ${r.indoor}/${r.set_temp}/${r.fan}/${r.mode} on=${r.comm_on} off=${r.comm_off} offline=${r.comm_offline}`);
    }
  }
  if (report.samples.duplicate_cards_same_page.length) {
    lines.push('');
    lines.push('同页重复卡样例');
    for (const r of report.samples.duplicate_cards_same_page.slice(0, 20)) {
      lines.push(`  ${r.building} F${r.floor} ${r.sub_area} ${r.page_name}: ${r.name} x${r.copies}`);
    }
  }
  if (report.samples.empty_sub_areas.length) {
    lines.push('');
    lines.push('空子区样例');
    for (const r of report.samples.empty_sub_areas.slice(0, 20)) {
      lines.push(`  ${r.building} F${r.floor} ${r.sub_area} idx=${r.sub_idx}`);
    }
  }
  return lines.join('\n') + '\n';
}

function main() {
  if (process.argv.includes('--help')) {
    console.log('Usage: EMS_DB_PATH=<database.db> EMS_QUALITY_OUT=<directory> node scripts/quality-report.js [--run-id=<id|latest-run>]');
    return;
  }
  const runIdArg = (process.argv.find(a => a.startsWith('--run-id=')) || '').split('=').slice(1).join('=');
  try {
    const runId = resolveQualityRunArg(runIdArg);
    const report = buildReport({ runId });
    const paths = qualityOutPaths(report.run_id);
    fs.mkdirSync(OUT_DIR, { recursive: true });
    fs.writeFileSync(paths.jsonOut, JSON.stringify(report, null, 2), 'utf8');
    fs.writeFileSync(paths.txtOut, renderText(report), 'utf8');
    console.log('Saved:', paths.jsonOut);
    console.log('Saved:', paths.txtOut);
    if (report.summary.issue_count > 0) {
      console.log(`Quality issues: ${report.summary.issue_count}`);
      process.exitCode = 2;
    }
  } catch (error) {
    console.error('ERROR: ' + (error && error.message ? error.message : String(error)));
    process.exitCode = 1;
  }
}

if (require.main === module) {
  main();
}

module.exports = { buildReport, renderText };
