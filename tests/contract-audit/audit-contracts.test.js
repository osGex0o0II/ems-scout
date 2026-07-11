'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');

const ROOT = path.join(__dirname, '..', '..');
const SCRIPT = path.join(ROOT, 'scripts', 'audit-contracts.js');
const FIXTURES = path.join(__dirname, 'fixtures');

function fixture(name) {
  return path.join(FIXTURES, name);
}

function hashFile(filePath) {
  return crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex');
}

function optionalFileIdentity(filePath) {
  if (!fs.existsSync(filePath)) return null;
  const stat = fs.statSync(filePath, { bigint: true });
  return {
    hash: hashFile(filePath),
    mtimeNs: stat.mtimeNs,
    size: stat.size,
  };
}

function sqliteSourceIdentity(dbPath) {
  return {
    db: optionalFileIdentity(dbPath),
    shm: optionalFileIdentity(`${dbPath}-shm`),
    wal: optionalFileIdentity(`${dbPath}-wal`),
  };
}

function runAudit(inputs, options = {}) {
  const result = spawnSync(process.execPath, [SCRIPT, ...inputs], {
    cwd: ROOT,
    encoding: 'utf8',
    env: { ...process.env, ...options.env },
    maxBuffer: 64 * 1024 * 1024,
  });
  assert.equal(result.error, undefined, result.error && result.error.message);
  let report;
  try {
    report = JSON.parse(result.stdout);
  } catch (error) {
    assert.fail(`audit output is not JSON: ${error.message}\nstdout=${result.stdout}\nstderr=${result.stderr}`);
  }
  return { ...result, report };
}

function sqliteCapability() {
  const cli = spawnSync(process.env.SQLITE3_BIN || 'sqlite3', ['-version'], { encoding: 'utf8' });
  if (!cli.error && cli.status === 0) return 'sqlite3-cli';
  try {
    require('node:sqlite');
    return 'node:sqlite';
  } catch {
    return null;
  }
}

function createSqliteFixture(dbPath, sql = fs.readFileSync(fixture('schema-v0.sql'), 'utf8')) {
  const sqliteBin = process.env.SQLITE3_BIN || 'sqlite3';
  const cli = spawnSync(sqliteBin, [dbPath], { encoding: 'utf8', input: sql });
  if (!cli.error && cli.status === 0) return;
  if (cli.error && cli.error.code !== 'ENOENT') throw cli.error;
  if (!cli.error && cli.status !== 0) throw new Error(cli.stderr || cli.stdout);
  const { DatabaseSync } = require('node:sqlite');
  const db = new DatabaseSync(dbPath);
  try {
    db.exec(sql);
  } finally {
    db.close();
  }
}

test('schemas declare Draft 2020-12 and stable contract identifiers', () => {
  const snapshot = JSON.parse(fs.readFileSync(path.join(ROOT, 'contracts', 'collection-snapshot-v1.schema.json'), 'utf8'));
  const event = JSON.parse(fs.readFileSync(path.join(ROOT, 'contracts', 'workflow-event-v1.schema.json'), 'utf8'));
  const control = JSON.parse(fs.readFileSync(path.join(ROOT, 'contracts', 'workflow-control-v1.schema.json'), 'utf8'));
  assert.equal(snapshot.$schema, 'https://json-schema.org/draft/2020-12/schema');
  assert.equal(snapshot.properties.contractVersion.const, 'ems.collection-snapshot/v1');
  assert.ok(snapshot.required.includes('workflowId'));
  assert.equal(snapshot.properties.workflowRunId, undefined);
  assert.equal(snapshot.$defs.deviceSourceKey.pattern, '^sk1_[a-f0-9]{64}$');
  assert.equal(snapshot.$defs.deviceUid.pattern, '^duid1_[a-f0-9]{64}$');
  assert.equal(event.$schema, 'https://json-schema.org/draft/2020-12/schema');
  assert.equal(event.$defs.contractVersion.const, 'ems.workflow-event/v1');
  assert.deepEqual(event.oneOf, [
    { $ref: '#/$defs/started' },
    { $ref: '#/$defs/progressEvent' },
    { $ref: '#/$defs/actionEvent' },
    { $ref: '#/$defs/terminal' },
  ]);
  assert.doesNotMatch(JSON.stringify(event), /workflowRunId|sequence|emittedAt|eventType|payload/);
  assert.deepEqual(event.$defs.terminal.properties.outcome.enum, [
    'succeeded',
    'succeeded_with_findings',
    'rejected',
    'auth_required',
    'cancelled',
    'internal_error',
  ]);
  assert.equal(control.$schema, 'https://json-schema.org/draft/2020-12/schema');
  assert.equal(control.properties.contractVersion.const, 'ems.workflow-control/v1');
  assert.equal(control.properties.type.const, 'cancel');
  assert.equal(control.additionalProperties, false);
});

