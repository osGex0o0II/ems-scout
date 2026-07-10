'use strict';

const fs = require('fs');
const path = require('path');
const { BLDG_ORDER } = require('../rules');

const ROOT = path.join(__dirname, '..', '..');
const DB_PATH = process.env.EMS_DB_PATH || path.join(ROOT, 'out', 'ac.db');

function normalizeFloorLabel(input) {
  const raw = String(input || '').trim().toUpperCase();
  if (!raw) return '';
  if (raw === 'BM') return 'BM';
  if (/^B\d+(?:\.\d+)?F?$/.test(raw)) return raw.endsWith('F') ? raw : raw + 'F';
  if (/^-?\d+(?:\.\d+)?F?$/.test(raw)) return raw.endsWith('F') ? raw : raw + 'F';
  return raw;
}

function parseFloorValue(input) {
  const raw = String(input || '').trim().toUpperCase();
  if (!raw) return null;
  if (raw === 'BM') return -2;
  const b = raw.match(/^B(\d+(?:\.\d+)?)F?$/);
  if (b) return -Number(b[1]);
  const f = raw.match(/^(-?\d+(?:\.\d+)?)F?$/);
  if (f) return Number(f[1]);
  return null;
}

function floorLabelFromValue(value) {
  if (value === null || value === undefined || value === '') return '';
  const n = Number(value);
  if (!Number.isFinite(n)) return String(value);
  if (n === -2) return 'BM';
  if (n < 0) return `B${Math.abs(n)}F`;
  return `${n}F`;
}

