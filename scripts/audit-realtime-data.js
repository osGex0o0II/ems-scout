#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const { installRealtimeLog } = require('./realtime-logger');

const ROOT = path.resolve(__dirname, '..');
const OUT_DIR = path.resolve(process.env.EMS_QUALITY_OUT || process.env.EMS_OUT_DIR || path.join(ROOT, 'out'));
installRealtimeLog({ prefix: 'realtime_audit' });

const FIELD_ORDER = [
  '温控器程序版本号',
  '当前开关机状态',
  '高风速阀门开关状态',
  '中风速阀门开关状态',
  '低风速阀门开关状态',
  '室内温度',
  '设定温度',
  '设定风速',
  '系统模式设置',
  '达温风机状态',
  '设定温度上限',
  '设定温度下限',
  '集控锁定',
  '系统类型',
  '制冷/通风温度补偿',
  '制热/地暖温度补偿',
  '待机显示温度',
  '温度回差',
  '防冷风时间',
  '防冻设置温度',
  '防冻保护是否开启',
  '室温显示精度',
  '温度单位选择',
  '掉电记忆',
  '通讯地址 (Modbus)',
  '恢复出厂设置',
];

const DEFAULT_TEMPLATE_VALUES = {
  '温控器程序版本号': 'V1.1',
  '当前开关机状态': '开机',
  '高风速阀门开关状态': '开',
  '中风速阀门开关状态': '开',
  '低风速阀门开关状态': '开',
  '室内温度': '26℃',
  '设定温度': '25℃',
  '设定风速': '中',
  '系统模式设置': '制冷',
  '达温风机状态': '关闭',
  '设定温度上限': '30℃',
  '设定温度下限': '17℃',
  '集控锁定': '关闭',
  '系统类型': '两管制冷',
  '制冷/通风温度补偿': '1',
  '制热/地暖温度补偿': '1',
  '待机显示温度': '实际温度',
  '温度回差': '0℃',
  '防冷风时间': '0h',
  '防冻设置温度': '0℃',
  '防冻保护是否开启': '关闭',
  '室温显示精度': '0.5℃',
  '温度单位选择': '摄氏度',
  '掉电记忆': '不记忆',
  '通讯地址 (Modbus)': '1',
  '恢复出厂设置': '常规',
};

const ENUM_FIELDS = {
  '当前开关机状态': ['开机', '关机'],
  '高风速阀门开关状态': ['开', '关'],
  '中风速阀门开关状态': ['开', '关'],
  '低风速阀门开关状态': ['开', '关'],
  '设定风速': ['自动', '高', '中', '低'],
  '系统模式设置': ['制冷', '通风', '制热', '送暖', '地暖', '制热+地暖'],
  '达温风机状态': ['开启', '关闭'],
  '集控锁定': ['开启', '关闭'],
  '系统类型': ['两管冷暖', '两管制冷', '两管制热', '四管制', '两管冷暖+地暖'],
  '待机显示温度': ['实际温度', '设定温度'],
  '防冷风时间': ['0秒', '30秒', '60秒', '90秒', '120秒', '0h'],
  '防冻保护是否开启': ['开启', '关闭'],
  '室温显示精度': ['0.1℃', '0.5℃', '1℃'],
  '温度单位选择': ['摄氏度', '华氏度'],
  '掉电记忆': ['记忆', '不记忆'],
  '恢复出厂设置': ['常规'],
};

const RANGE_FIELDS = {
  '室内温度': { min: -10, max: 60, unit: '℃' },
  '设定温度': { min: 5, max: 40, unit: '℃' },
  '设定温度上限': { min: 5, max: 45, unit: '℃' },
  '设定温度下限': { min: 0, max: 40, unit: '℃' },
  '通讯地址 (Modbus)': { min: 0, max: 255, unit: '' },
};

function argValue(name) {
  const prefix = `--${name}=`;
  const hit = process.argv.find(a => a.startsWith(prefix));
  return hit ? hit.slice(prefix.length) : '';
}

