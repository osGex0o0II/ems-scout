#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const {
  openDb,
  computeMonitorStatuses,
  refreshMonitorSnapshots,
  loadMonitorEvents,
} = require('../src/panel/monitor');
const { resolveRunId } = require('../src/panel/history');

const ROOT = path.join(__dirname, '..');
const OUT_DIR = process.env.EMS_MONITOR_REPORT_OUT || path.join(ROOT, 'out');
const LEGACY_ENABLE_ENV = 'EMS_ENABLE_LEGACY_REPORTS';

if (process.env[LEGACY_ENABLE_ENV] !== '1') {
  console.error(
    'Legacy monitor text/json report is disabled. Use the native app diagnostics/monitor views and 数据管理 -> 导出当前筛选 Excel.\n' +
    `Emergency legacy use: set ${LEGACY_ENABLE_ENV}=1 and rerun this command.`
  );
  process.exit(2);
}

function localStamp() {
  const d = new Date();
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}_${pad(d.getHours())}${pad(d.getMinutes())}`;
}

function renderText(statuses, events) {
  const lines = [];
  lines.push('未开放楼层重点监控');
  lines.push('生成时间: ' + new Date().toLocaleString('zh-CN', { hour12: false }));
  lines.push('');

  if (statuses.length === 0) {
    lines.push('暂无监控配置。');
    return lines.join('\n') + '\n';
  }

  const important = statuses.filter(s => s.opened || s.severity === 'P1' || s.severity === 'P2');
  lines.push(`监控对象: ${statuses.length}`);
  lines.push(`需关注: ${important.length}`);
  lines.push('');

  for (const s of statuses) {
    const target = `${s.building} ${s.floor_label}${s.sub_area_text ? ' ' + s.sub_area_text : ''}`;
    lines.push(`${target}: ${s.status_label}`);
    lines.push(`  优先级: ${s.priority}  期望: ${s.expected_status}`);
    lines.push(`  卡片=${s.card_count} 开机=${s.on_count} 关机=${s.off_count} 离线=${s.offline_count} 未知=${s.unknown_count} 真实温度=${s.real_temp_count}`);
    if (s.note) lines.push(`  备注: ${s.note}`);
  }

  if (events.length) {
    lines.push('');
    lines.push('最近变化');
    for (const e of events.slice(0, 30)) {
      lines.push(`  [${e.severity}] ${e.event_at} ${e.message}`);
    }
  }

  return lines.join('\n') + '\n';
}

function main() {
  const refresh = !process.argv.includes('--no-refresh');
  const runArg = (process.argv.find(a => a.startsWith('--run-id=')) || '').split('=').slice(1).join('=');
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const db = openDb();
  try {
    const runId = resolveRunId(db, runArg);
    const runOptions = { run_id: runId || '' };
    const statuses = refresh ? refreshMonitorSnapshots(db, runOptions) : computeMonitorStatuses(db, { includeDisabled: true, ...runOptions });
    const events = loadMonitorEvents(db, 100, runOptions);
    const ts = localStamp();
    const runSuffix = runId ? `_run${runId}` : '';
    const jsonPath = path.join(OUT_DIR, `未开放楼层重点监控_${ts}${runSuffix}.json`);
    const txtPath = path.join(OUT_DIR, `未开放楼层重点监控_${ts}${runSuffix}.txt`);
    fs.writeFileSync(jsonPath, JSON.stringify({ generated_at: new Date().toISOString(), run_id: runId, statuses, events }, null, 2), 'utf8');
    fs.writeFileSync(txtPath, renderText(statuses, events), 'utf8');
    console.log('Saved:', jsonPath);
    console.log('Saved:', txtPath);
  } finally {
    db.close();
  }
}

if (require.main === module) main();

module.exports = { renderText };
