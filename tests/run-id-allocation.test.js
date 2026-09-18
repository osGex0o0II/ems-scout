'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const Database = require('better-sqlite3');
const { ensureHistorySchema, createRunFromCurrent, deleteRun, restoreCurrentFromRun } = require('../src/data-history');

test('keeps the internal primary key monotonic while reusing the display batch number', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-scout-run-id-'));
  const databasePath = path.join(root, 'ac.db');
  const db = new Database(databasePath);
  try {
    ensureHistorySchema(db);
    db.exec(`
      CREATE TABLE buildings (
        building TEXT PRIMARY KEY,
        sub_area_count INTEGER,
        menu_clicked TEXT,
        updated_at TEXT
      );
      CREATE TABLE sub_areas (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        building TEXT NOT NULL,
        sub_idx INTEGER,
        floor REAL,
        text TEXT,
        x INTEGER,
        y INTEGER
      );
      CREATE TABLE pages (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        sub_area_id INTEGER NOT NULL,
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
        err TEXT
      );
      CREATE TABLE cards (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        page_id INTEGER NOT NULL,
        name TEXT,
        switch TEXT,
        mode TEXT,
        indoor TEXT,
        set_temp TEXT,
        fan TEXT,
        indicator TEXT,
        comm TEXT
      );
      INSERT INTO buildings VALUES ('1号', 1, 'yes', '2026-09-17T00:00:00+08:00');
      INSERT INTO sub_areas VALUES (1, '1号', 0, 1, '1F', 100, 100);
      INSERT INTO pages VALUES (1, 1, '一页', 1, 1, 1, '', '', '', 'grid', '', '2026-09-17T00:00:00+08:00', '');
      INSERT INTO cards VALUES (1, 1, '1-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机');
    `);

    const firstRunId = createRunFromCurrent(db, {
      completedAt: '2026-09-17T00:01:00+08:00',
      runKey: 'first-run',
    });

    db.prepare("UPDATE current_data_sources SET state = 'bound', run_id = 999, batch_uid = 'external-current' WHERE building = '1号'").run();
    deleteRun(db, firstRunId);
    const secondRunId = createRunFromCurrent(db, {
      completedAt: '2026-09-17T00:02:00+08:00',
      runKey: 'second-run',
    });

    assert.equal(firstRunId, 1);
    assert.equal(secondRunId, 2);
    assert.deepEqual(
      db.prepare('SELECT id, run_no FROM collection_runs ORDER BY id').all(),
      [{ id: 2, run_no: 1 }],
    );
  } finally {
    db.close();
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('does not reuse a deleted run key or internal numeric id', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-history-'));
  const dbPath = path.join(dir, 'history.db');
  let db;
  try {
    db = new Database(dbPath);
    ensureHistorySchema(db);
    db.exec(`
      CREATE TABLE buildings (building TEXT PRIMARY KEY, sub_area_count INTEGER, menu_clicked TEXT, updated_at TEXT);
      CREATE TABLE sub_areas (id INTEGER PRIMARY KEY AUTOINCREMENT, building TEXT NOT NULL, sub_idx INTEGER, floor REAL, text TEXT, x INTEGER, y INTEGER);
      CREATE TABLE pages (id INTEGER PRIMARY KEY AUTOINCREMENT, sub_area_id INTEGER NOT NULL, page_name TEXT, count INTEGER, raw_count INTEGER, unique_count INTEGER, duplicate_names TEXT, on_href TEXT, off_href TEXT, layout TEXT, quality_reason TEXT, collected_at TEXT, err TEXT);
      CREATE TABLE cards (id INTEGER PRIMARY KEY AUTOINCREMENT, page_id INTEGER NOT NULL, name TEXT, switch TEXT, mode TEXT, indoor TEXT, set_temp TEXT, fan TEXT, indicator TEXT, comm TEXT);
      INSERT INTO buildings VALUES ('1号', 1, 'yes', '2026-09-17T00:00:00+08:00');
      INSERT INTO sub_areas VALUES (1, '1号', 0, 1, '1F', 100, 100);
      INSERT INTO pages VALUES (1, 1, '一页', 1, 1, 1, '', '', '', 'grid', '', '2026-09-17T00:00:00+08:00', '');
      INSERT INTO cards VALUES (1, 1, '1-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机');
    `);
    const first = createRunFromCurrent(db, {
      buildings: ['1号'],
      completedAt: '2026-09-17T01:00:00+08:00',
      runKey: 'old-1',
    });
    db.prepare("UPDATE current_data_sources SET state = 'bound', run_id = 999, batch_uid = 'external-current' WHERE building = '1号'").run();
    deleteRun(db, first);
    const second = createRunFromCurrent(db, {
      buildings: ['1号'],
      completedAt: '2026-09-17T02:00:00+08:00',
      runKey: 'old-1',
    });
    assert.equal(second, first + 1);
    assert.equal(db.prepare('SELECT run_no FROM collection_runs WHERE id = ?').get(second).run_no, 1);
    assert.notEqual(db.prepare('SELECT run_key FROM collection_runs WHERE id = ?').get(second).run_key, 'old-1');
  } finally {
    db?.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('fails closed when a full batch has no current data source records', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-history-empty-sources-'));
  const dbPath = path.join(dir, 'history.db');
  const db = new Database(dbPath);
  try {
    ensureHistorySchema(db);
    db.prepare(`
      INSERT INTO collection_runs
        (id, run_no, run_key, batch_uid, completed_at, imported_at, status, scope, buildings)
      VALUES (?, ?, ?, ?, ?, ?, 'completed', 'full', ?)
    `).run(
      7,
      7,
      'empty-source-run',
      'batch-empty-source',
      '2026-09-18T00:00:00+08:00',
      '2026-09-18T00:00:00+08:00',
      JSON.stringify(['1号', '2号', '3号', '4号', '5号', '6号']),
    );

    assert.throws(
      () => deleteRun(db, 7),
      /当前数据来源记录为空/,
    );
    assert.equal(db.prepare('SELECT COUNT(*) AS c FROM collection_runs WHERE id = 7').get().c, 1);
  } finally {
    db.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('imports the legacy sqlite_sequence high-water mark into the id registry', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-history-sequence-'));
  const dbPath = path.join(dir, 'history.db');
  const db = new Database(dbPath);
  try {
    db.exec(`
      CREATE TABLE collection_runs (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        run_key TEXT UNIQUE,
        completed_at TEXT NOT NULL,
        imported_at TEXT NOT NULL,
        status TEXT NOT NULL DEFAULT 'completed',
        scope TEXT NOT NULL DEFAULT 'full',
        buildings TEXT NOT NULL DEFAULT '[]'
      );
      INSERT INTO collection_runs (id, run_key, completed_at, imported_at, buildings)
      VALUES (41, 'deleted-41', '2026-09-17T00:00:00Z', '2026-09-17T00:00:00Z', '["1号"]');
      DELETE FROM collection_runs WHERE id = 41;
    `);

    ensureHistorySchema(db);

    assert.equal(db.prepare('SELECT MAX(technical_id) AS value FROM run_id_registry').get().value, 41);
  } finally {
    db.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('partial restore preserves current data from buildings outside the snapshot scope', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-history-partial-'));
  const dbPath = path.join(dir, 'history.db');
  const db = new Database(dbPath);
  try {
    ensureHistorySchema(db);
    db.exec(`
      CREATE TABLE buildings (building TEXT PRIMARY KEY, sub_area_count INTEGER, menu_clicked TEXT, updated_at TEXT);
      CREATE TABLE sub_areas (id INTEGER PRIMARY KEY AUTOINCREMENT, building TEXT NOT NULL, sub_idx INTEGER, floor REAL, text TEXT, x INTEGER, y INTEGER);
      CREATE TABLE pages (id INTEGER PRIMARY KEY AUTOINCREMENT, sub_area_id INTEGER NOT NULL, page_name TEXT, count INTEGER, raw_count INTEGER, unique_count INTEGER, duplicate_names TEXT, on_href TEXT, off_href TEXT, layout TEXT, quality_reason TEXT, collected_at TEXT, err TEXT);
      CREATE TABLE cards (id INTEGER PRIMARY KEY AUTOINCREMENT, page_id INTEGER NOT NULL, name TEXT, switch TEXT, mode TEXT, indoor TEXT, set_temp TEXT, fan TEXT, indicator TEXT, comm TEXT);
      INSERT INTO buildings VALUES ('1号', 1, 'yes', '2026-09-17T00:00:00+08:00'), ('2号', 1, 'yes', '2026-09-17T00:00:00+08:00');
      INSERT INTO sub_areas VALUES (1, '1号', 0, 1, '1F', 100, 100), (2, '2号', 0, 1, '1F', 100, 100);
      INSERT INTO pages VALUES (1, 1, '一页', 1, 1, 1, '', '', '', 'grid', '', '2026-09-17T00:00:00+08:00', ''), (2, 2, '一页', 1, 1, 1, '', '', '', 'grid', '', '2026-09-17T00:00:00+08:00', '');
      INSERT INTO cards VALUES (1, 1, '1-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机'), (2, 2, '2-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机');
    `);

    const runId = createRunFromCurrent(db, {
      buildings: ['1号'],
      completedAt: '2026-09-17T00:01:00+08:00',
      runKey: 'partial-1',
    });
    const beforeRevision = db.prepare("SELECT revision_uid FROM current_data_sources WHERE building = '2号'").get().revision_uid;
    db.prepare("UPDATE cards SET name = 'CHANGED-1' WHERE id = 1").run();

    restoreCurrentFromRun(db, runId);

    assert.equal(
      db.prepare("SELECT c.name FROM cards c JOIN pages p ON p.id = c.page_id JOIN sub_areas sa ON sa.id = p.sub_area_id WHERE sa.building = '1号'").get().name,
      '1-0101-KT',
    );
    assert.equal(
      db.prepare("SELECT c.name FROM cards c JOIN pages p ON p.id = c.page_id JOIN sub_areas sa ON sa.id = p.sub_area_id WHERE sa.building = '2号'").get().name,
      '2-0101-KT',
    );
    assert.deepEqual(
      db.prepare('SELECT building FROM buildings ORDER BY building').all(),
      [{ building: '1号' }, { building: '2号' }],
    );
    assert.equal(
      db.prepare("SELECT revision_uid FROM current_data_sources WHERE building = '2号'").get().revision_uid,
      beforeRevision,
    );
  } finally {
    db.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