test('legacy collection audit is deterministic and reports internal fields and sentinels', () => {
  const input = fixture('legacy-snapshot.json');
  const before = fs.statSync(input, { bigint: true });
  const first = runAudit([input]);
  const second = runAudit([input]);
  const after = fs.statSync(input, { bigint: true });
  assert.equal(first.status, 0, first.stderr);
  assert.equal(second.status, 0, second.stderr);
  assert.equal(first.stdout, second.stdout);
  const audited = first.report.files[0];
  assert.equal(audited.shape, 'legacy-collection-snapshot/v0');
  assert.equal(audited.unknownShape, false);
  assert.equal(audited.topLevelContractVersion, null);
  assert.equal(audited.sha256, hashFile(input));
  assert.equal(audited.internalFields._nameFloor.count, 1);
  assert.deepEqual(audited.sentinels.byKind, {
    emptyString: 1,
    hyphen: 1,
    null: 0,
    numericZero: 2,
    stringZero: 1,
    total: 5,
  });
  assert.deepEqual(audited.readOnlyCheck, {
    contentUnchanged: true,
    mtimeUnchanged: true,
    sizeUnchanged: true,
  });
  assert.equal(before.size, after.size);
  assert.equal(before.mtimeNs, after.mtimeNs);
});

test('CollectionSnapshot, WorkflowEvent, and WorkflowControl v1 fixtures validate', () => {
  const snapshot = runAudit([fixture('collection-snapshot-v1.json')]);
  assert.equal(snapshot.status, 0, snapshot.stderr);
  assert.equal(snapshot.report.files[0].shape, 'collection-snapshot/v1');
  assert.equal(snapshot.report.files[0].contractValidation.valid, true);
  assert.deepEqual(snapshot.report.files[0].contractValidation.errors, []);

  const events = runAudit([fixture('workflow-events-v1.ndjson')]);
  assert.equal(events.status, 0, events.stderr);
  assert.equal(events.report.files[0].documentFormat, 'ndjson');
  assert.equal(events.report.files[0].shape, 'workflow-event-stream/v1');
  assert.equal(events.report.files[0].contractValidation.valid, true);

  const control = runAudit([fixture('workflow-control-v1.json')]);
  assert.equal(control.status, 0, control.stderr);
  assert.equal(control.report.files[0].shape, 'workflow-control/v1');
  assert.equal(control.report.files[0].contractValidation.valid, true);
});

