const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const Database = require('better-sqlite3');
const { ensureHistorySchema, createRunFromCurrent, listRuns } = require('../src/data-history');

function createFixture() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-scout-time-'));
  const file = path.join(dir, 'ac.db');
  const db = new Database(file);
  db.exec(`
    CREATE TABLE buildings (building TEXT PRIMARY KEY, sub_area_count INTEGER, menu_clicked TEXT, updated_at TEXT);
    CREATE TABLE sub_areas (id INTEGER PRIMARY KEY AUTOINCREMENT, building TEXT NOT NULL, sub_idx INTEGER, floor REAL, text TEXT, x INTEGER, y INTEGER);
    CREATE TABLE pages (id INTEGER PRIMARY KEY AUTOINCREMENT, sub_area_id INTEGER NOT NULL, page_name TEXT, count INTEGER, raw_count INTEGER, unique_count INTEGER, duplicate_names TEXT, on_href TEXT, off_href TEXT, layout TEXT, quality_reason TEXT, collected_at TEXT, err TEXT);
    CREATE TABLE cards (id INTEGER PRIMARY KEY AUTOINCREMENT, page_id INTEGER NOT NULL, name TEXT, switch TEXT, mode TEXT, indoor TEXT, set_temp TEXT, fan TEXT, indicator TEXT, comm TEXT);
    INSERT INTO buildings VALUES ('1号', 1, '1号楼', '2026-09-16T15:23:16+08:00');
    INSERT INTO sub_areas VALUES (1, '1号', 0, 1, '1F', 0, 0);
    INSERT INTO pages VALUES (1, 1, 'default', 1, 1, 1, '', '', '', 'grid', '', '2026-09-16T15:23:16+08:00', NULL);
    INSERT INTO cards VALUES (1, 1, '1-0101-KT', 'OFF', '制冷', '26', '25', '中', 'green.png', '关机');
  `);
  ensureHistorySchema(db);
  return { db, dir };
}

test('delayed import never persists a negative batch duration', () => {
  const { db, dir } = createFixture();
  try {
    const runId = createRunFromCurrent(db, {
      startedAt: '2026-09-17T00:34:01+08:00',
      completedAt: '2026-09-16T15:23:16+08:00',
      runKey: 'delayed-import',
    });
    const row = db.prepare('SELECT started_at, completed_at, duration_ms FROM collection_runs WHERE id = ?').get(runId);
    assert.ok(!row.started_at || Date.parse(row.started_at) <= Date.parse(row.completed_at));
    assert.ok(row.duration_ms === null || row.duration_ms >= 0);
  } finally {
    db.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('valid batch timestamps persist a monotonic duration', () => {
  const { db, dir } = createFixture();
  try {
    const runId = createRunFromCurrent(db, {
      startedAt: '2026-09-16T15:22:16+08:00',
      completedAt: '2026-09-16T15:23:16+08:00',
      runKey: 'valid-duration',
    });
    const row = db.prepare('SELECT duration_ms FROM collection_runs WHERE id = ?').get(runId);
    assert.equal(row.duration_ms, 60_000);
  } finally {
    db.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('history list orders batches by completion time rather than import time', () => {
  const { db, dir } = createFixture();
  try {
    createRunFromCurrent(db, {
      completedAt: '2026-09-16T15:23:16+08:00',
      runKey: 'completed-first',
    });
    createRunFromCurrent(db, {
      completedAt: '2026-09-17T15:23:16+08:00',
      runKey: 'completed-second',
    });
    const rows = listRuns(db, { seed: false, limit: 10 });
    assert.equal(rows[0].run_key, 'completed-second');
  } finally {
    db.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
