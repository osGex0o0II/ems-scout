'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');
const Database = require('better-sqlite3');
const { publishNewFile } = require('../../scripts/merge-legacy-databases');

const root = path.resolve(__dirname, '../..');
const schema = fs.readFileSync(path.join(root, 'scripts/schema.sql'), 'utf8');

function hash(file) {
  return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}

function createSource(file) {
  const db = new Database(file);
  db.exec(schema);
  db.prepare('INSERT INTO buildings VALUES (?, ?, ?)').run('1号', 1, 'legacy');
  db.prepare('INSERT INTO buildings VALUES (?, ?, ?)').run('2号', 1, 'legacy');
  const insertSubArea = db.prepare('INSERT INTO sub_areas (building, sub_idx, floor, text, x, y) VALUES (?, ?, ?, ?, ?, ?)');
  const one = Number(insertSubArea.run('1号', 0, 1, '1F', 10, 10).lastInsertRowid);
  const two = Number(insertSubArea.run('2号', 0, 2, '2F', 20, 20).lastInsertRowid);
  const insertPage = db.prepare('INSERT INTO pages (sub_area_id, page_name, count) VALUES (?, ?, NULL)');
  const pageOne = Number(insertPage.run(one, '一页').lastInsertRowid);
  const pageTwo = Number(insertPage.run(two, '一页').lastInsertRowid);
  const insertCard = db.prepare('INSERT INTO cards (page_id, name, switch, mode, indoor, set_temp, fan, comm) VALUES (?, ?, ?, ?, ?, ?, ?, ?)');
  insertCard.run(pageOne, '1-A', 'OFF', '制冷', '25', '24', '中', '关机');
  insertCard.run(pageTwo, '2-A', 'ON', '制冷', '26', '24', '中', '开机');
  insertCard.run(pageTwo, '2-A', 'ON', '制冷', '26', '24', '中', '开机');
  db.close();
}

test('legacy merge preserves mixed building identity and never opens source archives directly', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-merge-safety-'));
  const sourceRoot = path.join(directory, 'sources');
  const sourceDir = path.join(sourceRoot, 'mixed');
  const target = path.join(directory, 'merged.db');
  fs.mkdirSync(sourceDir, { recursive: true });
  const source = path.join(sourceDir, 'ac.db');
  createSource(source);
  const beforeHash = hash(source);

  const result = spawnSync(process.execPath, [path.join(root, 'scripts/merge-legacy-databases.js')], {
    cwd: root,
    env: {
      ...process.env,
      EMS_MERGE_SOURCE_ROOT: sourceRoot,
      EMS_MERGE_TARGET_PATH: target,
      EMS_MERGE_TEMP_ROOT: path.join(directory, 'temp'),
    },
    encoding: 'utf8',
  });

  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.equal(hash(source), beforeHash);
  assert.deepEqual(fs.readdirSync(sourceDir), ['ac.db']);
  const merged = new Database(target, { readonly: true, fileMustExist: true });
  assert.deepEqual(merged.prepare('SELECT building FROM buildings ORDER BY building').all(), [
    { building: '1号' },
    { building: '2号' },
  ]);
  assert.deepEqual(merged.prepare('SELECT count, raw_count, unique_count FROM pages ORDER BY id').all(), [
    { count: 1, raw_count: 1, unique_count: 1 },
    { count: 1, raw_count: 2, unique_count: 1 },
  ]);
  assert.equal(merged.prepare('SELECT COUNT(*) AS n FROM cards').get().n, 2);
  assert.deepEqual(merged.prepare(`
    SELECT r.card_count, COUNT(c.id) AS detail_count,
           r.on_count, SUM(c.comm = '开机') AS detail_on,
           r.off_count, SUM(c.comm = '关机') AS detail_off
    FROM collection_runs r
    LEFT JOIN run_cards c ON c.run_id = r.id
    GROUP BY r.id
    ORDER BY r.id
  `).all(), [
    { card_count: 1, detail_count: 1, on_count: 0, detail_on: 0, off_count: 1, detail_off: 1 },
    { card_count: 1, detail_count: 1, on_count: 1, detail_on: 1, off_count: 0, detail_off: 0 },
  ]);
  assert.deepEqual(merged.pragma('foreign_key_check'), []);
  merged.close();
  fs.rmSync(directory, { recursive: true, force: true });
});

test('quality report leaves the audited database byte-for-byte unchanged', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-quality-readonly-'));
  const dbPath = path.join(directory, 'audit.db');
  createSource(dbPath);
  const beforeHash = hash(dbPath);

  const result = spawnSync(process.execPath, [path.join(root, 'scripts/quality-report.js')], {
    cwd: root,
    env: { ...process.env, EMS_DB_PATH: dbPath, EMS_QUALITY_OUT: path.join(directory, 'report') },
    encoding: 'utf8',
  });

  assert.equal([0, 2].includes(result.status), true, result.stderr || result.stdout);
  assert.equal(hash(dbPath), beforeHash);
  assert.deepEqual(fs.readdirSync(directory).filter(name => name.startsWith('audit.db-')), []);
  const report = JSON.parse(fs.readFileSync(path.join(directory, 'report', 'quality_report.json'), 'utf8'));
  assert.equal(report.issues.some(issue => issue.code === 'page_count_mismatch'), true);
  fs.rmSync(directory, { recursive: true, force: true });
});

