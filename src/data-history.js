'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { BLDG_ORDER } = require('./rules');
const { formatLocalTimestamp } = require('./time');

const ROOT = path.join(__dirname, '..');
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
  db.pragma('busy_timeout = 10000');
  db.pragma('foreign_keys = ON');
  db.exec(`
    CREATE TABLE IF NOT EXISTS collection_runs (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      run_no INTEGER,
      run_key TEXT UNIQUE,
      batch_uid TEXT NOT NULL DEFAULT '',
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
      collection_mode TEXT NOT NULL DEFAULT '',
      is_anomaly INTEGER NOT NULL DEFAULT 0,
      note TEXT NOT NULL DEFAULT '',
      restored_from_run_id INTEGER,
      lifecycle_state TEXT NOT NULL DEFAULT 'completed',
      current_revision_uid TEXT,
      restored_from_batch_uid TEXT
    );
    CREATE INDEX IF NOT EXISTS idx_collection_runs_completed
      ON collection_runs(completed_at DESC);

    CREATE TABLE IF NOT EXISTS run_id_registry (
      technical_id INTEGER PRIMARY KEY,
      allocated_at TEXT NOT NULL,
      allocation_kind TEXT NOT NULL DEFAULT 'collection_run'
    );
    CREATE INDEX IF NOT EXISTS idx_run_id_registry_allocated
      ON run_id_registry(allocated_at);

    CREATE TABLE IF NOT EXISTS run_key_registry (
      run_key TEXT PRIMARY KEY,
      batch_uid TEXT NOT NULL DEFAULT '',
      first_seen_at TEXT NOT NULL,
      deleted_at TEXT,
      last_run_id INTEGER
    );
    CREATE INDEX IF NOT EXISTS idx_run_key_registry_deleted
      ON run_key_registry(deleted_at);

    CREATE TABLE IF NOT EXISTS run_operations (
      operation_id TEXT PRIMARY KEY,
      operation_type TEXT NOT NULL,
      run_id INTEGER,
      batch_uid TEXT,
      run_key TEXT,
      occurred_at TEXT NOT NULL,
      result TEXT NOT NULL,
      summary TEXT NOT NULL DEFAULT '',
      deleted_cards INTEGER NOT NULL DEFAULT 0,
      deleted_pages INTEGER NOT NULL DEFAULT 0,
      deleted_sub_areas INTEGER NOT NULL DEFAULT 0,
      deleted_buildings INTEGER NOT NULL DEFAULT 0,
      pending_artifacts INTEGER NOT NULL DEFAULT 0
    );

    CREATE TABLE IF NOT EXISTS current_data_state (
      id INTEGER PRIMARY KEY CHECK (id = 1),
      revision_uid TEXT NOT NULL UNIQUE,
      updated_at TEXT NOT NULL,
      source TEXT NOT NULL DEFAULT '本机 SQLite'
    );

    CREATE TABLE IF NOT EXISTS current_data_sources (
      building TEXT PRIMARY KEY,
      revision_uid TEXT NOT NULL,
      run_id INTEGER,
      batch_uid TEXT,
      source_updated_at TEXT,
      card_count INTEGER NOT NULL DEFAULT 0,
      state TEXT NOT NULL DEFAULT 'bound',
      reason TEXT NOT NULL DEFAULT ''
    );

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
      collected_at TEXT,
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

    CREATE TABLE IF NOT EXISTS run_realtime_details (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      run_id INTEGER NOT NULL,
      batch_uid TEXT,
      source_row_id TEXT NOT NULL,
      building TEXT NOT NULL,
      floor REAL,
      sub_area TEXT,
      page_name TEXT,
      name TEXT,
      source_file TEXT,
      source_updated_at TEXT,
      payload_json TEXT NOT NULL,
      FOREIGN KEY(run_id) REFERENCES collection_runs(id) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX IF NOT EXISTS ux_run_realtime_details_row
      ON run_realtime_details(run_id, source_row_id);
    CREATE INDEX IF NOT EXISTS idx_run_realtime_details_run_building
      ON run_realtime_details(run_id, building);

    CREATE TABLE IF NOT EXISTS run_area_group_rules (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      run_id INTEGER NOT NULL,
      group_id INTEGER NOT NULL,
      group_key TEXT NOT NULL DEFAULT '',
      group_name TEXT NOT NULL DEFAULT '',
      enabled INTEGER NOT NULL DEFAULT 1,
      rule_order INTEGER NOT NULL,
      building TEXT NOT NULL,
      zuo TEXT NOT NULL DEFAULT '-',
      floor_label TEXT NOT NULL DEFAULT '',
      floor_value REAL,
      match_mode TEXT NOT NULL,
      keywords TEXT NOT NULL DEFAULT '',
      note TEXT NOT NULL DEFAULT '',
      FOREIGN KEY(run_id) REFERENCES collection_runs(id) ON DELETE CASCADE
    );
    CREATE INDEX IF NOT EXISTS idx_run_area_group_rules_run_group
      ON run_area_group_rules(run_id, group_id, rule_order, id);

    CREATE TABLE IF NOT EXISTS floor_catalog (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      building TEXT NOT NULL,
      floor_label TEXT NOT NULL,
      floor_value REAL,
      source TEXT NOT NULL DEFAULT 'manual',
      enabled INTEGER NOT NULL DEFAULT 1,
      note TEXT NOT NULL DEFAULT '',
      created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60)),
      updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f', 'now', 'localtime') || printf('%+.2d:%02d', CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 3600 AS INTEGER), abs(CAST((strftime('%s','now','localtime') - strftime('%s','now')) / 60 AS INTEGER)) % 60))
    );
    CREATE UNIQUE INDEX IF NOT EXISTS idx_floor_catalog_key
      ON floor_catalog(building, floor_label);
  `);

  try { db.exec("ALTER TABLE collection_runs ADD COLUMN quality_summary TEXT NOT NULL DEFAULT '{}'"); } catch {}
  try { db.exec('ALTER TABLE collection_runs ADD COLUMN run_no INTEGER'); } catch {}
  try { db.exec('ALTER TABLE collection_runs ADD COLUMN is_anomaly INTEGER NOT NULL DEFAULT 0'); } catch {}
  try { db.exec("ALTER TABLE collection_runs ADD COLUMN collection_mode TEXT NOT NULL DEFAULT ''"); } catch {}
  try { db.exec('ALTER TABLE collection_runs ADD COLUMN restored_from_run_id INTEGER'); } catch {}
  try { db.exec("ALTER TABLE collection_runs ADD COLUMN batch_uid TEXT NOT NULL DEFAULT ''"); } catch {}
  try { db.exec("ALTER TABLE collection_runs ADD COLUMN lifecycle_state TEXT NOT NULL DEFAULT 'completed'"); } catch {}
  try { db.exec('ALTER TABLE collection_runs ADD COLUMN current_revision_uid TEXT'); } catch {}
  try { db.exec('ALTER TABLE collection_runs ADD COLUMN restored_from_batch_uid TEXT'); } catch {}
  try { db.exec('ALTER TABLE buildings ADD COLUMN updated_at TEXT'); } catch {}
  try { db.exec('ALTER TABLE sub_areas ADD COLUMN sub_idx INT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN raw_count INT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN unique_count INT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN duplicate_names TEXT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN quality_reason TEXT'); } catch {}
  try { db.exec('ALTER TABLE run_pages ADD COLUMN quality_reason TEXT'); } catch {}
  try { db.exec('ALTER TABLE pages ADD COLUMN collected_at TEXT'); } catch {}
  try { db.exec('ALTER TABLE run_pages ADD COLUMN collected_at TEXT'); } catch {}
  try { db.exec('ALTER TABLE run_realtime_details ADD COLUMN batch_uid TEXT'); } catch {}
  try { db.exec('ALTER TABLE cards ADD COLUMN indicator TEXT'); } catch {}
  try { db.exec("ALTER TABLE current_data_sources ADD COLUMN state TEXT NOT NULL DEFAULT 'bound'"); } catch {}
  try { db.exec("ALTER TABLE current_data_sources ADD COLUMN reason TEXT NOT NULL DEFAULT ''"); } catch {}

  const now = formatLocalTimestamp();
  const legacyRuns = db.prepare(`
    SELECT id, run_no, run_key, batch_uid
    FROM collection_runs
    ORDER BY id
  `).all();
  const usedRunNumbers = new Set();
  const updateRunNumber = db.prepare('UPDATE collection_runs SET run_no = ? WHERE id = ?');
  const updateBatchUid = db.prepare('UPDATE collection_runs SET batch_uid = ? WHERE id = ?');
  const updateRunKey = db.prepare('UPDATE collection_runs SET run_key = ? WHERE id = ?');
  for (const run of legacyRuns) {
    let runNo = Number(run.run_no);
    if (!Number.isInteger(runNo) || runNo <= 0 || usedRunNumbers.has(runNo)) {
      runNo = 1;
      while (usedRunNumbers.has(runNo)) runNo += 1;
      updateRunNumber.run(runNo, run.id);
    }
    usedRunNumbers.add(runNo);
    if (!String(run.batch_uid || '').trim()) {
      updateBatchUid.run(crypto.randomUUID(), run.id);
    }
    if (!String(run.run_key || '').trim()) {
      updateRunKey.run(`legacy_run_${run.id}_${crypto.randomUUID()}`, run.id);
    }
  }

  const identityRows = db.prepare(`
    SELECT id, run_key, batch_uid, imported_at, completed_at
    FROM collection_runs
    ORDER BY id
  `).all();
  const registerTechnicalId = db.prepare(`
    INSERT OR IGNORE INTO run_id_registry (technical_id, allocated_at, allocation_kind)
    VALUES (?, ?, 'collection_run')
  `);
  const register = db.prepare(`
    INSERT INTO run_key_registry (run_key, batch_uid, first_seen_at, last_run_id)
    VALUES (?, ?, ?, ?)
    ON CONFLICT(run_key) DO UPDATE SET
      batch_uid = CASE WHEN run_key_registry.batch_uid = '' THEN excluded.batch_uid ELSE run_key_registry.batch_uid END,
      last_run_id = excluded.last_run_id
  `);
  for (const run of identityRows) {
    registerTechnicalId.run(run.id, run.imported_at || run.completed_at || now);
    register.run(
      run.run_key,
      run.batch_uid,
      run.imported_at || run.completed_at || now,
      run.id);
  }
  try {
    const sequence = db.prepare(`
      SELECT seq
      FROM sqlite_sequence
      WHERE name = 'collection_runs'
      LIMIT 1
    `).get();
    const highWaterMark = Number(sequence?.seq || 0);
    if (Number.isInteger(highWaterMark) && highWaterMark > 0) {
      registerTechnicalId.run(highWaterMark, now);
    }
  } catch {
    // Legacy databases without AUTOINCREMENT do not have sqlite_sequence.
  }
  try {
    db.exec('CREATE UNIQUE INDEX IF NOT EXISTS ux_collection_runs_run_no ON collection_runs(run_no) WHERE run_no IS NOT NULL');
    db.exec('CREATE UNIQUE INDEX IF NOT EXISTS ux_collection_runs_batch_uid ON collection_runs(batch_uid) WHERE batch_uid <> \'\'');
  } catch (error) {
    throw new Error('批次身份不唯一，已停止历史库操作：' + error.message);
  }
  ensureLegacyCurrentDataState(db);
}

