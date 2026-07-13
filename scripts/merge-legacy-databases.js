#!/usr/bin/env node
'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const Database = require('better-sqlite3');
const { copyStableSqliteSnapshot } = require('./sqlite-snapshot');

const root = path.resolve(__dirname, '..');

function uniqueCards(cards) {
  const seen = new Set();
  const unique = [];
  const copies = new Map();
  for (const card of cards) {
    const name = String(card.name || '');
    if (name && seen.has(name)) {
      copies.set(name, (copies.get(name) || 1) + 1);
      continue;
    }
    if (name) seen.add(name);
    unique.push(card);
  }
  return {
    unique,
    duplicates: [...copies].map(([name, count]) => ({ name, copies: count })),
  };
}

function readSource(snapshotPath) {
  const source = new Database(snapshotPath, { fileMustExist: true });
  try {
    source.pragma('query_only = ON');
    const quickCheck = source.pragma('quick_check');
    if (quickCheck.some(row => row.quick_check !== 'ok')) throw new Error('源数据库快照未通过 quick_check。');
    const buildings = source.prepare('SELECT * FROM buildings ORDER BY building').all();
    const subAreas = source.prepare('SELECT * FROM sub_areas ORDER BY id').all();
    const pages = source.prepare('SELECT * FROM pages ORDER BY id').all();
    const cards = source.prepare('SELECT * FROM cards ORDER BY id').all();
    const subAreaIds = new Set(subAreas.map(row => row.id));
    const pageIds = new Set(pages.map(row => row.id));
    const orphanPage = pages.find(row => !subAreaIds.has(row.sub_area_id));
    const orphanCard = cards.find(row => !pageIds.has(row.page_id));
    if (orphanPage || orphanCard) throw new Error('源数据库包含孤立 page/card，已停止合并。');
    return { buildings, subAreas, pages, cards };
  } finally {
    source.close();
  }
}

function publishNewFile(partialPath, targetPath) {
  fs.linkSync(partialPath, targetPath);
  fs.unlinkSync(partialPath);
}

