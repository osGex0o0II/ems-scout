const fs = require('fs');
const path = require('path');
const Database = require('better-sqlite3');
const { ensureMonitorSchema } = require('../src/panel/monitor');
const { ensureHistorySchema, createRunFromCurrent, syncFloorCatalogFromCurrent } = require('../src/panel/history');
const { validateEnumData, formatValidation } = require('../src/enum-validator');

const ROOT = path.join(__dirname, '..');
const JSON_PATH = process.env.EMS_JSON_PATH || path.join(ROOT, 'out', 'enum_full_v5.json');
const DB_PATH = process.env.EMS_DB_PATH || path.join(ROOT, 'out', 'ac.db');

const data = JSON.parse(fs.readFileSync(JSON_PATH, 'utf8'));
const BUILDING_FILTER = process.argv.find(a => a.startsWith('--bldg='));
const IMPORT_FILTER = BUILDING_FILTER ? BUILDING_FILTER.split('=')[1].split(',').map(s => s.trim()).filter(Boolean) : null;
const buildings = (data.buildings || []).filter(b => !IMPORT_FILTER || IMPORT_FILTER.includes(b.building));

if (IMPORT_FILTER && buildings.length !== IMPORT_FILTER.length) {
  const found = new Set(buildings.map(b => b.building));
  const missing = IMPORT_FILTER.filter(b => !found.has(b));
  throw new Error('Filtered building(s) not found in JSON: ' + missing.join(', '));
}

if (process.env.EMS_SKIP_ENUM_VALIDATION !== '1') {
  const validation = validateEnumData(data, { buildings: IMPORT_FILTER || [] });
  for (const line of formatValidation(validation)) console.log(line);
  if (!validation.ok) {
    throw new Error('采集结果校验失败，已停止导入数据库。');
  }
}

const db = new Database(DB_PATH);
db.pragma('journal_mode = WAL');
db.pragma('foreign_keys = OFF');

function ensureSchema() {
  db.exec(`
    CREATE TABLE IF NOT EXISTS buildings (
      building TEXT PRIMARY KEY,
      sub_area_count INT,
      menu_clicked TEXT,
      updated_at TEXT
    );
    CREATE TABLE IF NOT EXISTS sub_areas (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      building TEXT NOT NULL,
      sub_idx INT,
      floor REAL,
      text TEXT,
      x INT,
      y INT,
      FOREIGN KEY(building) REFERENCES buildings(building)
    );
    CREATE TABLE IF NOT EXISTS pages (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      sub_area_id INT NOT NULL,
      page_name TEXT,
      count INT,
      raw_count INT,
      unique_count INT,
      duplicate_names TEXT,
      on_href TEXT,
      off_href TEXT,
      layout TEXT,
      quality_reason TEXT,
      err TEXT,
      FOREIGN KEY(sub_area_id) REFERENCES sub_areas(id)
    );
    CREATE TABLE IF NOT EXISTS cards (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      page_id INT NOT NULL,
      name TEXT,
      switch TEXT,
      mode TEXT,
      indoor TEXT,
      set_temp TEXT,
      fan TEXT,
      indicator TEXT,
      comm TEXT,
      FOREIGN KEY(page_id) REFERENCES pages(id)
    );
    CREATE INDEX IF NOT EXISTS idx_sa_building ON sub_areas(building);
    CREATE INDEX IF NOT EXISTS idx_sa_floor ON sub_areas(building, floor);
    CREATE INDEX IF NOT EXISTS idx_pg_sa ON pages(sub_area_id);
    CREATE INDEX IF NOT EXISTS idx_cd_pg ON cards(page_id);
    CREATE INDEX IF NOT EXISTS idx_cd_sw ON cards(switch);
    CREATE INDEX IF NOT EXISTS idx_cd_name ON cards(name);
  `);
  ensureMonitorSchema(db);
  ensureHistorySchema(db);
  try { db.exec('ALTER TABLE buildings ADD COLUMN updated_at TEXT'); } catch {}
  try { db.exec('ALTER TABLE sub_areas ADD COLUMN sub_idx INT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN raw_count INT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN unique_count INT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN duplicate_names TEXT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN quality_reason TEXT'); } catch {}
  try { db.exec('ALTER TABLE cards ADD COLUMN indicator TEXT'); } catch {}
}

ensureSchema();

const insertSA = db.prepare(`INSERT INTO sub_areas (building, sub_idx, text, floor, x, y) VALUES (?, ?, ?, ?, ?, ?)`);
const insertPage = db.prepare(`INSERT INTO pages (sub_area_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`);
const insertCard = db.prepare(`INSERT INTO cards (page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`);

function uniquePageCards(cards = []) {
  const seen = new Set();
  const out = [];
  const duplicates = new Map();
  for (const c of cards) {
    const key = c && c.name ? c.name : '';
    if (key && seen.has(key)) {
      duplicates.set(key, (duplicates.get(key) || 1) + 1);
      continue;
    }
    if (key) seen.add(key);
    out.push(c);
  }
  return {
    cards: out,
    rawCount: cards.length,
    uniqueCount: out.length,
    duplicateNames: [...duplicates.entries()].map(([name, copies]) => ({ name, copies })),
  };
}