function ensureLegacyCurrentDataState(db) {
  const sourceCount = db.prepare('SELECT COUNT(*) AS c FROM current_data_sources').get().c;
  if (sourceCount > 0 || tableCount(db, 'cards') === 0 || tableCount(db, 'buildings') === 0) return;
  const revisionUid = `legacy-unresolved-${crypto.randomUUID()}`;
  const now = formatLocalTimestamp();
  const tx = db.transaction(() => {
    db.prepare(`
      INSERT INTO current_data_state (id, revision_uid, updated_at, source)
      VALUES (1, ?, ?, '本机旧库迁移，来源未确定')
      ON CONFLICT(id) DO UPDATE SET revision_uid=excluded.revision_uid, updated_at=excluded.updated_at, source=excluded.source
    `).run(revisionUid, now);
    db.prepare(`
      INSERT INTO current_data_sources
        (building, revision_uid, run_id, batch_uid, source_updated_at, card_count, state, reason)
      SELECT b.building, ?, NULL, NULL, b.updated_at,
             (SELECT COUNT(*) FROM cards c JOIN pages p ON p.id = c.page_id JOIN sub_areas sa ON sa.id = p.sub_area_id WHERE sa.building = b.building),
             'unresolved', '旧数据库未记录当前数据来源，禁止自动绑定到最新历史批次'
      FROM buildings b
    `).run(revisionUid);
  });
  tx();
}