function ensureHistorySchema(db) {
  db.exec(`
    CREATE TABLE IF NOT EXISTS collection_runs (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      run_key TEXT UNIQUE,
      started_at TEXT,
      completed_at TEXT NOT NULL,
      imported_at TEXT NOT NULL,
      status TEXT NOT NULL DEFAULT 'completed',
      scope TEXT NOT NULL DEFAULT 'full',
      buildings TEXT NOT NULL DEFAULT '[]',
      json_path TEXT,
      db_snapshot_path TEXT,
      card_count INTEGER NOT NULL DEFAULT 0,
      on_count INTEGER NOT NULL DEFAULT 0,
      off_count INTEGER NOT NULL DEFAULT 0,
      offline_count INTEGER NOT NULL DEFAULT 0,
      unknown_count INTEGER NOT NULL DEFAULT 0,
      quality_summary TEXT NOT NULL DEFAULT '{}',
      is_anomaly INTEGER NOT NULL DEFAULT 0,
      note TEXT NOT NULL DEFAULT ''
    );
    CREATE INDEX IF NOT EXISTS idx_collection_runs_completed
      ON collection_runs(completed_at DESC);

    CREATE TABLE IF NOT EXISTS run_buildings (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      run_id INTEGER NOT NULL,
      building TEXT NOT NULL,
      sub_area_count INTEGER,
      menu_clicked TEXT,
      updated_at TEXT,
      FOREIGN KEY(run_id) REFERENCES collection_runs(id)
    );
    CREATE INDEX IF NOT EXISTS idx_run_buildings_run ON run_buildings(run_id, building);

    CREATE TABLE IF NOT EXISTS run_sub_areas (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      run_id INTEGER NOT NULL,
      source_sub_area_id INTEGER,
      building TEXT NOT NULL,
      sub_idx INTEGER,
      floor REAL,
      floor_label TEXT,
      text TEXT,
      x INTEGER,
      y INTEGER,
      FOREIGN KEY(run_id) REFERENCES collection_runs(id)
    );
    CREATE INDEX IF NOT EXISTS idx_run_sa_run_building ON run_sub_areas(run_id, building);
    CREATE INDEX IF NOT EXISTS idx_run_sa_run_floor ON run_sub_areas(run_id, building, floor);

    CREATE TABLE IF NOT EXISTS run_pages (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      run_id INTEGER NOT NULL,
      run_sub_area_id INTEGER NOT NULL,
      source_page_id INTEGER,
      page_name TEXT,
      count INTEGER,
      raw_count INTEGER,
      unique_count INTEGER,
      duplicate_names TEXT,
      on_href TEXT,
      off_href TEXT,
      layout TEXT,
      quality_reason TEXT,
      err TEXT,
      FOREIGN KEY(run_id) REFERENCES collection_runs(id),
      FOREIGN KEY(run_sub_area_id) REFERENCES run_sub_areas(id)
    );
    CREATE INDEX IF NOT EXISTS idx_run_pages_sa ON run_pages(run_sub_area_id);

    CREATE TABLE IF NOT EXISTS run_cards (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      run_id INTEGER NOT NULL,
      run_page_id INTEGER NOT NULL,
      source_card_id INTEGER,
      name TEXT,
      switch TEXT,
      mode TEXT,
      indoor TEXT,
      set_temp TEXT,
      fan TEXT,
      indicator TEXT,
      comm TEXT,
      FOREIGN KEY(run_id) REFERENCES collection_runs(id),
      FOREIGN KEY(run_page_id) REFERENCES run_pages(id)
    );
    CREATE INDEX IF NOT EXISTS idx_run_cards_run ON run_cards(run_id);
    CREATE INDEX IF NOT EXISTS idx_run_cards_page ON run_cards(run_page_id);
    CREATE INDEX IF NOT EXISTS idx_run_cards_name ON run_cards(name);
    CREATE INDEX IF NOT EXISTS idx_run_cards_switch ON run_cards(switch);

    CREATE TABLE IF NOT EXISTS floor_catalog (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      building TEXT NOT NULL,
      floor_label TEXT NOT NULL,
      floor_value REAL,
      source TEXT NOT NULL DEFAULT 'manual',
      enabled INTEGER NOT NULL DEFAULT 1,
      note TEXT NOT NULL DEFAULT '',
      created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
      updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
    );
    CREATE UNIQUE INDEX IF NOT EXISTS idx_floor_catalog_key
      ON floor_catalog(building, floor_label);
  `);

  try { db.exec('ALTER TABLE floor_monitor_snapshots ADD COLUMN run_id INTEGER'); } catch {}
  try { db.exec('ALTER TABLE floor_monitor_events ADD COLUMN run_id INTEGER'); } catch {}
  try { db.exec("ALTER TABLE collection_runs ADD COLUMN quality_summary TEXT NOT NULL DEFAULT '{}'"); } catch {}
  try { db.exec('ALTER TABLE collection_runs ADD COLUMN is_anomaly INTEGER NOT NULL DEFAULT 0'); } catch {}
  try { db.exec('ALTER TABLE buildings ADD COLUMN updated_at TEXT'); } catch {}
  try { db.exec('ALTER TABLE sub_areas ADD COLUMN sub_idx INT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN raw_count INT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN unique_count INT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN duplicate_names TEXT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN quality_reason TEXT'); } catch {}
  try { db.exec('ALTER TABLE run_pages ADD COLUMN quality_reason TEXT'); } catch {}
  try { db.exec('ALTER TABLE cards ADD COLUMN indicator TEXT'); } catch {}
  try { db.exec('CREATE INDEX IF NOT EXISTS idx_floor_monitor_snapshots_run ON floor_monitor_snapshots(run_id, monitored_floor_id, id DESC)'); } catch {}
  try { db.exec('CREATE INDEX IF NOT EXISTS idx_floor_monitor_events_run ON floor_monitor_events(run_id, monitored_floor_id, id DESC)'); } catch {}
}

function uniqueRunKey(db, base) {
  let key = base;
  let n = 1;
  const exists = db.prepare('SELECT 1 FROM collection_runs WHERE run_key = ?');
  while (exists.get(key)) {
    n += 1;
    key = `${base}_${n}`;
  }
  return key;
}

function localRunKey(date = new Date()) {
  const pad = n => String(n).padStart(2, '0');
  return `${date.getFullYear()}${pad(date.getMonth() + 1)}${pad(date.getDate())}_${pad(date.getHours())}${pad(date.getMinutes())}${pad(date.getSeconds())}`;
}

function listRuns(db, options = {}) {
  ensureHistorySchema(db);
  if (options.seed !== false) seedCurrentRun(db);
  const limit = Math.max(1, Math.min(Number(options.limit) || 100, 500));
  return db.prepare(`
    SELECT id, run_key, started_at, completed_at, imported_at, status, scope,
           buildings, json_path, db_snapshot_path, card_count, on_count,
           off_count, offline_count, unknown_count, quality_summary, is_anomaly, note
    FROM collection_runs
    ORDER BY datetime(completed_at) DESC, id DESC
    LIMIT ?
  `).all(limit).map(r => ({
    ...r,
    buildings: parseJsonArray(r.buildings),
    is_anomaly: Number(r.is_anomaly || 0),
    label: runLabel(r),
  }));
}

