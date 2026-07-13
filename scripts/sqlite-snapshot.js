'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

function fingerprint(file) {
  const stat = fs.statSync(file);
  return {
    bytes: stat.size,
    modified: stat.mtimeMs,
    sha256: crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex'),
  };
}

function sourceSet(databasePath) {
  return ['', '-wal', '-shm']
    .map(suffix => ({ suffix, file: databasePath + suffix }))
    .filter(item => fs.existsSync(item.file));
}

function stableState(databasePath) {
  return sourceSet(databasePath).map(item => ({ suffix: item.suffix, ...fingerprint(item.file) }));
}

function copyStableSqliteSnapshot(databasePath, tempRoot = os.tmpdir(), maxAttempts = 3) {
  const source = path.resolve(databasePath);
  if (!fs.existsSync(source)) throw new Error(`SQLite source not found: ${source}`);
  fs.mkdirSync(tempRoot, { recursive: true });
  let lastError;
  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    const directory = fs.mkdtempSync(path.join(path.resolve(tempRoot), 'ems-sqlite-snapshot-'));
    const snapshotPath = path.join(directory, 'database.db');
    try {
      const before = stableState(source);
      for (const item of sourceSet(source)) fs.copyFileSync(item.file, snapshotPath + item.suffix);
      const after = stableState(source);
      if (JSON.stringify(before) !== JSON.stringify(after)) {
        throw new Error('SQLite source changed while the private snapshot was copied');
      }
      for (const expected of before) {
        const copied = fingerprint(snapshotPath + expected.suffix);
        if (copied.bytes !== expected.bytes || copied.sha256 !== expected.sha256) {
          throw new Error(`SQLite snapshot copy mismatch for ${expected.suffix || 'main database'}`);
        }
      }
      return { directory, path: snapshotPath };
    } catch (error) {
      lastError = error;
      fs.rmSync(directory, { recursive: true, force: true });
    }
  }
  throw new Error(`Unable to capture a stable SQLite snapshot after ${maxAttempts} attempts: ${lastError && lastError.message}`);
}

module.exports = { copyStableSqliteSnapshot };
