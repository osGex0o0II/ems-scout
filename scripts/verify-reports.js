#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const Database = require('better-sqlite3');
const XLSX = require('xlsx');
const { BLDG_ORDER, classifyAreaType } = require('../src/rules');

const ROOT = path.join(__dirname, '..');
const DB_PATH = process.env.EMS_DB_PATH || path.join(ROOT, 'out', 'ac.db');

function argValue(name, fallback) {
  const arg = process.argv.find(a => a.startsWith(name + '='));
  return arg ? arg.slice(name.length + 1) : fallback;
}

const REPORT_DIR = path.resolve(argValue('--dir', ROOT));

function fail(message) {
  throw new Error(message);
}

function latestFile(pattern) {
  if (!fs.existsSync(REPORT_DIR)) fail('Report dir not found: ' + REPORT_DIR);
  const files = fs.readdirSync(REPORT_DIR)
    .filter(name => pattern.test(name))
    .map(name => ({ name, full: path.join(REPORT_DIR, name), mtime: fs.statSync(path.join(REPORT_DIR, name)).mtimeMs }))
    .sort((a, b) => b.mtime - a.mtime);
  return files[0] || null;
}

function readRows(ws) {
  return XLSX.utils.sheet_to_json(ws, { header: 1, defval: '' });
}

function numberCell(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) fail('Expected numeric cell, got: ' + value);
  return n;
}

function dbSummary() {
  const db = new Database(DB_PATH, { readonly: true });
  const rows = db.prepare(`
    SELECT sa.building, p.layout, c.name, c.switch, c.comm
    FROM cards c
    JOIN pages p ON c.page_id = p.id
    JOIN sub_areas sa ON p.sub_area_id = sa.id
  `).all();
  const duplicateRenderedPages = db.prepare(`
    SELECT COUNT(*) AS n
    FROM pages
    WHERE COALESCE(raw_count, count) > COALESCE(unique_count, count)
  `).get().n;
  db.close();

  const withArea = rows.map(r => ({ ...r, areaType: classifyAreaType(r.name, r.layout) }));
  const count = pred => withArea.filter(pred).length;
  return {
    total: withArea.length,
    on: count(r => r.switch === 'ON'),
    off: count(r => r.switch === 'OFF'),
    notOn: count(r => r.switch !== 'ON'),
    offline: count(r => r.switch === '-' || r.comm === '离线'),
    pub: count(r => r.areaType === '公区'),
    nonPub: count(r => r.areaType !== '公区'),
    pubOn: count(r => r.switch === 'ON' && r.areaType === '公区'),
    nonPubOn: count(r => r.switch === 'ON' && r.areaType !== '公区'),
    pubNotOn: count(r => r.switch !== 'ON' && r.areaType === '公区'),
    nonPubNotOn: count(r => r.switch !== 'ON' && r.areaType !== '公区'),
    duplicateRenderedPages,
  };
}

function verifyAllWorkbook(file, expected) {
  const wb = XLSX.readFile(file.full, { cellStyles: true });
  const ws = wb.Sheets['汇总'];
  if (!ws) fail(file.name + ': missing 汇总 sheet');
  if (!wb.Sheets['报表说明']) fail(file.name + ': missing 报表说明 sheet');
  const rows = readRows(ws);
  const totalRow = rows.find(r => r[0] === '合计');
  if (!totalRow) fail(file.name + ': missing 合计 row');
  const got = {
    total: numberCell(totalRow[2]),
    on: numberCell(totalRow[3]),
    off: numberCell(totalRow[4]),
    offline: numberCell(totalRow[5]),
    pub: numberCell(totalRow[6]),
    nonPub: numberCell(totalRow[7]),
  };
  for (const [key, value] of Object.entries(got)) {
    if (value !== expected[key]) fail(`${file.name}: summary ${key}=${value}, expected ${expected[key]}`);
  }
  verifyWorkbookUsability(wb, file.name, { requireAllSheetsFilter: true });
  return got;
}

function verifyStatusWorkbook(file, expected, label) {
  const wb = XLSX.readFile(file.full, { cellStyles: true });
  const ws = wb.Sheets['汇总'];
  if (!ws) fail(file.name + ': missing 汇总 sheet');
  if (!wb.Sheets['报表说明']) fail(file.name + ': missing 报表说明 sheet');
  const rows = readRows(ws);
  const totalRow = rows.find(r => r[0] === '合计');
  if (!totalRow) fail(file.name + ': missing 合计 row');
  const got = {
    pub: numberCell(totalRow[2]),
    nonPub: numberCell(totalRow[3]),
    total: numberCell(totalRow[4]),
  };
  if (got.pub !== expected.pub || got.nonPub !== expected.nonPub || got.total !== expected.total) {
    fail(`${file.name}: ${label} summary ${JSON.stringify(got)}, expected ${JSON.stringify(expected)}`);
  }

  let deviceRows = 0;
  for (const b of BLDG_ORDER) {
    const sheet = wb.Sheets[b];
    if (!sheet) continue;
    for (const row of readRows(sheet).slice(1)) {
      if (row[0] === b) deviceRows++;
    }
  }
  if (deviceRows !== expected.total) {
    fail(`${file.name}: ${label} device rows=${deviceRows}, expected ${expected.total}`);
  }
  verifyStatusRiskColumns(wb, file.name);
  verifyWorkbookUsability(wb, file.name, { requireAllSheetsFilter: false });
  return got;
}