function uniqueRunKey(db, base) {
  let key = base;
  let n = 1;
  const exists = db.prepare(`
    SELECT 1 FROM collection_runs WHERE run_key = ?
    UNION ALL
    SELECT 1 FROM run_key_registry WHERE run_key = ?
    LIMIT 1
  `);
  while (exists.get(key, key)) {
    n += 1;
    key = `${base}_${n}`;
  }
  return key;
}

function nextCollectionRunId(db) {
  const row = db.prepare(`
    SELECT MAX(value) + 1 AS id
    FROM (
      SELECT COALESCE(MAX(id), 0) AS value FROM collection_runs
      UNION ALL
      SELECT COALESCE(MAX(technical_id), 0) AS value FROM run_id_registry
    )
  `).get();
  return Number(row.id);
}

function nextRunNumber(db) {
  const row = db.prepare(`
    SELECT COALESCE(
      (
        SELECT MIN(candidate)
        FROM (
          SELECT 1 AS candidate
          UNION ALL
          SELECT run_no + 1
          FROM collection_runs
          WHERE run_no IS NOT NULL AND run_no > 0
        ) candidates
        WHERE NOT EXISTS (
          SELECT 1
          FROM collection_runs existing
          WHERE existing.run_no = candidates.candidate
        )
      ),
      1
    ) AS run_no
  `).get();
  return Number(row.run_no);
}