function runLabel(run) {
  const dt = new Date(run.completed_at);
  const pad = n => String(n).padStart(2, '0');
  const ts = Number.isNaN(dt.getTime())
    ? run.completed_at
    : `${dt.getFullYear()}-${pad(dt.getMonth() + 1)}-${pad(dt.getDate())} ${pad(dt.getHours())}:${pad(dt.getMinutes())}`;
  const buildings = parseJsonArray(run.buildings).join(',');
  return `${ts} ${run.scope === 'partial' ? buildings : '全量'} (${run.card_count || 0}张)`;
}

function setRunAnomaly(db, runId, isAnomaly, note = '') {
  ensureHistorySchema(db);
  const id = resolveRunId(db, runId);
  if (!id) throw new Error('Run id is required');
  const row = db.prepare('SELECT id, note FROM collection_runs WHERE id = ?').get(id);
  if (!row) throw new Error('Run not found: ' + runId);
  const extra = String(note || '').trim();
  let nextNote = row.note || '';
  if (isAnomaly && extra && !String(row.note || '').includes(extra)) {
    nextNote = [row.note || '', extra].filter(Boolean).join('；');
  } else if (!isAnomaly) {
    nextNote = String(nextNote)
      .split('；')
      .map(s => s.trim())
      .filter(s => s && s !== '采集数据异常，已隔离')
      .join('；');
  }
  db.prepare('UPDATE collection_runs SET is_anomaly = ?, note = ? WHERE id = ?').run(isAnomaly ? 1 : 0, nextNote, id);
  return db.prepare(`
    SELECT id, run_key, completed_at, card_count, is_anomaly, note
    FROM collection_runs
    WHERE id = ?
  `).get(id);
}

function restoreCurrentFromRun(db, runId) {
  ensureHistorySchema(db);
  const id = resolveRunId(db, runId);
  if (!id) throw new Error('Run id is required');
  const run = db.prepare('SELECT id, run_key, completed_at, buildings FROM collection_runs WHERE id = ?').get(id);
  if (!run) throw new Error('Run not found: ' + runId);

  const tx = db.transaction(() => {
    db.exec('DELETE FROM cards; DELETE FROM pages; DELETE FROM sub_areas; DELETE FROM buildings;');

    const insertBuilding = db.prepare(`
      INSERT INTO buildings (building, sub_area_count, menu_clicked, updated_at)
      VALUES (?, ?, ?, ?)
    `);
    const insertSA = db.prepare(`
      INSERT INTO sub_areas (building, sub_idx, text, floor, x, y)
      VALUES (?, ?, ?, ?, ?, ?)
    `);
    const insertPage = db.prepare(`
      INSERT INTO pages (sub_area_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    `);
    const insertCard = db.prepare(`
      INSERT INTO cards (page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
    `);

    const bRows = db.prepare(`
      SELECT building, sub_area_count, menu_clicked, updated_at
      FROM run_buildings
      WHERE run_id = ?
      ORDER BY building
    `).all(id);
    for (const b of bRows) {
      insertBuilding.run(b.building, b.sub_area_count, b.menu_clicked, b.updated_at || run.completed_at);
    }

    const runSaRows = db.prepare(`
      SELECT id, building, sub_idx, floor, text, x, y
      FROM run_sub_areas
      WHERE run_id = ?
      ORDER BY id
    `).all(id);
    const saMap = new Map();
    for (const sa of runSaRows) {
      const res = insertSA.run(sa.building, sa.sub_idx, sa.text, sa.floor, sa.x, sa.y);
      saMap.set(sa.id, Number(res.lastInsertRowid));
    }

    const runPageRows = db.prepare(`
      SELECT id, run_sub_area_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err
      FROM run_pages
      WHERE run_id = ?
      ORDER BY id
    `).all(id);
    const pageMap = new Map();
    for (const p of runPageRows) {
      const saId = saMap.get(p.run_sub_area_id);
      if (!saId) continue;
      const res = insertPage.run(saId, p.page_name, p.count, p.raw_count, p.unique_count, p.duplicate_names, p.on_href, p.off_href, p.layout, p.quality_reason, p.err);
      pageMap.set(p.id, Number(res.lastInsertRowid));
    }

    const runCardRows = db.prepare(`
      SELECT run_page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm
      FROM run_cards
      WHERE run_id = ?
      ORDER BY id
    `).all(id);
    for (const c of runCardRows) {
      const pageId = pageMap.get(c.run_page_id);
      if (!pageId) continue;
      insertCard.run(pageId, c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan, c.indicator, c.comm);
    }

    syncFloorCatalogFromCurrent(db);
  });

  tx();
  return {
    id,
    run_key: run.run_key,
    completed_at: run.completed_at,
    buildings: parseJsonArray(run.buildings),
  };
}

