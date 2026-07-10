const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');
const { BLDG_ORDER, BLDG_META, isPublic } = require('../rules');

const ROOT = path.join(__dirname, '..', '..');
const DB_PATH = path.join(ROOT, 'out', 'ac.db');
const JSON_PATH = path.join(ROOT, 'out', 'enum_full_v5.json');
const LAST_COLLECT_PATH = path.join(ROOT, 'out', '.last_collect.json');
const QUALITY_REPORT_PATH = path.join(ROOT, 'out', 'quality_report.json');

function parseMin(t) {
  if (t.includes('秒')) return parseInt(t) / 60;
  if (t.includes('分钟')) return parseInt(t);
  return 1;
}

const BLDG_ESTIMATE = Object.fromEntries(BLDG_ORDER.map(b => {
  const meta = BLDG_META[b];
  return [b, { name: meta.name, cards: meta.baselineCards, time: meta.estimateTime }];
}));

function detectEdgeMode(argv = process.argv) {
  if (argv.includes('--edge')) return '--edge';
  return '--auto-launch';
}

function getSettingCliArgs(state) {
  if (!state.settings) return [];
  const { toCliArgs } = require('./settings');
  return toCliArgs(state.settings);
}

function saveLastCollect(info) { fs.writeFileSync(LAST_COLLECT_PATH, JSON.stringify(info)); }
function loadLastCollect() { try { return JSON.parse(fs.readFileSync(LAST_COLLECT_PATH, 'utf8')); } catch { return null; } }
function loadQualityReport() { try { return JSON.parse(fs.readFileSync(QUALITY_REPORT_PATH, 'utf8')); } catch { return null; } }

function runScript(script, args, label) {
  return new Promise((resolve, reject) => {
    console.log(`\n  >> ${label || script}${args.length > 0 ? ' ' + args.join(' ') : ''}...\n`);
    const proc = spawn('node', [script, ...args], {
      cwd: ROOT,
      stdio: ['inherit', 'pipe', 'inherit'],
      shell: true,
    });
    let action = null;
    let duringEnum = false;
    let needNewline = false;
    let buf = '';
    proc.stdout.on('data', (chunk) => {
      buf += chunk.toString();
      while (true) {
        const nl = buf.indexOf('\n');
        if (nl < 0) break;
        const line = buf.slice(0, nl);
        buf = buf.slice(nl + 1);
        if (line.startsWith('\r')) {
          process.stdout.write(line);
          needNewline = true;
          continue;
        }
        if (line.startsWith('[ACTION]')) {
          action = line.slice('[ACTION]'.length).trim();
          continue;
        }
        if (line.startsWith('[PROGRESS]')) {
          try {
            const p = JSON.parse(line.slice('[PROGRESS]'.length));
            if (p.t === 'c') {
              duringEnum = true;
              const pct = Math.round((p.curSa / p.totalSa) * 100);
              const barW = 20;
              const filled = Math.round((p.curSa / p.totalSa) * barW);
              const bar = '█'.repeat(filled) + '░'.repeat(barW - filled);
              const progressLine = `  ${p.bldg}  ${String(p.cards).padStart(3)}张  累计${String(p.acc).padStart(5)}张  ${bar}  ${pct}%`;
              process.stdout.write('\r' + progressLine);
              needNewline = true;
              continue;
            }
          } catch (e) {}
        }
        if (duringEnum && /^\[\d{2}:\d{2}:\d{2}\] {4,}/.test(line)) continue;
        if (needNewline) { process.stdout.write('\n'); needNewline = false; }
        console.log(line);
      }
    });
    proc.on('close', code => {
      if (needNewline) process.stdout.write('\n');
      if (code === 0 && action !== 'switch_to_cdp' && action !== 'return') resolve();
      else if (action === 'switch_to_cdp') reject(new Error('SWITCH_TO_CDP'));
      else if (action === 'return') reject(new Error('RETURN_TO_MENU'));
      else reject(new Error(`${label || script} exited with code ${code}`));
    });
    proc.on('error', reject);
  });
}

async function doEnumeration(state, buildings, full) {
  const mode = state.edgeMode;
  const settingsArgs = getSettingCliArgs(state);
  if (full) {
    if (fs.existsSync(JSON_PATH)) { console.log('  清除旧数据...'); fs.unlinkSync(JSON_PATH); }
    await runScript(path.join(ROOT, 'src', 'enumerate.js'), [mode, ...settingsArgs], '全量采集');
  } else {
    const bldgArg = buildings.join(',');
    await runScript(path.join(ROOT, 'src', 'enumerate.js'), [mode, '--append', `--bldg=${bldgArg}`, ...settingsArgs], '采集 ' + buildings.join('、'));
  }
}