function normalizeDuplicateNames(value) {
  if (!value) return [];
  if (Array.isArray(value)) return value;
  if (typeof value === 'string') {
    try {
      const parsed = JSON.parse(value);
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }
  return [];
}

function insertBuildings() {
  for (const bldg of buildings) {
    if (!bldg.subAreas) continue;
    console.log(bldg.building + ':', bldg.subAreas.length + ' sub-areas');
    for (const sa of bldg.subAreas) {
      const saResult = insertSA.run(bldg.building, sa.idx ?? null, sa.text, sa.floor, sa.x, sa.y);
      const saId = saResult.lastInsertRowid;
      for (const p of (sa.pages || [])) {
        const pageCards = uniquePageCards(p.cards || []);
        const cards = pageCards.cards;
        const duplicateList = normalizeDuplicateNames(p.duplicateNames);
        const duplicateNames = (duplicateList.length ? duplicateList : pageCards.duplicateNames);
        const rawCount = Number.isFinite(p.rawCount) ? p.rawCount : pageCards.rawCount;
        const uniqueCount = Number.isFinite(p.uniqueCount) ? p.uniqueCount : pageCards.uniqueCount;
        const duplicateNamesJson = duplicateNames.length ? JSON.stringify(duplicateNames) : '';
        const pResult = insertPage.run(saId, p.page, cards.length, rawCount, uniqueCount, duplicateNamesJson, p.onHref ?? null, p.offHref ?? null, p.layout, p.qualityReason ?? p.quality_reason ?? '', p.err ?? null);
        const pageId = pResult.lastInsertRowid;
        for (const c of cards) {
          insertCard.run(pageId, c.name, c.switch, c.mode, c.indoor, c.setTemp, c.fan, c.indicator || '', c.comm || '');
        }
      }
    }
  }
}

// Clear old data first. With --bldg, replace only selected buildings.
console.log(IMPORT_FILTER ? 'Clearing selected building data...' : 'Clearing old data...');
function clearAll() {
  db.exec('DELETE FROM cards; DELETE FROM pages; DELETE FROM sub_areas; DELETE FROM buildings;');
}
function clearBuildings(selected) {
  const getSaIds = db.prepare(`SELECT id FROM sub_areas WHERE building = ?`);
  const deleteCards = db.prepare(`DELETE FROM cards WHERE page_id IN (SELECT id FROM pages WHERE sub_area_id = ?)`);
  const deletePages = db.prepare(`DELETE FROM pages WHERE sub_area_id = ?`);
  const deleteSubAreas = db.prepare(`DELETE FROM sub_areas WHERE building = ?`);
  for (const building of selected) {
    for (const row of getSaIds.all(building)) {
      deleteCards.run(row.id);
      deletePages.run(row.id);
    }
    deleteSubAreas.run(building);
  }
}

console.log('Importing...');
const now = new Date().toISOString();
const upsert = db.prepare(`INSERT INTO buildings (building, sub_area_count, menu_clicked, updated_at) VALUES (?, ?, ?, ?) ON CONFLICT(building) DO UPDATE SET sub_area_count=excluded.sub_area_count, menu_clicked=excluded.menu_clicked, updated_at=excluded.updated_at`);
const updateTs = db.prepare(`UPDATE buildings SET updated_at=? WHERE building=?`);

const importCurrent = db.transaction(() => {
  if (IMPORT_FILTER) clearBuildings(IMPORT_FILTER);
  else clearAll();

  insertBuildings();

  for (const bldg of buildings) {
    upsert.run(bldg.building, (bldg.subAreas||[]).length, bldg.menuClicked||'', now);
  }
  for (const bldg of buildings) {
    if (!IMPORT_FILTER || IMPORT_FILTER.includes(bldg.building)) updateTs.run(now, bldg.building);
  }

  syncFloorCatalogFromCurrent(db);
  return createRunFromCurrent(db, {
    buildings: IMPORT_FILTER || buildings.map(b => b.building),
    completedAt: now,
    jsonPath: JSON_PATH,
    note: IMPORT_FILTER ? '面板/脚本单栋或多栋导入' : '面板/脚本全量导入',
  });
});
const runId = importCurrent();
console.log(`History run: ${runId || 'none'}`);

// Show per-building timestamps
console.log('');
for (const bldg of buildings) {
  const row = db.prepare(`SELECT updated_at FROM buildings WHERE building=?`).get(bldg.building);
  const t = row ? new Date(row.updated_at) : null;
  const ts = t ? `${String(t.getHours()).padStart(2,'0')}:${String(t.getMinutes()).padStart(2,'0')}` : '--';
  console.log(`  ${bldg.building}: ${(bldg.subAreas||[]).length} 子区  (更新 ${ts})`);
}

console.log('Done.');
db.close();
