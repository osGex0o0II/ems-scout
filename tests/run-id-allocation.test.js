'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const Database = require('better-sqlite3');
const { ensureHistorySchema, createRunFromCurrent } = require('../src/data-history');

test('allocates the lowest unused collection run id after a historical run is deleted', () => {
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
      INSERT INTO collection_runs (id, run_key, completed_at, imported_at, status, scope, buildings)
        VALUES (1, 'old-1', '2026-09-15T00:00:00+08:00', '2026-09-15T00:00:00+08:00', 'completed', 'full', '["1号"]');
      INSERT INTO collection_runs (id, run_key, completed_at, imported_at, status, scope, buildings)
        VALUES (3, 'old-3', '2026-09-16T00:00:00+08:00', '2026-09-16T00:00:00+08:00', 'completed', 'full', '["1号"]');
    `);

    const runId = createRunFromCurrent(db, {
      completedAt: '2026-09-17T00:01:00+08:00',
      runKey: 'new-run',
    });

    assert.equal(runId, 2);
    assert.deepEqual(
      db.prepare('SELECT id FROM collection_runs ORDER BY id').all().map(row => row.id),
      [1, 2, 3],
    );
  } finally {
    db.close();
    fs.rmSync(root, { recursive: true, force: true });
  }
});