function timestamp() {
  const d = new Date();
  const p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}_${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function resolveFile(file) {
  const direct = path.isAbsolute(file) ? file : path.resolve(ROOT, file);
  if (fs.existsSync(direct)) return direct;
  const fallback = path.join(OUT_DIR, path.basename(file));
  if (fs.existsSync(fallback)) return fallback;
  return direct;
}

function newestSummaryFile() {
  if (!fs.existsSync(OUT_DIR)) throw new Error(`Output directory not found: ${OUT_DIR}`);
  const files = fs.readdirSync(OUT_DIR)
    .filter(name => /^realtime_all_buildings_batch_summary_.*\.json$/.test(name))
    .map(name => {
      const full = path.join(OUT_DIR, name);
      return { full, mtime: fs.statSync(full).mtimeMs };
    })
    .sort((a, b) => b.mtime - a.mtime);
  if (!files.length) throw new Error('No realtime_all_buildings_batch_summary_*.json found');
  return files[0].full;
}

function normalizeValue(value) {
  return String(value ?? '').replace(/\s+/g, '').replace(/\.0(?=℃?$)/, '');
}

function normalizeEnum(value) {
  return String(value ?? '').replace(/\s+/g, '');
}

function defaultTemplateScore(fields) {
  let matched = 0;
  let checked = 0;
  for (const [name, expected] of Object.entries(DEFAULT_TEMPLATE_VALUES)) {
    if (!(name in fields)) continue;
    checked++;
    if (normalizeValue(fields[name]) === normalizeValue(expected)) matched++;
  }
  return {
    matched,
    checked,
    isDefaultLike: checked >= 8 && matched >= Math.max(8, Math.floor(checked * 0.75)),
  };
}

function parseNumber(value) {
  const m = String(value ?? '').match(/-?\d+(?:\.\d+)?/);
  if (!m) return null;
  const n = Number(m[0]);
  return Number.isFinite(n) ? n : null;
}

function inc(map, key, amount = 1) {
  const k = String(key ?? '');
  map[k] = (map[k] || 0) + amount;
}

function compactRow(row, file) {
  return {
    file: path.relative(ROOT, file),
    building: row.building || '',
    subAreaText: row.subAreaText || '',
    pageName: row.pageName || '',
    name: row.name || '',
    devId: row.devId || '',
  };
}

function fieldSnapshot(fields) {
  return {
    '当前开关机状态': fields['当前开关机状态'] || '',
    '集控锁定': fields['集控锁定'] || '',
    '系统模式设置': fields['系统模式设置'] || '',
    '设定风速': fields['设定风速'] || '',
    '室内温度': fields['室内温度'] || '',
    '设定温度': fields['设定温度'] || '',
    '设定温度上限': fields['设定温度上限'] || '',
    '设定温度下限': fields['设定温度下限'] || '',
    '通讯地址 (Modbus)': fields['通讯地址 (Modbus)'] || '',
  };
}

function makeBuildingStats() {
  return {
    rows: 0,
    collectionErrors: 0,
    deviceAnomalyRows: 0,
    deviceAnomalyEvents: 0,
    fieldCounts: {},
    realtimeTagCounts: {},
    realtimeValidTagCounts: {},
    switchCounts: {},
    cardCommCounts: {},
    lockCounts: {},
    collectionErrorCategories: {},
    deviceAnomalyCategories: {},
  };
}

function addCollectionError(output, byBuilding, category, row, file, detail) {
  const building = row?.building || detail?.building || 'UNKNOWN';
  if (!byBuilding[building]) byBuilding[building] = makeBuildingStats();
  byBuilding[building].collectionErrors++;
  inc(byBuilding[building].collectionErrorCategories, category);
  inc(output.collectionErrors.byCategory, category);
  output.collectionErrors.rows.push({
    category,
    ...(row ? compactRow(row, file) : { file: file ? path.relative(ROOT, file) : '', building }),
    detail,
  });
}

function addDeviceAnomaly(output, byBuilding, row, file, issues, categories) {
  const building = row.building || 'UNKNOWN';
  if (!byBuilding[building]) byBuilding[building] = makeBuildingStats();
  byBuilding[building].deviceAnomalyRows++;
  byBuilding[building].deviceAnomalyEvents += issues.length;
  for (const category of categories) {
    inc(byBuilding[building].deviceAnomalyCategories, category);
    inc(output.deviceAnomalies.byCategory, category);
  }
  output.deviceAnomalies.rows.push({
    ...compactRow(row, file),
    realtimeValidTagCount: row.realtimeValidTagCount,
    issues,
    fields: fieldSnapshot(row.fields || {}),
  });
}

function resolveInputs() {
  const filesArg = argValue('files');
  if (filesArg) {
    return {
      mode: 'files',
      summaryFile: '',
      files: filesArg.split(',').map(s => s.trim()).filter(Boolean).map(resolveFile),
    };
  }

  const summaryFile = resolveFile(argValue('summary') || newestSummaryFile());
  const summary = readJson(summaryFile);
  if (Array.isArray(summary.rows)) {
    return { mode: 'single', summaryFile, files: [summaryFile], summary };
  }
  const files = (summary.results || [])
    .map(item => item.file)
    .filter(Boolean)
    .map(resolveFile);
  if (!files.length) throw new Error(`No result files found in summary: ${summaryFile}`);
  return { mode: 'summary', summaryFile, files, summary };
}

function auditFile(file, output, byBuilding, seenDevIds) {
  const data = readJson(file);
  const rows = Array.isArray(data.rows) ? data.rows : [];
  const summary = data.summary || {};
  const summaryBuilding = summary.building || rows[0]?.building || 'UNKNOWN';

  if ((summary.failed || 0) !== 0) {
    addCollectionError(output, byBuilding, 'summaryFailed', null, file, {
      building: summaryBuilding,
      failed: summary.failed,
    });
  }
  if ((summary.defaultLike || 0) !== 0) {
    addCollectionError(output, byBuilding, 'summaryDefaultLike', null, file, {
      building: summaryBuilding,
      defaultLike: summary.defaultLike,
    });
  }
  if (summary.devices !== undefined && Number(summary.devices) !== rows.length) {
    addCollectionError(output, byBuilding, 'rowCountMismatch', null, file, {
      building: summaryBuilding,
      summaryDevices: summary.devices,
      rows: rows.length,
    });
  }

  for (const row of rows) {
    const building = row.building || summaryBuilding;
    row.building = building;
    if (!byBuilding[building]) byBuilding[building] = makeBuildingStats();
    const stats = byBuilding[building];
    const fields = row.fields || {};
    const fieldCount = Object.keys(fields).length;
    stats.rows++;
    output.totalRows++;
    inc(stats.fieldCounts, fieldCount);
    inc(stats.realtimeTagCounts, row.realtimeTagCount);
    inc(stats.realtimeValidTagCounts, row.realtimeValidTagCount);
    inc(stats.switchCounts, fields['当前开关机状态'] || '');
    inc(stats.cardCommCounts, row.cardComm || row.card_comm || '');
    inc(stats.lockCounts, fields['集控锁定'] || '');

    const devKey = row.devId ? String(row.devId) : '';
    if (!devKey || !row.name) {
      addCollectionError(output, byBuilding, 'missingMetadata', row, file, {
        hasName: !!row.name,
        hasDevId: !!devKey,
      });
    } else if (seenDevIds.has(devKey)) {
      addCollectionError(output, byBuilding, 'duplicateDevId', row, file, {
        first: seenDevIds.get(devKey),
      });
    } else {
      seenDevIds.set(devKey, compactRow(row, file));
    }

    if (row.error) {
      addCollectionError(output, byBuilding, 'rowError', row, file, { error: row.error });
    }

    const score = defaultTemplateScore(fields);
    if (row.defaultLike || score.isDefaultLike) {
      addCollectionError(output, byBuilding, 'defaultLike', row, file, {
        matched: score.matched,
        checked: score.checked,
        rowDefaultLike: !!row.defaultLike,
      });
    }

    if (Number(row.fieldCount) !== 26 || fieldCount !== 26) {
      addCollectionError(output, byBuilding, 'fieldCount', row, file, {
        rowFieldCount: row.fieldCount,
        actualFieldCount: fieldCount,
      });
    }

    if (Number(row.realtimeTagCount) !== 46) {
      addCollectionError(output, byBuilding, 'realtimeTagCount', row, file, {
        realtimeTagCount: row.realtimeTagCount,
      });
    }

    const missing = FIELD_ORDER.filter(name => !(name in fields));
    if (missing.length) {
      addCollectionError(output, byBuilding, 'missingRequiredField', row, file, { missing });
    }

    const issues = [];
    const categories = new Set();
    const validTags = Number(row.realtimeValidTagCount);
    if (validTags === 0) {
      issues.push('实时点位 valid=0');
      categories.add('invalidRealtimeTags');
    } else if (Number.isFinite(validTags) && validTags !== 46) {
      issues.push(`实时点位 valid=${row.realtimeValidTagCount}/46`);
      categories.add('partialRealtimeTags');
    }

    for (const [name, allowedValues] of Object.entries(ENUM_FIELDS)) {
      if (!(name in fields)) continue;
      const value = fields[name];
      const normalized = normalizeEnum(value);
      const allowed = allowedValues.map(normalizeEnum);
      if (!allowed.includes(normalized)) {
        const raw = row.rawFields && row.rawFields[name] !== undefined ? ` raw=${row.rawFields[name]}` : '';
        issues.push(`枚举异常 ${name}=${value}${raw}`);
        categories.add(name === '集控锁定' ? 'invalidLock' : 'invalidEnum');
      }
    }

    for (const [name, range] of Object.entries(RANGE_FIELDS)) {
      if (!(name in fields)) continue;
      const n = parseNumber(fields[name]);
      if (n === null || n < range.min || n > range.max) {
        issues.push(`范围异常 ${name}=${fields[name]} 期望 ${range.min}-${range.max}${range.unit}`);
        categories.add('outOfRange');
      }
    }

    if (issues.length) {
      addDeviceAnomaly(output, byBuilding, row, file, issues, categories);
    }
  }
}

function main() {
  const input = resolveInputs();
  const outputPath = resolveFile(argValue('output') || path.join(OUT_DIR, `realtime_quality_classified_${timestamp()}.json`));
  const byBuilding = {};
  const seenDevIds = new Map();
  const output = {
    createdAt: new Date().toISOString(),
    input: {
      mode: input.mode,
      summaryFile: input.summaryFile ? path.relative(ROOT, input.summaryFile) : '',
      files: input.files.map(file => path.relative(ROOT, file)),
    },
    totalRows: 0,
    uniqueDevices: 0,
    collectionErrors: {
      count: 0,
      byCategory: {},
      rows: [],
    },
    deviceAnomalies: {
      rowCount: 0,
      eventCount: 0,
      byCategory: {},
      rows: [],
    },
    byBuilding,
    conclusion: {
      collectionOk: false,
      note: '设备异常只记录，不作为采集失败；采集失败仅包含缺字段、缺点位、默认模板、重复 devId 和脚本错误。',
    },
  };

  for (const file of input.files) {
    if (!fs.existsSync(file)) throw new Error(`Result file not found: ${file}`);
    auditFile(file, output, byBuilding, seenDevIds);
  }

  output.uniqueDevices = seenDevIds.size;
  output.collectionErrors.count = output.collectionErrors.rows.length;
  output.deviceAnomalies.rowCount = output.deviceAnomalies.rows.length;
  output.deviceAnomalies.eventCount = output.deviceAnomalies.rows.reduce((sum, row) => sum + row.issues.length, 0);
  output.conclusion.collectionOk = output.collectionErrors.count === 0;

  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, JSON.stringify(output, null, 2), 'utf8');

  console.log(`[审计完成] ${outputPath}`);
  console.log(JSON.stringify({
    totalRows: output.totalRows,
    uniqueDevices: output.uniqueDevices,
    collectionOk: output.conclusion.collectionOk,
    collectionErrors: output.collectionErrors.count,
    deviceAnomalyRows: output.deviceAnomalies.rowCount,
    deviceAnomalyEvents: output.deviceAnomalies.eventCount,
    deviceAnomalyByCategory: output.deviceAnomalies.byCategory,
  }, null, 2));

  if (!output.conclusion.collectionOk) process.exit(2);
}

main();