function deleteRun(db, runId) {
  ensureHistorySchema(db);
  const id = resolveRunId(db, runId);
  if (!id) throw new Error('Run id is required');
  const run = db.prepare('SELECT id, run_key, completed_at, card_count FROM collection_runs WHERE id = ?').get(id);
  if (!run) throw new Error('Run not found: ' + runId);
  const tx = db.transaction(() => {
    db.prepare('DELETE FROM run_cards WHERE run_id = ?').run(id);
    db.prepare('DELETE FROM run_pages WHERE run_id = ?').run(id);
    db.prepare('DELETE FROM run_sub_areas WHERE run_id = ?').run(id);
    db.prepare('DELETE FROM run_buildings WHERE run_id = ?').run(id);
    db.prepare('DELETE FROM collection_runs WHERE id = ?').run(id);
  });
  tx();
  return run;
}

function parseJsonArray(value) {
  if (Array.isArray(value)) return value;
  try {
    const parsed = JSON.parse(value || '[]');
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function resolveRunId(db, value) {
  if (!value || value === 'latest' || value === 'current') return null;
  const id = Number(value);
  if (!Number.isInteger(id) || id <= 0) throw new Error('Invalid run_id: ' + value);
  const row = db.prepare('SELECT id FROM collection_runs WHERE id = ?').get(id);
  if (!row) throw new Error('Run not found: ' + id);
  return id;
}

function sourceForRun(runId) {
  if (!runId) {
    return {
      runId: null,
      buildings: 'buildings',
      subAreas: 'sub_areas',
      pages: 'pages',
      cards: 'cards',
      pageSaColumn: 'sub_area_id',
      cardPageColumn: 'page_id',
      runWhere: '',
      runParams: [],
    };
  }
  return {
    runId,
    buildings: 'run_buildings',
    subAreas: 'run_sub_areas',
    pages: 'run_pages',
    cards: 'run_cards',
    pageSaColumn: 'run_sub_area_id',
    cardPageColumn: 'run_page_id',
    runWhere: 'sa.run_id = ?',
    runParams: [runId],
  };
}

function createRunFromCurrent(db, options = {}) {
  ensureHistorySchema(db);
  const selected = normalizeBuildings(options.buildings);
  const hasData = db.prepare(`
    SELECT COUNT(*) AS c
    FROM sub_areas sa
    JOIN pages p ON p.sub_area_id = sa.id
    JOIN cards c ON c.page_id = p.id
    ${selected.length ? `WHERE sa.building IN (${selected.map(() => '?').join(',')})` : ''}
  `).get(...selected).c;
  if (!hasData) return null;

  const now = options.completedAt || new Date().toISOString();
  const runKey = uniqueRunKey(db, options.runKey || localRunKey(new Date(now)));
  const scope = selected.length && selected.length < BLDG_ORDER.length ? 'partial' : 'full';
  const buildings = selected.length ? selected : db.prepare('SELECT DISTINCT building FROM sub_areas ORDER BY building').all().map(r => r.building);

  const insertRun = db.prepare(`
    INSERT INTO collection_runs
      (run_key, started_at, completed_at, imported_at, status, scope, buildings, json_path, db_snapshot_path, note)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `);
  const insertRunBuilding = db.prepare(`
    INSERT INTO run_buildings (run_id, building, sub_area_count, menu_clicked, updated_at)
    VALUES (?, ?, ?, ?, ?)
  `);
  const insertRunSA = db.prepare(`
    INSERT INTO run_sub_areas (run_id, source_sub_area_id, building, sub_idx, floor, floor_label, text, x, y)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
  `);
  const insertRunPage = db.prepare(`
    INSERT INTO run_pages
      (run_id, run_sub_area_id, source_page_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `);
  const insertRunCard = db.prepare(`
    INSERT INTO run_cards
      (run_id, run_page_id, source_card_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `);
  const updateRunStats = db.prepare(`
    UPDATE collection_runs
    SET card_count = ?, on_count = ?, off_count = ?, offline_count = ?, unknown_count = ?
    WHERE id = ?
  `);

  const tx = db.transaction(() => {
    const res = insertRun.run(
      runKey,
      options.startedAt || null,
      now,
      new Date().toISOString(),
      options.status || 'completed',
      scope,
      JSON.stringify(buildings),
      options.jsonPath || null,
      options.dbSnapshotPath || null,
      options.note || ''
    );
    const runId = Number(res.lastInsertRowid);
    const bRows = db.prepare(`
      SELECT building, sub_area_count, menu_clicked, updated_at
      FROM buildings
      ${buildings.length ? `WHERE building IN (${buildings.map(() => '?').join(',')})` : ''}
      ORDER BY building
    `).all(...buildings);
    for (const b of bRows) {
      insertRunBuilding.run(runId, b.building, b.sub_area_count, b.menu_clicked, b.updated_at);
    }

    const saRows = db.prepare(`
      SELECT id, building, sub_idx, floor, text, x, y
      FROM sub_areas
      ${buildings.length ? `WHERE building IN (${buildings.map(() => '?').join(',')})` : ''}
      ORDER BY building, sub_idx, id
    `).all(...buildings);
    const pageMap = new Map();
    for (const sa of saRows) {
      const saRes = insertRunSA.run(runId, sa.id, sa.building, sa.sub_idx, sa.floor, floorLabelFromValue(sa.floor), sa.text, sa.x, sa.y);
      const runSaId = Number(saRes.lastInsertRowid);
      const pageRows = db.prepare(`
        SELECT id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, err
        FROM pages
        WHERE sub_area_id = ?
        ORDER BY id
      `).all(sa.id);
      for (const p of pageRows) {
        const pageRes = insertRunPage.run(runId, runSaId, p.id, p.page_name, p.count, p.raw_count, p.unique_count, p.duplicate_names, p.on_href, p.off_href, p.layout, p.quality_reason, p.err);
        pageMap.set(p.id, Number(pageRes.lastInsertRowid));
      }
    }

    if (pageMap.size) {
      const cardRows = db.prepare(`
        SELECT c.id, c.page_id, c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan, c.indicator, c.comm
        FROM cards c
        JOIN pages p ON p.id = c.page_id
        JOIN sub_areas sa ON sa.id = p.sub_area_id
        ${buildings.length ? `WHERE sa.building IN (${buildings.map(() => '?').join(',')})` : ''}
        ORDER BY c.id
      `).all(...buildings);
      for (const c of cardRows) {
        insertRunCard.run(runId, pageMap.get(c.page_id), c.id, c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan, c.indicator, c.comm);
      }
    }

    const stats = db.prepare(`
      SELECT COUNT(*) AS total,
             SUM(comm = '开机') AS on_count,
             SUM(comm = '关机') AS off_count,
             SUM(comm = '离线') AS offline_count,
             SUM(COALESCE(comm, '') NOT IN ('开机', '关机', '离线')) AS unknown_count
      FROM run_cards
      WHERE run_id = ?
    `).get(runId);
    updateRunStats.run(stats.total || 0, stats.on_count || 0, stats.off_count || 0, stats.offline_count || 0, stats.unknown_count || 0, runId);
    syncFloorCatalogFromCurrent(db);
    return runId;
  });

  return tx();
}

function normalizeBuildings(buildings) {
  if (!buildings) return [];
  if (typeof buildings === 'string') {
    return buildings.split(',').map(s => s.trim()).filter(Boolean);
  }
  if (Array.isArray(buildings)) {
    return buildings.map(s => String(s).trim()).filter(Boolean);
  }
  return [];
}

function seedCurrentRun(db) {
  ensureHistorySchema(db);
  const existing = db.prepare('SELECT COUNT(*) AS c FROM collection_runs').get().c;
  if (existing > 0) {
    syncFloorCatalogFromCurrent(db);
    return null;
  }
  const cardCount = tableCount(db, 'cards');
  if (!cardCount) {
    syncFloorCatalogFromCurrent(db);
    return null;
  }
  let completedAt = new Date().toISOString();
  try {
    const row = db.prepare('SELECT MAX(updated_at) AS t FROM buildings WHERE updated_at IS NOT NULL').get();
    if (row && row.t) completedAt = row.t;
  } catch {}
  return createRunFromCurrent(db, {
    completedAt,
    runKey: 'seed_current_' + localRunKey(new Date(completedAt)),
    note: '从现有最新数据库自动建立的历史批次',
  });
}

function tableCount(db, table) {
  try { return db.prepare(`SELECT COUNT(*) AS c FROM ${table}`).get().c; }
  catch { return 0; }
}

function syncFloorCatalogFromCurrent(db) {
  ensureHistorySchema(db);
  const now = new Date().toISOString();
  const rows = db.prepare(`
    SELECT building, floor
    FROM sub_areas
    WHERE floor IS NOT NULL
    GROUP BY building, floor
    ORDER BY building, floor
  `).all();
  const upsert = db.prepare(`
    INSERT INTO floor_catalog (building, floor_label, floor_value, source, enabled, note, created_at, updated_at)
    VALUES (?, ?, ?, 'discovered', 1, '', ?, ?)
    ON CONFLICT(building, floor_label) DO UPDATE SET
      floor_value = excluded.floor_value,
      source = CASE
        WHEN floor_catalog.source = 'manual' THEN 'manual+discovered'
        ELSE 'discovered'
      END,
      enabled = 1,
      updated_at = excluded.updated_at
  `);
  const tx = db.transaction(() => {
    for (const r of rows) {
      upsert.run(r.building, floorLabelFromValue(r.floor), r.floor, now, now);
    }
  });
  tx();
}

function saveFloorCatalog(db, input) {
  ensureHistorySchema(db);
  const building = String(input.building || '').trim();
  const floorLabel = normalizeFloorLabel(input.floor_label || input.floor || input.floorLabel);
  const floorValue = parseFloorValue(floorLabel);
  const note = String(input.note || '').trim();
  const enabled = input.enabled === false || input.enabled === 0 ? 0 : 1;
  if (!BLDG_ORDER.includes(building)) throw new Error('Invalid building: ' + building);
  if (!floorLabel || !Number.isFinite(Number(floorValue))) throw new Error('Invalid floor: ' + floorLabel);
  const now = new Date().toISOString();
  db.prepare(`
    INSERT INTO floor_catalog (building, floor_label, floor_value, source, enabled, note, created_at, updated_at)
    VALUES (?, ?, ?, 'manual', ?, ?, ?, ?)
    ON CONFLICT(building, floor_label) DO UPDATE SET
      floor_value = excluded.floor_value,
      source = CASE
        WHEN floor_catalog.source = 'discovered' THEN 'manual+discovered'
        ELSE 'manual'
      END,
      enabled = excluded.enabled,
      note = excluded.note,
      updated_at = excluded.updated_at
  `).run(building, floorLabel, floorValue, enabled, note, now, now);
  return db.prepare('SELECT * FROM floor_catalog WHERE building = ? AND floor_label = ?').get(building, floorLabel);
}

function loadFloorCatalog(db, options = {}) {
  ensureHistorySchema(db);
  syncFloorCatalogFromCurrent(db);
  const params = [];
  const where = [];
  if (options.building) {
    where.push('building = ?');
    params.push(options.building);
  }
  if (!options.includeDisabled) where.push('enabled = 1');
  const rows = db.prepare(`
    SELECT id, building, floor_label, floor_value, source, enabled, note, created_at, updated_at
    FROM floor_catalog
    ${where.length ? 'WHERE ' + where.join(' AND ') : ''}
    ORDER BY building, floor_value, floor_label
  `).all(...params);
  return rows;
}

module.exports = {
  DB_PATH,
  ensureHistorySchema,
  parseFloorValue,
  normalizeFloorLabel,
  floorLabelFromValue,
  listRuns,
  setRunAnomaly,
  restoreCurrentFromRun,
  deleteRun,
  resolveRunId,
  sourceForRun,
  createRunFromCurrent,
  seedCurrentRun,
  syncFloorCatalogFromCurrent,
  saveFloorCatalog,
  loadFloorCatalog,
};