test('invalid recognized v1 envelope and unknown versions return exit code 2', t => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-contract-invalid-'));
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const invalidPath = path.join(temp, 'invalid-v1.json');
  const invalid = JSON.parse(fs.readFileSync(fixture('collection-snapshot-v1.json'), 'utf8'));
  delete invalid.artifact;
  fs.writeFileSync(invalidPath, JSON.stringify(invalid), 'utf8');

  const recognized = runAudit([invalidPath]);
  assert.equal(recognized.status, 2);
  assert.equal(recognized.report.files[0].unknownShape, false);
  assert.equal(recognized.report.files[0].contractValidation.valid, false);
  assert.match(recognized.report.files[0].contractValidation.errors.join('\n'), /artifact/);

  const unknown = runAudit([fixture('unknown.json')]);
  assert.equal(unknown.status, 2);
  assert.equal(unknown.report.files[0].unknownShape, true);
  assert.equal(unknown.report.files[0].shape, 'unknown-contract-version');
  assert.equal(unknown.report.files[0].topLevelContractVersion, 'ems.collection-snapshot/v99');

  const oldProtocolPath = path.join(temp, 'old-workflow-protocol.json');
  fs.writeFileSync(oldProtocolPath, JSON.stringify({
    contractVersion: 'ems.workflow-event/v1',
    workflowRunId: 'old-run',
    sequence: 1,
    emittedAt: '2026-07-11T00:00:00.000Z',
    eventType: 'workflow.started',
    payload: {},
  }), 'utf8');
  const oldProtocol = runAudit([oldProtocolPath]);
  assert.equal(oldProtocol.status, 2);
  assert.equal(oldProtocol.report.files[0].shape, 'workflow-event/v1');
  assert.equal(oldProtocol.report.files[0].contractValidation.valid, false);

  const brokenStreamPath = path.join(temp, 'broken-stream.ndjson');
  const brokenStream = fs.readFileSync(fixture('workflow-events-v1.ndjson'), 'utf8')
    .replace('"seq":2', '"seq":4');
  fs.writeFileSync(brokenStreamPath, brokenStream, 'utf8');
  const broken = runAudit([brokenStreamPath]);
  assert.equal(broken.status, 2);
  assert.equal(broken.report.files[0].unknownShape, false);
  assert.match(broken.report.files[0].contractValidation.errors.join('\n'), /must have seq 2/);
});

test('SQLite audit is read-only and includes user_version, tables, columns, and fingerprint', t => {
  if (!sqliteCapability()) return t.skip('sqlite3 CLI and node:sqlite are both unavailable');
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-contract-sqlite-'));
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const dbPath = path.join(temp, 'fixture.db');
  createSqliteFixture(dbPath);
  const walPath = `${dbPath}-wal`;
  if (!fs.existsSync(walPath)) fs.writeFileSync(walPath, Buffer.alloc(0));
  const beforeBytes = fs.readFileSync(dbPath);
  const beforeWalBytes = fs.readFileSync(walPath);
  const before = fs.statSync(dbPath, { bigint: true });
  const beforeWal = fs.statSync(walPath, { bigint: true });
  const beforeEntries = fs.readdirSync(temp).sort();
  const first = runAudit([dbPath]);
  const second = runAudit([dbPath]);
  const after = fs.statSync(dbPath, { bigint: true });
  assert.equal(first.status, 0, first.stderr);
  assert.equal(first.stdout, second.stdout);
  assert.equal(first.report.files[0].shape, 'ems-sqlite/core');
  assert.equal(first.report.files[0].sqlite.userVersion, 7);
  assert.deepEqual(first.report.files[0].sqlite.missingCoreTables, []);
  assert.deepEqual(first.report.files[0].sqlite.tables.map(table => table.name), [
    'buildings',
    'cards',
    'pages',
    'sub_areas',
  ]);
  const cards = first.report.files[0].sqlite.tables.find(table => table.name === 'cards');
  assert.deepEqual(cards.columns.map(column => column.name), ['id', 'page_id', 'name']);
  assert.match(first.report.files[0].sqlite.schemaFingerprint, /^[a-f0-9]{64}$/);
  assert.equal(first.report.files[0].sha256, hashFile(dbPath));
  assert.equal(first.report.files[0].readOnlyCheck.companionsUnchanged, true);
  assert.deepEqual(fs.readFileSync(dbPath), beforeBytes);
  assert.deepEqual(fs.readFileSync(walPath), beforeWalBytes);
  assert.deepEqual(fs.readdirSync(temp).sort(), beforeEntries);
  assert.equal(before.size, after.size);
  assert.equal(before.mtimeNs, after.mtimeNs);
  const afterWal = fs.statSync(walPath, { bigint: true });
  assert.equal(beforeWal.size, afterWal.size);
  assert.equal(beforeWal.mtimeNs, afterWal.mtimeNs);
});

