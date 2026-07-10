'use strict';
const fs = require('fs');
const path = require('path');

const SETTINGS_PATH = path.resolve(__dirname, '..', '..', 'out', '.settings.json');

const SETTINGS_SCHEMA = {
  log_level: {
    label: '日志级别',
    type: 'select',
    default: 'INFO',
    options: ['ERROR', 'WARN', 'INFO', 'DEBUG'],
    cliFlag: '--log-level=',
  },
  log_categories: {
    label: '日志类别过滤',
    type: 'select',
    default: 'all',
    options: ['all', 'ENUM,QUALITY', 'RULE,VUE', 'QUALITY,RULE', 'RULE,VUE,CRASH'],
    cliFlag: '--log-category=',
  },
  log_file: {
    label: '日志文件输出',
    type: 'toggle',
    default: false,
    cliFlag: '--log-file',
  },
};

const DEFAULT_VALUES = {};
for (const [k, v] of Object.entries(SETTINGS_SCHEMA)) {
  DEFAULT_VALUES[k] = v.default;
}

function load() {
  try {
    const raw = JSON.parse(fs.readFileSync(SETTINGS_PATH, 'utf8'));
    const out = {};
    for (const [k, v] of Object.entries(SETTINGS_SCHEMA)) {
      out[k] = k in raw ? raw[k] : v.default;
    }
    return out;
  } catch {
    return { ...DEFAULT_VALUES };
  }
}

function save(settings) {
  fs.mkdirSync(path.dirname(SETTINGS_PATH), { recursive: true });
  fs.writeFileSync(SETTINGS_PATH, JSON.stringify(settings, null, 2));
}

function toCliArgs(settings) {
  const args = [];
  for (const [k, v] of Object.entries(SETTINGS_SCHEMA)) {
    const val = settings[k];
    if (val === undefined || val === v.default) continue;
    if (v.type === 'toggle') {
      if (val === true) args.push(v.cliFlag);
    } else {
      args.push(v.cliFlag + val);
    }
  }
  return args;
}

module.exports = { SETTINGS_SCHEMA, SETTINGS_PATH, load, save, toCliArgs };