function verifyWorkbookUsability(wb, fileName, opts = {}) {
  const explanationRows = readRows(wb.Sheets['报表说明']);
  const text = explanationRows.map(r => r.join(' | ')).join('\n');
  for (const required of ['报表摘要', '数据质量说明', '风险分布', '异常分布', '开机风险 Top', '楼层开机 Top', '座区统计', '异常标签说明', '口径说明']) {
    if (!text.includes(required)) fail(`${fileName}: 报表说明 missing ${required}`);
  }
  verifyDetailSheets(wb, fileName);
  for (const sheetName of wb.SheetNames) {
    if (sheetName === '报表说明') continue;
    const ws = wb.Sheets[sheetName];
    if (!ws) continue;
    if (sheetName === '汇总' || opts.requireAllSheetsFilter) {
      if (!ws['!autofilter']) fail(`${fileName}: sheet ${sheetName} missing autofilter`);
    }
    if (!ws['!cols'] || ws['!cols'].length === 0) fail(`${fileName}: sheet ${sheetName} missing column widths`);
  }
}

function verifyDetailSheets(wb, fileName) {
  const riskSheet = wb.Sheets['风险明细'];
  const anomalySheet = wb.Sheets['异常明细'];
  if (!riskSheet) fail(fileName + ': missing 风险明细 sheet');
  if (!anomalySheet) fail(fileName + ': missing 异常明细 sheet');
  const riskRows = readRows(riskSheet);
  const anomalyRows = readRows(anomalySheet);
  for (const col of ['风险等级', '风险分', '风险原因', '楼栋', '设备名']) {
    if (!riskRows[0] || !riskRows[0].includes(col)) fail(`${fileName}: 风险明细 missing ${col} column`);
  }
  for (const col of ['异常标签', '楼栋', '设备名', '风险分', '备注']) {
    if (!anomalyRows[0] || !anomalyRows[0].includes(col)) fail(`${fileName}: 异常明细 missing ${col} column`);
  }
  if (!riskSheet['!autofilter']) fail(`${fileName}: 风险明细 missing autofilter`);
  if (!anomalySheet['!autofilter']) fail(`${fileName}: 异常明细 missing autofilter`);
  if (!riskSheet['!cols'] || riskSheet['!cols'].length === 0) fail(`${fileName}: 风险明细 missing column widths`);
  if (!anomalySheet['!cols'] || anomalySheet['!cols'].length === 0) fail(`${fileName}: 异常明细 missing column widths`);
}

function verifyStatusRiskColumns(wb, fileName) {
  const required = ['风险等级', '风险分', '风险原因'];
  for (const b of BLDG_ORDER) {
    const sheet = wb.Sheets[b];
    if (!sheet) continue;
    const rows = readRows(sheet);
    if (rows.length === 0) continue;
    const header = rows[0];
    for (const col of required) {
      if (!header.includes(col)) fail(`${fileName}: sheet ${b} missing ${col} column`);
    }
  }
}

function verifyDuplicateNotes(files, expected) {
  if (expected.duplicateRenderedPages === 0) return 0;
  let hits = 0;
  for (const file of files.filter(Boolean)) {
    if (file.name.endsWith('.xlsx')) {
      const wb = XLSX.readFile(file.full);
      for (const sheet of wb.SheetNames) {
        for (const row of readRows(wb.Sheets[sheet])) {
          const text = row.join(' | ');
          if (text.includes('重复渲染') || text.includes('同页重复渲染')) hits++;
        }
      }
    } else {
      const text = fs.readFileSync(file.full, 'utf8');
      if (text.includes('重复渲染') || text.includes('同页重复渲染')) hits++;
    }
  }
  if (hits === 0) fail('duplicate rendered pages exist in DB but no report note was found');
  return hits;
}

function main() {
  const expected = dbSummary();
  const files = {
    allXlsx: latestFile(/^设备总清单_.*\.xlsx$/),
    onXlsx: latestFile(/^未关闭空调清单_.*\.xlsx$/),
    offXlsx: latestFile(/^未开启空调清单_.*\.xlsx$/),
    allMd: latestFile(/^设备总清单_.*\.md$/),
    allTxt: latestFile(/^设备总清单_.*\.txt$/),
    offMd: latestFile(/^未开启空调清单_.*\.md$/),
    offTxt: latestFile(/^未开启空调清单_.*\.txt$/),
  };
  for (const [key, file] of Object.entries(files)) {
    if (!file && key.endsWith('Xlsx')) fail('Missing required report: ' + key);
  }

  verifyAllWorkbook(files.allXlsx, expected);
  verifyStatusWorkbook(files.onXlsx, { pub: expected.pubOn, nonPub: expected.nonPubOn, total: expected.on }, 'ON');
  verifyStatusWorkbook(files.offXlsx, { pub: expected.pubNotOn, nonPub: expected.nonPubNotOn, total: expected.notOn }, 'not ON');
  const duplicateNoteHits = verifyDuplicateNotes(Object.values(files), expected);

  console.log('Report verification passed.');
  console.log(`  DB total=${expected.total}, ON=${expected.on}, notON=${expected.notOn}`);
  console.log(`  duplicate rendered pages=${expected.duplicateRenderedPages}, report note hits=${duplicateNoteHits}`);
  console.log('  dir=' + REPORT_DIR);
}

if (require.main === module) {
  main();
}