test('SQLite audit falls back to node:sqlite without loading better-sqlite3', t => {
  try {
    require('node:sqlite');
  } catch {
    return t.skip('node:sqlite is unavailable on this Node runtime');
  }
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-contract-fallback-'));
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const dbPath = path.join(temp, 'fixture.db');
  createSqliteFixture(dbPath);
  fs.rmSync(`${dbPath}-wal`, { force: true });
  fs.rmSync(`${dbPath}-shm`, { force: true });
  const result = runAudit([dbPath], {
    env: { SQLITE3_BIN: path.join(temp, 'missing-sqlite3') },
  });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.report.files[0].sqlite.reader, 'node:sqlite');
  assert.doesNotMatch(fs.readFileSync(SCRIPT, 'utf8'), /better-sqlite3/);
});

test('WAL-mode SQLite without companion files is audited from a private writable snapshot', t => {
  if (!sqliteCapability()) return t.skip('sqlite3 CLI and node:sqlite are both unavailable');
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-contract-wal-no-companion-'));
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const dbPath = path.join(temp, 'wal-no-companion.db');
  createSqliteFixture(dbPath);
  fs.rmSync(`${dbPath}-wal`, { force: true });
  fs.rmSync(`${dbPath}-shm`, { force: true });
  const header = fs.readFileSync(dbPath).subarray(0, 20);
  assert.equal(header[18], 2, 'database write version must remain WAL');
  assert.equal(header[19], 2, 'database read version must remain WAL');
  const before = sqliteSourceIdentity(dbPath);
  const beforeEntries = fs.readdirSync(temp).sort();
  const result = runAudit([dbPath]);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.report.files[0].shape, 'ems-sqlite/core');
  assert.equal(result.report.files[0].sqlite.tables.length, 4);
  assert.equal(result.report.files[0].readOnlyCheck.companionsUnchanged, true);
  assert.deepEqual(sqliteSourceIdentity(dbPath), before);
  assert.deepEqual(fs.readdirSync(temp).sort(), beforeEntries);
});

test('archived 1号 and 2号 four-table WAL baselines audit without source changes', () => {
  const inputs = [
    path.join(ROOT, 'data', '1号楼', 'ac.db'),
    path.join(ROOT, 'data', '2号楼', 'ac.db'),
  ];
  const before = inputs.map(sqliteSourceIdentity);
  const result = runAudit(inputs);
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(result.report.files.map(file => file.shape), [
    'ems-sqlite/core',
    'ems-sqlite/core',
  ]);
  assert.deepEqual(result.report.files.map(file => file.sqlite.tables.length), [4, 4]);
  assert.deepEqual(result.report.files.map(file => file.sqlite.userVersion), [0, 0]);
  assert.equal(
    result.report.files[0].sqlite.schemaFingerprint,
    result.report.files[1].sqlite.schemaFingerprint
  );
  assert.ok(result.report.files.every(file => file.readOnlyCheck.companionsUnchanged));
  assert.deepEqual(inputs.map(sqliteSourceIdentity), before);
});

test('SQLite with an unknown table shape returns exit code 2', t => {
  if (!sqliteCapability()) return t.skip('sqlite3 CLI and node:sqlite are both unavailable');
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'ems-contract-unknown-db-'));
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const dbPath = path.join(temp, 'unknown.db');
  createSqliteFixture(dbPath, 'PRAGMA user_version=0; CREATE TABLE unrelated (id INTEGER PRIMARY KEY);');
  const result = runAudit([dbPath]);
  assert.equal(result.status, 2, result.stderr);
  assert.equal(result.report.files[0].shape, 'unknown-sqlite');
  assert.equal(result.report.files[0].unknownShape, true);
  assert.deepEqual(result.report.files[0].sqlite.missingCoreTables, [
    'buildings',
    'cards',
    'pages',
    'sub_areas',
  ]);
});
