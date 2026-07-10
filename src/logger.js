'use strict';
const fs = require('fs');
const path = require('path');

const LEVELS = Object.freeze({ ERROR: 0, WARN: 1, INFO: 2, DEBUG: 3 });
const LEVEL_NAMES = Object.keys(LEVELS);

const CATEGORIES = Object.freeze({ ENUM: 'ENUM', QUALITY: 'QUALITY', RULE: 'RULE', VUE: 'VUE', CRASH: 'CRASH', NET: 'NET' });

const COLORS = Object.freeze({
  ERROR: '\x1b[31m', WARN: '\x1b[33m', INFO: '\x1b[37m', DEBUG: '\x1b[90m',
  ENUM: '\x1b[36m', QUALITY: '\x1b[33m', RULE: '\x1b[35m', VUE: '\x1b[32m', CRASH: '\x1b[31m', NET: '\x1b[90m',
  RESET: '\x1b[0m'
});

let minLevel = LEVELS.INFO;
let enabledCategories = new Set(Object.values(CATEGORIES));
let logFileStream = null;
let logFilePath = null;

for (const stream of [process.stdout, process.stderr]) {
  if (stream && stream.on && !stream.__emsEpipeHandled) {
    stream.__emsEpipeHandled = true;
    stream.on('error', err => {
      if (!err || err.code !== 'EPIPE') throw err;
    });
  }
}

function ts() { return new Date().toISOString().substring(11, 19); }
function tsFull() { return new Date().toISOString(); }

function setLevel(level) {
  const l = level.toUpperCase();
  if (l in LEVELS) minLevel = LEVELS[l];
}

function setCategories(cats) {
  if (!cats || cats === 'all') {
    enabledCategories = new Set(Object.values(CATEGORIES));
    return;
  }
  enabledCategories = new Set(cats.split(',').map(c => c.trim().toUpperCase()).filter(c => c in CATEGORIES));
}

function enableFileLog(dir) {
  const date = new Date().toISOString().substring(0, 10);
  logFilePath = path.resolve(dir, `enum_${date}.log`);
  fs.mkdirSync(path.dirname(logFilePath), { recursive: true });
  logFileStream = fs.createWriteStream(logFilePath, { flags: 'a' });
}

function close() {
  if (logFileStream) { logFileStream.end(); logFileStream = null; }
}

const levelColor = l => COLORS[l] || COLORS.INFO;
const catColor = c => COLORS[c] || '';

function log(level, category, msg, extra) {
  const l = typeof level === 'number' ? Object.keys(LEVELS).find(k => LEVELS[k] === level) || level : level.toUpperCase();
  const c = category.toUpperCase();
  if (!(l in LEVELS)) return;
  if (LEVELS[l] > minLevel) return;
  if (!enabledCategories.has(c)) return;

  const t = ts();
  const line = `${t} ${l} ${c} ${msg}`;

  // Console: colored
  try {
    console.log(`${COLORS.RESET}[${t}] ${levelColor(l)}${l}\x1b[0m ${catColor(c)}${c}\x1b[0m ${msg}`);
  } catch (err) {
    if (!err || err.code !== 'EPIPE') throw err;
  }
  if (extra && typeof extra === 'object' && LEVELS[l] <= LEVELS.DEBUG) {
    for (const [k, v] of Object.entries(extra)) {
      if (v !== undefined) {
        try {
          console.log(`  ${COLORS.DEBUG}${k}=${v}${COLORS.RESET}`);
        } catch (err) {
          if (!err || err.code !== 'EPIPE') throw err;
        }
      }
    }
  }

  // File: NDJSON
  if (logFileStream) {
    const entry = Object.assign({ ts: tsFull(), level: l, cat: c, msg }, extra);
    logFileStream.write(JSON.stringify(entry) + '\n');
  }
}

module.exports = { log, setLevel, setCategories, enableFileLog, close, LEVELS, CATEGORIES };
