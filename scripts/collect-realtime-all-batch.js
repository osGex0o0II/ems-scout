#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const { installRealtimeLog } = require('./realtime-logger');
const { DEFAULT_REALTIME_CDP_PORT, ensureRealtimeBrowser } = require('./realtime-browser');

const ROOT = path.resolve(__dirname, '..');
const OUT_DIR = path.resolve(process.env.EMS_OUT_DIR || path.join(ROOT, 'out'));
const NODE = process.execPath;
const BUILDINGS = (process.argv.find(a => a.startsWith('--buildings=')) || '--buildings=1号,2号,3号,4号,5号,6号')
  .split('=')[1]
  .split(',')
  .map(s => s.trim())
  .filter(Boolean);
const BROWSER_MODE = (process.argv.find(a => a.startsWith('--browser-mode=')) || `--browser-mode=${process.env.REALTIME_BROWSER_MODE || 'persistent'}`)
  .split('=')
  .slice(1)
  .join('=') || 'persistent';
const BATCH_SIZE = Number((process.argv.find(a => a.startsWith('--batch-size=')) || '').split('=')[1] || 20);
const REOPEN_EVERY = Number((process.argv.find(a => a.startsWith('--reopen-every=')) || '').split('=')[1] || 3);
const TIMEOUT_MS = Number((process.argv.find(a => a.startsWith('--timeout=')) || '').split('=')[1] || 15000);
const MAX_DEVICES = Number((process.argv.find(a => a.startsWith('--max-devices=')) || '').split('=')[1] || 0);
const REFRESH_INVENTORY = process.argv.includes('--refresh-inventory');
const WRITE_LATEST = process.argv.includes('--write-latest');
const SKIP_INVENTORY = process.argv.includes('--skip-inventory');
const PREPARE_PAGE = !process.argv.includes('--no-prepare-page');
const SKIP_AUDIT = process.argv.includes('--skip-audit');
const LOG_FILE = process.argv.includes('--log-file') || process.argv.some(a => a.startsWith('--log-file='));
installRealtimeLog({ prefix: 'realtime_all_batch' });

const USE_MANAGED_BROWSER = BROWSER_MODE !== 'cdp';
const MANAGED_CDP_URL = `http://127.0.0.1:${DEFAULT_REALTIME_CDP_PORT}`;