async function doImport(buildings) {
  const args = buildings && buildings.length < BLDG_ORDER.length ? [`--bldg=${buildings.join(',')}`] : [];
  await runScript(path.join(ROOT, 'scripts', 'import.js'), args, '导入数据库');
  await runScript(path.join(ROOT, 'scripts', 'quality-report.js'), [], '质量审计');
}

function dbStatus() {
  if (!fs.existsSync(DB_PATH)) return null;
  try {
    const Database = require('better-sqlite3');
    const db = new Database(DB_PATH, { readonly: true });
    const count = db.prepare('SELECT COUNT(*) AS c FROM cards').get().c;
    const ts = fs.statSync(DB_PATH).mtime;
    db.close();
    return { count, mtime: ts };
  } catch { return null; }
}

function loadDataForOverview() {
  if (!fs.existsSync(DB_PATH)) return null;
  try {
    const Database = require('better-sqlite3');
    const db = new Database(DB_PATH, { readonly: true });
    const rows = db.prepare('SELECT sa.building, c.switch, c.comm, c.name, p.layout FROM sub_areas sa JOIN pages p ON p.sub_area_id = sa.id JOIN cards c ON c.page_id = p.id').all();
    db.close();
    return rows.map(r => ({ ...r, pub: isPublic(r.name, r.layout) }));
  } catch { return null; }
}

function loadBuildingStats() {
  if (!fs.existsSync(DB_PATH)) return null;
  try {
    const Database = require('better-sqlite3');
    const db = new Database(DB_PATH, { readonly: true });
    const counts = db.prepare('SELECT sa.building, COUNT(*) AS total FROM sub_areas sa JOIN pages p ON p.sub_area_id = sa.id JOIN cards c ON c.page_id = p.id GROUP BY sa.building').all();
    let upd = {};
    try { const u = db.prepare('SELECT building, updated_at FROM buildings').all(); for (const r of u) upd[r.building] = r.updated_at; } catch {}
    db.close();
    return BLDG_ORDER.map(b => {
      const meta = BLDG_META[b];
      const total = (counts.find(c => c.building === b) || {}).total || 0;
      return { building: b, total, baseline: meta.baselineCards, delta: total - meta.baselineCards, updatedAt: upd[b] || null };
    });
  } catch { return null; }
}

function qualityLine(report) {
  if (!report) return '质量审计: 未生成';
  const s = report.summary || {};
  const parts = [];
  if (s.placeholder_cards) parts.push(`占位 ${s.placeholder_cards}`);
  if (s.state_mismatch) parts.push(`状态冲突 ${s.state_mismatch}`);
  if (s.unknown_comm) parts.push(`未知通讯 ${s.unknown_comm}`);
  if (s.duplicate_cards_same_page) parts.push(`重复卡 ${s.duplicate_cards_same_page}`);
  if (s.duplicate_rendered_pages) parts.push(`重复渲染页 ${s.duplicate_rendered_pages}`);
  if (s.empty_sub_areas) parts.push(`空子区 ${s.empty_sub_areas}`);
  if (s.suspicious_uniform_pages) parts.push(`疑似默认页 ${s.suspicious_uniform_pages}`);
  const issues = s.issue_count || 0;
  return issues > 0 ? `质量审计: ${issues} 项问题 (${parts.join(' / ') || '详见报告'})` : '质量审计: OK';
}

function qualityBadge(report) {
  if (!report) return '质量  未生成';
  const s = report.summary || {};
  const parts = [];
  if (s.placeholder_cards) parts.push(`P1 占位${s.placeholder_cards}`);
  if (s.state_mismatch) parts.push(`P1 状态冲突${s.state_mismatch}`);
  const p2 = [];
  if (s.unknown_comm) p2.push(`未知通讯${s.unknown_comm}`);
  if (s.duplicate_cards_same_page) p2.push(`重复卡${s.duplicate_cards_same_page}`);
  if (s.duplicate_rendered_pages) p2.push(`重复渲染页${s.duplicate_rendered_pages}`);
  if (s.empty_sub_areas) p2.push(`空子区${s.empty_sub_areas}`);
  if (s.suspicious_uniform_pages) p2.push(`疑似默认页${s.suspicious_uniform_pages}`);
  if (p2.length) parts.push('P2 ' + p2.join('/'));
  return parts.length ? '质量  ' + parts.join('  ') : '质量  OK';
}

function deltaLabel(delta) {
  if (delta === 0) return 'OK';
  return delta > 0 ? `+${delta}` : String(delta);
}

module.exports = {
  ROOT,
  DB_PATH,
  JSON_PATH,
  BLDG_ESTIMATE,
  detectEdgeMode,
  getSettingCliArgs,
  parseMin,
  saveLastCollect,
  loadLastCollect,
  loadQualityReport,
  doEnumeration,
  doImport,
  dbStatus,
  loadDataForOverview,
  loadBuildingStats,
  qualityLine,
  qualityBadge,
  deltaLabel,
};
