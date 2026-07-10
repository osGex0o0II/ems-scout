'use strict';

const fs = require('fs');
const Database = require('better-sqlite3');
const { DB_PATH, openDb } = require('../monitor');

function databaseExists() {
  return fs.existsSync(DB_PATH);
}

function openReadonlyDb() {
  if (!databaseExists()) throw new Error('Database not found: ' + DB_PATH);
  return new Database(DB_PATH, { readonly: true });
}

module.exports = {
  DB_PATH,
  databaseExists,
  openDb,
  openReadonlyDb,
};