function mergeLegacyDatabases(options = {}) {
  const sourceRoot = path.resolve(options.sourceRoot || process.env.EMS_MERGE_SOURCE_ROOT || path.join(root, 'data'));
  const targetPath = path.resolve(options.targetPath || process.env.EMS_MERGE_TARGET_PATH || path.join(sourceRoot, 'ac.db'));
  const tempRoot = path.resolve(options.tempRoot || process.env.EMS_MERGE_TEMP_ROOT || os.tmpdir());
  const schemaPath = path.resolve(options.schemaPath || path.join(root, 'scripts', 'schema.sql'));
  if (fs.existsSync(targetPath)) throw new Error(`目标数据库已存在，为避免覆盖而停止：${targetPath}`);
  fs.mkdirSync(tempRoot, { recursive: true });
  fs.mkdirSync(path.dirname(targetPath), { recursive: true });
  const sources = fs.readdirSync(sourceRoot, { withFileTypes: true })
    .filter(entry => entry.isDirectory())
    .map(entry => path.join(sourceRoot, entry.name, 'ac.db'))
    .filter(file => fs.existsSync(file))
    .sort((left, right) => left.localeCompare(right, 'zh-CN'));
  if (sources.length === 0) throw new Error('data/*/ac.db 未找到可合并的楼栋数据库。');

  const partialPath = `${targetPath}.partial-${process.pid}-${Date.now()}`;
  const snapshots = [];
  let target;
  try {
    const loaded = sources.map(sourcePath => {
      const snapshot = copyStableSqliteSnapshot(sourcePath, tempRoot);
      snapshots.push(snapshot.directory);
      return { sourcePath, modifiedAt: new Date(fs.statSync(sourcePath).mtimeMs).toISOString(), ...readSource(snapshot.path) };
    });
    const owners = new Map();
    for (const item of loaded) {
      for (const building of new Set(item.subAreas.map(row => String(row.building || '').replace(/楼$/, '')))) {
        if (!building) throw new Error(`源数据库缺少楼栋标识：${item.sourcePath}`);
        if (owners.has(building)) throw new Error(`楼栋 ${building} 同时出现在多个源数据库，已停止以避免重复。`);
        owners.set(building, item);
      }
    }

    target = new Database(partialPath);
    target.pragma('foreign_keys = ON');
    target.exec(fs.readFileSync(schemaPath, 'utf8'));
    const insertBuilding = target.prepare('INSERT INTO buildings (building, sub_area_count, menu_clicked) VALUES (?, ?, ?)');
    const insertSubArea = target.prepare('INSERT INTO sub_areas (building, sub_idx, floor, text, x, y) VALUES (?, ?, ?, ?, ?, ?)');
    const insertPage = target.prepare('INSERT INTO pages (sub_area_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)');
    const insertCard = target.prepare('INSERT INTO cards (page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)');
    const insertRun = target.prepare("INSERT INTO collection_runs (run_key, completed_at, imported_at, status, scope, buildings, card_count, on_count, off_count, offline_count, unknown_count, note) VALUES (?, ?, ?, 'completed', 'partial', ?, ?, ?, ?, ?, ?, ?)");
    const insertRunBuilding = target.prepare('INSERT INTO run_buildings (run_id, building, sub_area_count, menu_clicked, updated_at) VALUES (?, ?, ?, ?, ?)');
    const insertRunSubArea = target.prepare('INSERT INTO run_sub_areas (run_id, source_sub_area_id, building, sub_idx, floor, floor_label, text, x, y) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)');
    const insertRunPage = target.prepare('INSERT INTO run_pages (run_id, run_sub_area_id, source_page_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)');
    const insertRunCard = target.prepare('INSERT INTO run_cards (run_id, run_page_id, source_card_id, name, switch, mode, indoor, set_temp, fan, indicator, comm) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)');

    const migrate = target.transaction(() => {
      const results = [];
      let runIndex = 0;
      for (const [building, item] of [...owners].sort(([left], [right]) => left.localeCompare(right, 'zh-CN'))) {
        const subAreas = item.subAreas.filter(row => String(row.building || '').replace(/楼$/, '') === building);
        const subAreaIds = new Set(subAreas.map(row => row.id));
        const pages = item.pages.filter(row => subAreaIds.has(row.sub_area_id));
        const pageIds = new Set(pages.map(row => row.id));
        const cards = item.cards.filter(row => pageIds.has(row.page_id));
        const pageEntries = pages.map(page => {
          const rawCards = cards.filter(row => row.page_id === page.id);
          return { page, rawCards, deduplicated: uniqueCards(rawCards) };
        });
        const uniqueRunCards = pageEntries.flatMap(entry => entry.deduplicated.unique);
        const buildingRow = item.buildings.find(row => String(row.building || '').replace(/楼$/, '') === building) || {};
        const counts = { on: 0, off: 0, offline: 0, unknown: 0 };
        for (const card of uniqueRunCards) {
          if (card.comm === '开机') counts.on++;
          else if (card.comm === '关机') counts.off++;
          else if (card.comm === '离线') counts.offline++;
          else counts.unknown++;
        }
        insertBuilding.run(building, subAreas.length, buildingRow.menu_clicked || 'legacy');
        const runId = Number(insertRun.run(
          `legacy-${building}-${++runIndex}`,
          item.modifiedAt,
          item.modifiedAt,
          JSON.stringify([building]),
          uniqueRunCards.length,
          counts.on,
          counts.off,
          counts.offline,
          counts.unknown,
          `由 ${path.relative(root, item.sourcePath)} 的临时快照迁移，原始数据库未打开。`).lastInsertRowid);
        insertRunBuilding.run(runId, building, subAreas.length, buildingRow.menu_clicked || 'legacy', item.modifiedAt);
        for (const subArea of subAreas) {
          const currentSubAreaId = Number(insertSubArea.run(building, subArea.sub_idx, subArea.floor, subArea.text, subArea.x, subArea.y).lastInsertRowid);
          const runSubAreaId = Number(insertRunSubArea.run(runId, subArea.id, building, subArea.sub_idx, subArea.floor, null, subArea.text, subArea.x, subArea.y).lastInsertRowid);
          for (const { page, rawCards, deduplicated } of pageEntries.filter(entry => entry.page.sub_area_id === subArea.id)) {
            const duplicateNames = deduplicated.duplicates.length ? JSON.stringify(deduplicated.duplicates) : '';
            const values = [page.page_name, deduplicated.unique.length, rawCards.length, deduplicated.unique.length, duplicateNames, page.on_href || '', page.off_href || '', page.layout || '', 'legacy_import', page.err || ''];
            const currentPageId = Number(insertPage.run(currentSubAreaId, ...values).lastInsertRowid);
            const runPageId = Number(insertRunPage.run(runId, runSubAreaId, page.id, ...values).lastInsertRowid);
            for (const card of deduplicated.unique) {
              const cardValues = [card.name, card.switch, card.mode, card.indoor, card.set_temp, card.fan, card.indicator || '', card.comm || ''];
              insertCard.run(currentPageId, ...cardValues);
              insertRunCard.run(runId, runPageId, card.id, ...cardValues);
            }
          }
        }
        results.push({ building, runId, cards: uniqueRunCards.length, rawCards: cards.length });
      }
      const violations = target.pragma('foreign_key_check');
      if (violations.length) throw new Error(`合并结果包含 ${violations.length} 个外键错误。`);
      return results;
    });
    const results = migrate();
    target.close();
    target = null;
    publishNewFile(partialPath, targetPath);
    return { target: targetPath, sources: results };
  } catch (error) {
    if (target) target.close();
    fs.rmSync(partialPath, { force: true });
    throw error;
  } finally {
    for (const directory of snapshots) fs.rmSync(directory, { recursive: true, force: true });
  }
}

function printHelp() {
  console.log('Usage: node scripts/merge-legacy-databases.js [options]');
  console.log('  --source-root=<dir>  source root containing */ac.db');
  console.log('  --target=<file>      new output database; existing files are refused');
  console.log('  --temp-root=<dir>    owned temporary snapshot root');
  console.log('  --schema=<file>      SQLite schema file');
}

function parseOptions(args) {
  const options = {};
  for (const arg of args) {
    if (arg === '--help' || arg === '-h') return { showHelp: true };
    const match = arg.match(/^--(source-root|target|temp-root|schema)=(.+)$/);
    if (!match) throw new Error(`未知参数：${arg}`);
    const key = { 'source-root': 'sourceRoot', target: 'targetPath', 'temp-root': 'tempRoot', schema: 'schemaPath' }[match[1]];
    options[key] = match[2];
  }
  return options;
}

function main() {
  try {
    const options = parseOptions(process.argv.slice(2));
    if (options.showHelp) {
      printHelp();
      return;
    }
    console.log(JSON.stringify(mergeLegacyDatabases(options), null, 2));
  } catch (error) {
    console.error('ERROR: ' + error.message);
    process.exitCode = 1;
  }
}

if (require.main === module) main();

module.exports = { mergeLegacyDatabases, parseOptions, publishNewFile };
