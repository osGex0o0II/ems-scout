const path = require('path');
const Database = require('better-sqlite3');
const { BLDG_ORDER, BLDG_META, classifyAreaType } = require('../src/rules');

const ROOT = path.join(__dirname, '..');
const DB_PATH = path.join(ROOT, 'out', 'ac.db');
const LEGACY_ENABLE_ENV = 'EMS_ENABLE_LEGACY_REPORTS';

if (process.env[LEGACY_ENABLE_ENV] !== '1') {
  console.error(
    'Legacy dump-public is disabled. Use the native app path: 数据管理 -> 导出当前筛选 Excel.\n' +
    `Emergency legacy use: set ${LEGACY_ENABLE_ENV}=1 and rerun this command.`
  );
  process.exit(2);
}

// 本地时间格式化
function localDate() {
  const d = new Date();
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`;
}
function localDateTime() {
  const d = new Date();
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

const OUT_PATH = path.join(ROOT, '公区未关闭空调_' + localDate() + '.txt');

// 中文页码 → 数字排序
const PAGE_ORDER = { '一页': 1, '二页': 2, '三页': 3, '四页': 4, '五页': 5, '六页': 6, '七页': 7, '八页': 8, '九页': 9, '十页': 10, 'default': 0 };
function pageSortKey(pageName) {
  return PAGE_ORDER[pageName] !== undefined ? PAGE_ORDER[pageName] : 99;
}

function parseDuplicateNames(value) {
  if (!value) return [];
  if (Array.isArray(value)) return value;
  try {
    const parsed = JSON.parse(value);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function duplicateNote(rawCount, uniqueCount, duplicateNames) {
  const raw = Number(rawCount) || 0;
  const unique = Number(uniqueCount) || 0;
  const dupes = parseDuplicateNames(duplicateNames);
  if (!(raw > unique)) return '';
  const names = dupes
    .filter(d => d && d.name)
    .map(d => `${d.name}x${Number(d.copies) || 2}`)
    .join(', ');
  return names ? `重复渲染 ${raw}->${unique}: ${names}` : `重复渲染 ${raw}->${unique}`;
}

function duplicateRowNote(row) {
  const dupes = parseDuplicateNames(row.duplicate_names);
  const found = dupes.find(d => d && d.name === row.name);
  if (!found) return '';
  return `同页重复渲染 x${Number(found.copies) || 2}`;
}

const t0 = Date.now();
const db = new Database(DB_PATH, { readonly: true });
const rows = db.prepare(`
  SELECT sa.building, sa.text AS sa_text, sa.floor, sa.x, sa.y,
         p.page_name, p.layout, p.raw_count, p.unique_count, p.duplicate_names,
         c.name, c.mode, c.indoor, c.set_temp, c.fan, c.comm
  FROM sub_areas sa
  JOIN pages p ON p.sub_area_id = sa.id
  JOIN cards c ON c.page_id = p.id
  WHERE c.switch = 'ON'
  ORDER BY sa.building, sa.y, sa.x, p.id, c.name
`).all();

const items = rows.filter(r => classifyAreaType(r.name, r.layout) === '公区')
  .map(r => {
    let zuo = '';
    const meta = BLDG_META[r.building];
    if (meta && meta.zuoFn) zuo = meta.zuoFn(r.x);
    return {
      ...r,
      zuo,
      pageDuplicateNote: duplicateNote(r.raw_count, r.unique_count, r.duplicate_names),
      rowDuplicateNote: duplicateRowNote(r),
    };
  });

const lines = [];
lines.push('公区未关闭空调清单');
lines.push('生成时间: ' + localDateTime());
lines.push('总耗时: ' + ((Date.now() - t0) / 1000).toFixed(1) + 's');
lines.push('总数: ' + items.length + ' 台');
lines.push('公区识别规则: layout=group，或命名含 GQ/WSJ/DTT/FDT/XFDT/CSJ/FWJ/ZBS/ZSG/MD/RDJHJF；QL-数字 房间排除');
lines.push('='.repeat(72));
lines.push('');

for (const bldg of BLDG_ORDER) {
  const sub = items.filter(r => r.building === bldg);
  if (sub.length === 0) continue;
  lines.push('【' + bldg + (BLDG_META[bldg] ? ' ' + BLDG_META[bldg].name : '') + '】 ' + sub.length + ' 台');

  // 按 (floor, sa_text, page_name) 分组，page 按数字排序
  const byGroup = {};
  for (const r of sub) {
    const key = r.floor + '|' + r.sa_text + '|' + r.page_name;
    if (!byGroup[key]) byGroup[key] = { floor: r.floor, sa_text: r.sa_text, page: r.page_name, items: [] };
    byGroup[key].items.push(r);
  }
  const sorted = Object.values(byGroup).sort((a, b) => {
    if (a.floor !== b.floor) return a.floor - b.floor;
    if (a.sa_text !== b.sa_text) return a.sa_text.localeCompare(b.sa_text);
    return pageSortKey(a.page) - pageSortKey(b.page);
  });

  for (const g of sorted) {
    const dupNote = g.items[0] ? g.items[0].pageDuplicateNote : '';
    const loc = g.sa_text + ' (F' + g.floor + ')' + (g.page !== 'default' ? ' [' + g.page + ']' : '') + (dupNote ? '  [' + dupNote + ']' : '');
    lines.push('  ' + loc);
    for (const r of g.items) {
      const zuo = r.zuo ? r.zuo + ' ' : '';
      // 不重复显示"开机"——该清单全为未关闭设备
      const commTag = r.comm === '离线' ? ' [离线]' : r.comm === '关机' ? ' [关机]' : '';
      const state = [r.mode, r.indoor + '℃', r.set_temp + '℃', r.fan].filter(Boolean).join('  ');
      const namePart = zuo + r.name;
      const note = r.rowDuplicateNote ? '  [' + r.rowDuplicateNote + ']' : '';
      lines.push('    ' + namePart.padEnd(20) + '  ' + commTag.padEnd(6) + state + note);
    }
  }
  lines.push('');
}

lines.push('='.repeat(72) + '  END');

const fs = require('fs');
fs.writeFileSync(OUT_PATH, lines.join('\n'), 'utf8');
console.log('Saved:', OUT_PATH);
console.log('Total public-area ON:', items.length);
console.log('Duplicate-render notes:', items.filter(r => r.rowDuplicateNote).length);