function timestamp() {
  const d = new Date();
  const p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}_${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function runNode(script, args) {
  const finalArgs = LOG_FILE && !args.some(a => a === '--log-file' || a.startsWith('--log-file='))
    ? [...args, '--log-file']
    : args;
  const command = [script, ...finalArgs].join(' ');
  console.log(`[RUN] node ${command}`);
  const result = spawnSync(NODE, [script, ...finalArgs], {
    cwd: ROOT,
    stdio: 'inherit',
    env: process.env,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Command failed (${result.status}): node ${command}`);
}

function progress(event) {
  console.log(`[PROGRESS] ${JSON.stringify({
    ts: new Date().toISOString(),
    ...event,
  })}`);
}

function childBrowserArgs(managed) {
  if (managed) {
    return [
      '--browser-mode=cdp',
      `--cdp-url=${MANAGED_CDP_URL}`,
      '--strict-cdp',
    ];
  }
  return [`--browser-mode=${BROWSER_MODE}`];
}

function runAudit(summaryPath) {
  const args = ['scripts/audit-realtime-data.js', `--summary=${summaryPath}`, ...(LOG_FILE ? ['--log-file'] : [])];
  const command = args.join(' ');
  console.log(`[RUN] node ${command}`);
  const result = spawnSync(NODE, args, {
    cwd: ROOT,
    stdio: 'inherit',
    env: process.env,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`Realtime quality audit failed (${result.status}): node ${command}`);
  }
}

function latestFile(prefix, building) {
  return path.join(OUT_DIR, `${prefix}_${building}_latest.json`);
}

function newestFile(patternPrefix, building) {
  const escaped = String(building).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const re = patternPrefix === 'realtime'
    ? new RegExp(`^realtime_${escaped}_(?:batch_)?\\d{8}_\\d{6}\\.json$`)
    : new RegExp(`^${patternPrefix}_${escaped}_.*\\.json$`);
  return fs.readdirSync(OUT_DIR)
    .filter(name => re.test(name) && !name.endsWith('_latest.json'))
    .map(name => {
      const full = path.join(OUT_DIR, name);
      return { full, mtime: fs.statSync(full).mtimeMs };
    })
    .sort((a, b) => b.mtime - a.mtime)[0]?.full || '';
}

function sourceFile(building) {
  const realtimeLatest = latestFile('realtime', building);
  if (fs.existsSync(realtimeLatest)) return realtimeLatest;
  const devicesLatest = latestFile('devices', building);
  if (fs.existsSync(devicesLatest)) return devicesLatest;
  return newestFile('realtime', building);
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function sourceExists(building) {
  return !!sourceFile(building);
}

function validateResult(file) {
  const data = readJson(file);
  const rows = data.rows || [];
  const issues = [];
  const invalidLocks = [];
  const seen = new Set();
  let duplicates = 0;
  const fieldCounts = {};
  const tagCounts = {};
  const validTagCounts = {};
  const switchCounts = {};
  const cardCommCounts = {};
  const lockCounts = {};

  for (const row of rows) {
    const key = String(row.devId || '');
    if (key && seen.has(key)) duplicates++;
    if (key) seen.add(key);
    fieldCounts[row.fieldCount] = (fieldCounts[row.fieldCount] || 0) + 1;
    tagCounts[row.realtimeTagCount] = (tagCounts[row.realtimeTagCount] || 0) + 1;
    validTagCounts[row.realtimeValidTagCount] = (validTagCounts[row.realtimeValidTagCount] || 0) + 1;
    const fields = row.fields || {};
    switchCounts[fields['当前开关机状态'] || ''] = (switchCounts[fields['当前开关机状态'] || ''] || 0) + 1;
    cardCommCounts[row.cardComm || row.card_comm || ''] = (cardCommCounts[row.cardComm || row.card_comm || ''] || 0) + 1;
    const lock = fields['集控锁定'] || '';
    lockCounts[lock] = (lockCounts[lock] || 0) + 1;
    if (lock && lock !== '开启' && lock !== '关闭') {
      invalidLocks.push({
        building: row.building,
        name: row.name,
        devId: row.devId,
        subAreaText: row.subAreaText,
        pageName: row.pageName,
        value: lock,
      });
    }
    if (row.error) issues.push({ devId: row.devId, name: row.name, error: row.error });
    if (row.defaultLike) issues.push({ devId: row.devId, name: row.name, error: 'default-like' });
    if (row.fieldCount !== 26) issues.push({ devId: row.devId, name: row.name, error: `fieldCount=${row.fieldCount}` });
    if (row.realtimeTagCount !== 46) issues.push({ devId: row.devId, name: row.name, error: `tagCount=${row.realtimeTagCount}` });
  }

  const summary = data.summary || {};
  if ((summary.failed || 0) !== 0) issues.push({ error: `summary.failed=${summary.failed}` });
  if ((summary.defaultLike || 0) !== 0) issues.push({ error: `summary.defaultLike=${summary.defaultLike}` });
  if (duplicates > 0) issues.push({ error: `duplicate devId count=${duplicates}` });

  return {
    file,
    summary,
    uniqueDevices: seen.size,
    fieldCounts,
    tagCounts,
    validTagCounts,
    switchCounts,
    cardCommCounts,
    lockCounts,
    invalidLocks,
    issues,
  };
}

function inventoryRows(building) {
  const file = latestFile('devices', building);
  if (!fs.existsSync(file)) return null;
  const rows = readJson(file).rows || [];
  const seen = new Set(rows.map(r => String(r.devId || '')).filter(Boolean));
  return { rows: rows.length, unique: seen.size, file };
}

async function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const startedAt = Date.now();
  const results = [];
  let browserSession = null;

  try {
    if (USE_MANAGED_BROWSER) {
      progress({
        phase: 'browser',
        status: 'running',
        buildingIndex: 0,
        buildingTotal: BUILDINGS.length,
        percent: 1,
        message: '正在打开实时采集浏览器',
      });
      browserSession = await ensureRealtimeBrowser({
        mode: 'persistent',
        cdpPort: DEFAULT_REALTIME_CDP_PORT,
        log: msg => console.log(msg),
      });
      progress({
        phase: 'browser',
        status: 'done',
        buildingIndex: 0,
        buildingTotal: BUILDINGS.length,
        percent: 3,
        message: `浏览器已就绪，后续楼栋复用同一窗口 ${browserSession.cdpUrl || MANAGED_CDP_URL}`,
      });
    }

    for (const building of BUILDINGS) {
    const buildingIndex = results.length + 1;
    progress({
      phase: 'building',
      status: 'running',
      building,
      buildingIndex,
      buildingTotal: BUILDINGS.length,
      percent: Math.round(((buildingIndex - 1) / BUILDINGS.length) * 100),
      message: `开始 ${building}`,
    });
    if (!SKIP_INVENTORY) {
      progress({
        phase: 'inventory',
        status: 'running',
        building,
        buildingIndex,
        buildingTotal: BUILDINGS.length,
        percent: Math.round(((buildingIndex - 1) / BUILDINGS.length) * 100),
        message: `${building} 准备设备清单`,
      });
      runNode('scripts/collect-building-realtime-details.js', [
        `--building=${building}`,
        ...childBrowserArgs(USE_MANAGED_BROWSER),
        '--inventory-only',
        ...(MAX_DEVICES > 0 ? [`--max-devices=${MAX_DEVICES}`] : []),
      ]);
    }
    if (!sourceExists(building)) {
      throw new Error(`No realtime/device source for ${building}`);
    }

    if (PREPARE_PAGE) {
      progress({
        phase: 'prepare',
        status: 'running',
        building,
        buildingIndex,
        buildingTotal: BUILDINGS.length,
        percent: Math.round(((buildingIndex - 1) / BUILDINGS.length) * 100),
        message: `${building} 打开并校验实时详情弹窗`,
      });
      runNode('scripts/collect-building-realtime-details.js', [
        `--building=${building}`,
        ...childBrowserArgs(USE_MANAGED_BROWSER),
        '--inventory-only',
        '--max-devices=1',
      ]);
    }

    const inventory = inventoryRows(building);
    const totalDevices = MAX_DEVICES > 0 ? MAX_DEVICES : Number(inventory?.unique || inventory?.rows || 0);
    progress({
      phase: 'realtime_batch',
      status: 'running',
      building,
      buildingIndex,
      buildingTotal: BUILDINGS.length,
      deviceDone: 0,
      deviceTotal: totalDevices,
      percent: Math.round(((buildingIndex - 1) / BUILDINGS.length) * 100),
      message: `${building} 实时详情批量采集开始`,
    });
    runNode('scripts/collect-building-realtime-batch.js', [
      `--building=${building}`,
      ...childBrowserArgs(USE_MANAGED_BROWSER),
      `--batch-size=${BATCH_SIZE}`,
      `--reopen-every=${REOPEN_EVERY}`,
      `--timeout=${TIMEOUT_MS}`,
      ...(MAX_DEVICES > 0 ? [`--max-devices=${MAX_DEVICES}`] : []),
      ...(WRITE_LATEST ? ['--write-latest'] : []),
    ]);

    const resultFile = newestFile('realtime', building);
    if (!resultFile) throw new Error(`Cannot find batch output for ${building}`);
    const validation = validateResult(resultFile);
    validation.inventory = inventoryRows(building);
    results.push(validation);
    if (validation.issues.length > 0) {
      console.log(`[QUALITY FAIL] ${building}: ${validation.issues.length} issue(s)`);
      console.log(JSON.stringify(validation.issues.slice(0, 20), null, 2));
      throw new Error(`Quality gate failed for ${building}`);
    }
    progress({
      phase: 'realtime_batch',
      status: 'done',
      building,
      buildingIndex,
      buildingTotal: BUILDINGS.length,
      deviceDone: validation.summary.devices || 0,
      deviceTotal: totalDevices || validation.summary.devices || 0,
      percent: Math.round((buildingIndex / BUILDINGS.length) * 100),
      message: `${building} 完成 ${validation.summary.devices || 0} 台`,
    });
    console.log(`[QUALITY OK] ${building}: devices=${validation.summary.devices}, elapsedMs=${validation.summary.elapsedMs}`);
    }

    const total = results.reduce((acc, item) => {
    acc.devices += item.summary.devices || 0;
    acc.success += item.summary.success || 0;
    acc.failed += item.summary.failed || 0;
    acc.defaultLike += item.summary.defaultLike || 0;
    acc.invalidLock += item.invalidLocks.length;
    acc.elapsedMs += item.summary.elapsedMs || 0;
    return acc;
  }, { devices: 0, success: 0, failed: 0, defaultLike: 0, invalidLock: 0, elapsedMs: 0 });

    const outPath = path.join(OUT_DIR, `realtime_all_buildings_batch_summary_${timestamp()}.json`);
    const summary = {
    createdAt: new Date().toISOString(),
    wallElapsedMs: Date.now() - startedAt,
    options: {
      buildings: BUILDINGS,
      batchSize: BATCH_SIZE,
      reopenEvery: REOPEN_EVERY,
      timeoutMs: TIMEOUT_MS,
      maxDevices: MAX_DEVICES,
      refreshInventory: REFRESH_INVENTORY,
      writeLatest: WRITE_LATEST,
      preparePage: PREPARE_PAGE,
      skipAudit: SKIP_AUDIT,
      browserMode: BROWSER_MODE,
      managedBrowser: USE_MANAGED_BROWSER,
      managedCdpUrl: USE_MANAGED_BROWSER ? MANAGED_CDP_URL : '',
    },
    total,
    results,
  };
    fs.writeFileSync(outPath, JSON.stringify(summary, null, 2), 'utf8');
    console.log(`[ALL DONE] ${outPath}`);
    console.log(JSON.stringify({ total, wallElapsedMs: summary.wallElapsedMs }, null, 2));
    progress({
      phase: 'done',
      status: 'done',
      buildingIndex: BUILDINGS.length,
      buildingTotal: BUILDINGS.length,
      deviceDone: total.devices,
      deviceTotal: total.devices,
      percent: 100,
      message: `实时详情采集完成：${total.devices} 台`,
    });
    if (!SKIP_AUDIT) runAudit(outPath);
  } finally {
    if (browserSession && browserSession.context) {
      await browserSession.context.close().catch(() => {});
    }
  }
}

main().catch(err => {
  console.error(err.stack || err);
  process.exit(1);
});
