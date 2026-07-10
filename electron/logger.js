'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const LOG_DIR = path.join(ROOT, 'logs');
const LOG_FILE = path.join(LOG_DIR, 'electron.log');

function ensureLogDir() {
  fs.mkdirSync(LOG_DIR, { recursive: true });
}

function serialize(value) {
  if (value instanceof Error) {
    return `${value.stack || value.message}`;
  }
  if (typeof value === 'string') return value;
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

function write(level, args) {
  ensureLogDir();
  const line = `${new Date().toISOString()} [${level}] ${args.map(serialize).join(' ')}\n`;
  fs.appendFileSync(LOG_FILE, line, 'utf8');
}

function info(...args) {
  write('INFO', args);
}

function warn(...args) {
  write('WARN', args);
}

function error(...args) {
  write('ERROR', args);
}

module.exports = {
  LOG_FILE,
  info,
  warn,
  error,
};