test('quality report does not mutate a live WAL database or its sidecars', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-quality-live-wal-'));
  const dbPath = path.join(directory, 'audit.db');
  createSource(dbPath);
  const live = new Database(dbPath);
  live.pragma('journal_mode = WAL');
  live.pragma('wal_autocheckpoint = 0');
  live.prepare('UPDATE buildings SET menu_clicked = ? WHERE building = ?').run('live-wal', '1号');
  live.prepare('SELECT COUNT(*) AS count FROM cards').get();
  const sourceFiles = [dbPath, dbPath + '-wal', dbPath + '-shm'];
  assert.equal(sourceFiles.every(file => fs.existsSync(file)), true);
  const before = Object.fromEntries(sourceFiles.map(file => [file, hash(file)]));

  const result = spawnSync(process.execPath, [path.join(root, 'scripts/quality-report.js')], {
    cwd: root,
    env: { ...process.env, EMS_DB_PATH: dbPath, EMS_QUALITY_OUT: path.join(directory, 'report') },
    encoding: 'utf8',
  });

  assert.equal([0, 2].includes(result.status), true, result.stderr || result.stdout);
  assert.deepEqual(Object.fromEntries(sourceFiles.map(file => [file, hash(file)])), before);
  assert.equal(fs.existsSync(path.join(directory, 'report', 'quality_report.json')), true);
  live.close();
  fs.rmSync(directory, { recursive: true, force: true });
});

test('legacy importer enables foreign keys explicitly', () => {
  const source = fs.readFileSync(path.join(root, 'scripts/import.js'), 'utf8');
  assert.match(source, /foreign_keys\s*=\s*ON/i);
  assert.doesNotMatch(source, /foreign_keys\s*=\s*OFF/i);
  assert.match(source, /foreign_key_check/i);
});

test('quality report opens only its owned SQLite snapshot', () => {
  const source = fs.readFileSync(path.join(root, 'scripts/quality-report.js'), 'utf8');
  assert.doesNotMatch(source, /new Database\(DB_PATH/);
  assert.match(source, /copyStableSqliteSnapshot/);
  assert.match(source, /let db;\s*try\s*\{\s*db = new Database\(snapshot\.path/,
    'snapshot cleanup must cover failures while opening the private database');
  assert.match(source, /db\?\.close\(\)/,
    'cleanup must tolerate a database constructor failure');
});

test('legacy merge help is side-effect free and errors do not leak stacks', () => {
  const help = spawnSync(process.execPath, [path.join(root, 'scripts/merge-legacy-databases.js'), '--help'], {
    cwd: root,
    encoding: 'utf8',
  });
  assert.equal(help.status, 0, help.stderr);
  assert.match(help.stdout, /--source-root/);
  assert.doesNotMatch(help.stdout + help.stderr, /data\\ac\.db|at main|node:internal/);

  const missing = spawnSync(process.execPath, [
    path.join(root, 'scripts/merge-legacy-databases.js'),
    '--source-root=' + path.join(os.tmpdir(), 'ems-merge-does-not-exist'),
    '--target=' + path.join(os.tmpdir(), 'ems-merge-never-created.db'),
  ], { cwd: root, encoding: 'utf8' });
  assert.equal(missing.status, 1);
  assert.match(missing.stderr, /^ERROR:/);
  assert.doesNotMatch(missing.stderr, /\n\s+at |node:internal/);
});

test('legacy merge publish never replaces a target created concurrently', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-merge-publish-'));
  const partial = path.join(directory, 'candidate.partial');
  const target = path.join(directory, 'target.db');
  fs.writeFileSync(partial, 'candidate');
  fs.writeFileSync(target, 'existing');
  try {
    assert.throws(() => publishNewFile(partial, target), error => error && error.code === 'EEXIST');
    assert.equal(fs.readFileSync(target, 'utf8'), 'existing');
    assert.equal(fs.readFileSync(partial, 'utf8'), 'candidate');
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('legacy import and quality help exit before touching configured data paths', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-legacy-help-'));
  try {
    const importDb = path.join(directory, 'import.db');
    const importHelp = spawnSync(process.execPath, [path.join(root, 'scripts/import.js'), '--help'], {
      cwd: root,
      env: { ...process.env, EMS_JSON_PATH: path.join(directory, 'missing.json'), EMS_DB_PATH: importDb },
      encoding: 'utf8',
    });
    assert.equal(importHelp.status, 0, importHelp.stderr);
    assert.equal(fs.existsSync(importDb), false);

    const qualityOut = path.join(directory, 'quality');
    const qualityHelp = spawnSync(process.execPath, [path.join(root, 'scripts/quality-report.js'), '--help'], {
      cwd: root,
      env: { ...process.env, EMS_DB_PATH: path.join(directory, 'missing.db'), EMS_QUALITY_OUT: qualityOut },
      encoding: 'utf8',
    });
    assert.equal(qualityHelp.status, 0, qualityHelp.stderr);
    assert.equal(fs.existsSync(qualityOut), false);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