function normalizeStoredTimestamp(value, fallback = formatLocalTimestamp()) {
  const text = value instanceof Date
    ? value.toISOString()
    : String(value ?? '').trim();
  if (!text) return fallback;

  // Legacy EMS exports omitted the offset. Treat those values as UTC once;
  // new and offset-bearing values retain their represented instant.
  const candidate = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(text) ? text : `${text}Z`;
  const parsed = new Date(candidate);
  return Number.isNaN(parsed.getTime()) ? fallback : formatLocalTimestamp(parsed);
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
    SELECT id, run_no, run_key, started_at, completed_at, imported_at, status, scope,
           buildings, json_path, db_snapshot_path, card_count, on_count,
           off_count, offline_count, unknown_count, quality_summary, is_anomaly, note
    FROM collection_runs
    ORDER BY datetime(COALESCE(imported_at, completed_at)) DESC, id DESC
    LIMIT ?
  `).all(limit).map(r => ({
    ...r,
    started_at: r.started_at ? normalizeStoredTimestamp(r.started_at, r.started_at) : r.started_at,
    completed_at: normalizeStoredTimestamp(r.completed_at, r.completed_at),
    imported_at: normalizeStoredTimestamp(r.imported_at, r.imported_at),
    buildings: parseJsonArray(r.buildings),
    is_anomaly: Number(r.is_anomaly || 0),
    label: runLabel(r),
  }));
}

function runLabel(run) {
  const normalized = normalizeStoredTimestamp(
    run.imported_at || run.completed_at,
    run.completed_at);
  const dt = new Date(normalized);
  const pad = n => String(n).padStart(2, '0');
  const ts = Number.isNaN(dt.getTime())
    ? normalized
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
  const run = db.prepare('SELECT id, run_key, batch_uid, completed_at, scope, buildings FROM collection_runs WHERE id = ?').get(id);
  if (!run) throw new Error('Run not found: ' + runId);
  const runCompletedAt = normalizeStoredTimestamp(run.completed_at);
  const sourceBuildings = parseJsonArray(run.buildings);
  const isPartial = String(run.scope || '').toLowerCase() === 'partial';
  if (isPartial && sourceBuildings.length === 0) {
    throw new Error('部分批次没有楼栋范围，无法安全恢复');
  }

  const tx = db.transaction(() => {
    if (isPartial) {
      const placeholders = sourceBuildings.map(() => '?').join(',');
      db.prepare(`
        DELETE FROM cards
        WHERE page_id IN (
          SELECT p.id FROM pages p
          JOIN sub_areas sa ON sa.id = p.sub_area_id
          WHERE sa.building IN (${placeholders})
        )
      `).run(...sourceBuildings);
      db.prepare(`
        DELETE FROM pages
        WHERE sub_area_id IN (SELECT id FROM sub_areas WHERE building IN (${placeholders}))
      `).run(...sourceBuildings);
      db.prepare(`DELETE FROM sub_areas WHERE building IN (${placeholders})`).run(...sourceBuildings);
      db.prepare(`DELETE FROM buildings WHERE building IN (${placeholders})`).run(...sourceBuildings);
    } else {
      db.exec('DELETE FROM cards; DELETE FROM pages; DELETE FROM sub_areas; DELETE FROM buildings;');
    }

    const insertBuilding = db.prepare(`
      INSERT INTO buildings (building, sub_area_count, menu_clicked, updated_at)
      VALUES (?, ?, ?, ?)
    `);
    const insertSA = db.prepare(`
      INSERT INTO sub_areas (building, sub_idx, text, floor, x, y)
      VALUES (?, ?, ?, ?, ?, ?)
    `);
    const insertPage = db.prepare(`
      INSERT INTO pages (sub_area_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, collected_at, err)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
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
      insertBuilding.run(
        b.building,
        b.sub_area_count,
        b.menu_clicked,
        normalizeStoredTimestamp(b.updated_at, runCompletedAt));
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
      SELECT id, run_sub_area_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, collected_at, err
      FROM run_pages
      WHERE run_id = ?
      ORDER BY id
    `).all(id);
    const pageMap = new Map();
    for (const p of runPageRows) {
      const saId = saMap.get(p.run_sub_area_id);
      if (!saId) continue;
      const res = insertPage.run(
        saId,
        p.page_name,
        p.count,
        p.raw_count,
        p.unique_count,
        p.duplicate_names,
        p.on_href,
        p.off_href,
        p.layout,
        p.quality_reason,
        normalizeStoredTimestamp(p.collected_at, runCompletedAt),
        p.err);
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
    const revisionUid = crypto.randomUUID();
    db.prepare(`
      INSERT INTO current_data_state (id, revision_uid, updated_at, source)
      VALUES (1, ?, ?, '本机 SQLite')
      ON CONFLICT(id) DO UPDATE SET revision_uid=excluded.revision_uid, updated_at=excluded.updated_at, source=excluded.source
    `).run(revisionUid, formatLocalTimestamp());
    if (isPartial) {
      db.prepare('UPDATE current_data_sources SET revision_uid = ? WHERE building IN (' + sourceBuildings.map(() => '?').join(',') + ')').run(revisionUid, ...sourceBuildings);
    } else {
      db.prepare('DELETE FROM current_data_sources').run();
    }
    const upsertSource = db.prepare(`
      INSERT INTO current_data_sources
        (building, revision_uid, run_id, batch_uid, source_updated_at, card_count, state, reason)
      VALUES (?, ?, ?, ?, ?, ?, 'bound', '')
      ON CONFLICT(building) DO UPDATE SET
        revision_uid=excluded.revision_uid, run_id=excluded.run_id, batch_uid=excluded.batch_uid,
        source_updated_at=excluded.source_updated_at, card_count=excluded.card_count,
        state=excluded.state, reason=excluded.reason
    `);
    for (const building of sourceBuildings) {
      const count = db.prepare(`
        SELECT COUNT(*) AS c
        FROM cards c JOIN pages p ON p.id = c.page_id JOIN sub_areas sa ON sa.id = p.sub_area_id
        WHERE sa.building = ?
      `).get(building).c;
      upsertSource.run(building, revisionUid, id, run.batch_uid || null, runCompletedAt, Number(count || 0));
    }
    db.prepare('UPDATE collection_runs SET current_revision_uid = ? WHERE id = ?').run(revisionUid, id);
  });

  tx();
  return {
    id,
    run_key: run.run_key,
    completed_at: runCompletedAt,
    buildings: parseJsonArray(run.buildings),
  };
}

function deleteRun(db, runId) {
  ensureHistorySchema(db);
  const id = resolveRunId(db, runId);
  if (!id) throw new Error('Run id is required');
  const run = db.prepare('SELECT id, run_key, batch_uid, completed_at, card_count, scope, buildings FROM collection_runs WHERE id = ?').get(id);
  if (!run) throw new Error('Run not found: ' + runId);
  const sourceRows = db.prepare('SELECT building, state, run_id, batch_uid, reason FROM current_data_sources').all();
  if (String(run.scope || '').toLowerCase() === 'full' && sourceRows.length === 0) {
    throw new Error('当前数据来源记录为空，不能安全删除全量历史批次');
  }
  const declaredBuildings = new Set(parseJsonArray(run.buildings));
  const overlap = sourceRows.filter(row => declaredBuildings.has(row.building));
  if (overlap.some(row => row.run_id === id || row.batch_uid === run.batch_uid)) {
    throw new Error('当前数据正在使用该批次，不能删除');
  }
  if (overlap.some(row => String(row.state || 'bound').toLowerCase() !== 'bound')) {
    throw new Error('当前数据来源未确定，不能安全删除重叠楼栋的历史批次');
  }
  if (db.prepare("SELECT 1 FROM collection_runs WHERE id <> ? AND restored_from_run_id = ? LIMIT 1").get(id, id)) {
    throw new Error('仍有恢复/备份记录依赖该批次，不能删除');
  }
  const tx = db.transaction(() => {
    db.prepare('DELETE FROM run_realtime_details WHERE run_id = ?').run(id);
    db.prepare('DELETE FROM run_cards WHERE run_id = ?').run(id);
    db.prepare('DELETE FROM run_pages WHERE run_id = ?').run(id);
    db.prepare('DELETE FROM run_sub_areas WHERE run_id = ?').run(id);
    db.prepare('DELETE FROM run_buildings WHERE run_id = ?').run(id);
    db.prepare('DELETE FROM collection_runs WHERE id = ?').run(id);
    db.prepare(`
      UPDATE run_key_registry
      SET deleted_at = ?, last_run_id = ?
      WHERE run_key = ? AND deleted_at IS NULL
    `).run(formatLocalTimestamp(), id, run.run_key);
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

  const now = normalizeStoredTimestamp(options.completedAt);
  const startedAt = options.startedAt
    ? normalizeStoredTimestamp(options.startedAt, now)
    : null;
  const requestedRunKey = options.runKey || localRunKey(new Date(now));
  const batchUid = options.batchUid || crypto.randomUUID();
  const revisionUid = options.currentRevisionUid || crypto.randomUUID();
  const scope = selected.length && selected.length < BLDG_ORDER.length ? 'partial' : 'full';
  const buildings = selected.length ? selected : db.prepare('SELECT DISTINCT building FROM sub_areas ORDER BY building').all().map(r => r.building);
  const hasAreaRuleSnapshotSource = hasTable(db, 'monitor_groups') &&
    hasTable(db, 'area_group_rules') &&
    hasColumn(db, 'monitor_groups', 'group_key');

  const insertRun = db.prepare(`
    INSERT INTO collection_runs
      (id, run_no, run_key, batch_uid, started_at, completed_at, imported_at, status, scope, buildings, json_path, db_snapshot_path, note, lifecycle_state, current_revision_uid, collection_mode)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
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
      (run_id, run_sub_area_id, source_page_id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, collected_at, err)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
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
  const insertAreaGroupRules = hasAreaRuleSnapshotSource
    ? db.prepare(`
        INSERT INTO run_area_group_rules
          (run_id, group_id, group_key, group_name, enabled, rule_order, building, zuo,
           floor_label, floor_value, match_mode, keywords, note)
        SELECT ?, g.id, g.group_key, g.name, g.enabled, r.rule_order, r.building, r.zuo,
               r.floor_label, r.floor_value, r.match_mode, r.keywords, r.note
        FROM area_group_rules r
        JOIN monitor_groups g ON g.id = r.group_id
        WHERE COALESCE(g.group_key, '') <> ''
      `)
    : null;

  const tx = db.transaction(() => {
    const runKey = uniqueRunKey(db, requestedRunKey);
    const runId = nextCollectionRunId(db);
    const runNo = nextRunNumber(db);
    db.prepare(`
      INSERT INTO run_id_registry (technical_id, allocated_at, allocation_kind)
      VALUES (?, ?, 'collection_run')
    `).run(runId, now);
    const res = insertRun.run(
      runId,
      runNo,
      runKey,
      batchUid,
      startedAt,
      now,
      formatLocalTimestamp(),
      options.status || 'completed',
      scope,
      JSON.stringify(buildings),
      options.jsonPath || null,
      options.dbSnapshotPath || null,
      options.note || '',
      options.lifecycleState || options.status || 'completed',
      revisionUid,
      options.collectionMode || ''
    );
    const insertedRunId = Number(res.lastInsertRowid);
    if (insertedRunId !== runId) {
      throw new Error(`Allocated collection run id ${runId}, but SQLite inserted ${insertedRunId}`);
    }
    if (insertAreaGroupRules) insertAreaGroupRules.run(runId);
    const bRows = db.prepare(`
      SELECT building, sub_area_count, menu_clicked, updated_at
      FROM buildings
      ${buildings.length ? `WHERE building IN (${buildings.map(() => '?').join(',')})` : ''}
      ORDER BY building
    `).all(...buildings);
    for (const b of bRows) {
      insertRunBuilding.run(
        runId,
        b.building,
        b.sub_area_count,
        b.menu_clicked,
        normalizeStoredTimestamp(b.updated_at, now));
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
        SELECT id, page_name, count, raw_count, unique_count, duplicate_names, on_href, off_href, layout, quality_reason, collected_at, err
        FROM pages
        WHERE sub_area_id = ?
        ORDER BY id
      `).all(sa.id);
      for (const p of pageRows) {
        const pageRes = insertRunPage.run(
          runId,
          runSaId,
          p.id,
          p.page_name,
          p.count,
          p.raw_count,
          p.unique_count,
          p.duplicate_names,
          p.on_href,
          p.off_href,
          p.layout,
          p.quality_reason,
          normalizeStoredTimestamp(p.collected_at, now),
          p.err);
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
    db.prepare(`
      INSERT INTO run_key_registry (run_key, batch_uid, first_seen_at, last_run_id)
      VALUES (?, ?, ?, ?)
      ON CONFLICT(run_key) DO UPDATE SET last_run_id = excluded.last_run_id
    `).run(runKey, batchUid, now, runId);
    const sourceBuildings = buildings.map(String);
    const updateRevision = db.prepare(`
      INSERT INTO current_data_state (id, revision_uid, updated_at, source)
      VALUES (1, ?, ?, '本机 SQLite')
      ON CONFLICT(id) DO UPDATE SET revision_uid = excluded.revision_uid, updated_at = excluded.updated_at
    `);
    updateRevision.run(revisionUid, now);
    if (scope === 'partial' && sourceBuildings.length) {
      db.prepare(
        'UPDATE current_data_sources SET revision_uid = ? WHERE building IN (' + sourceBuildings.map(() => '?').join(',') + ')',
      ).run(revisionUid, ...sourceBuildings);
    }
    const upsertSource = db.prepare(`
      INSERT INTO current_data_sources (building, revision_uid, run_id, batch_uid, source_updated_at, card_count, state, reason)
      VALUES (?, ?, ?, ?, ?, ?, 'bound', '')
      ON CONFLICT(building) DO UPDATE SET
        revision_uid = excluded.revision_uid,
        run_id = excluded.run_id,
        batch_uid = excluded.batch_uid,
        source_updated_at = excluded.source_updated_at,
        card_count = excluded.card_count,
        state = excluded.state,
        reason = excluded.reason
    `);
    for (const building of sourceBuildings) {
      upsertSource.run(
        building,
        revisionUid,
        runId,
        batchUid,
        now,
        Number(db.prepare('SELECT COUNT(*) AS c FROM run_cards WHERE run_id = ? AND EXISTS (SELECT 1 FROM run_pages p JOIN run_sub_areas sa ON sa.id = p.run_sub_area_id WHERE p.id = run_cards.run_page_id AND p.run_id = run_cards.run_id AND sa.building = ?)').get(runId, building).c || 0));
    }
    syncFloorCatalogFromCurrent(db);
    return runId;
  });

  return tx.immediate();
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
  let completedAt = formatLocalTimestamp();
  try {
    const row = db.prepare('SELECT MAX(updated_at) AS t FROM buildings WHERE updated_at IS NOT NULL').get();
    if (row && row.t) completedAt = normalizeStoredTimestamp(row.t, completedAt);
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

function hasTable(db, table) {
  return Boolean(db.prepare("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?").get(table));
}

function hasColumn(db, table, column) {
  try {
    return db.prepare(`PRAGMA table_info(${table})`).all().some(row => row.name === column);
  } catch {
    return false;
  }
}

function syncFloorCatalogFromCurrent(db) {
  ensureHistorySchema(db);
  const now = formatLocalTimestamp();
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
  const now = formatLocalTimestamp();
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
  normalizeStoredTimestamp,
  sourceForRun,
  createRunFromCurrent,
  seedCurrentRun,
  syncFloorCatalogFromCurrent,
  saveFloorCatalog,
  loadFloorCatalog,
};
