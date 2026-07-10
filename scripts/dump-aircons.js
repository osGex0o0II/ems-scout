// scripts/dump-aircons.js — legacy Excel 空调明细报表
// 当前产品唯一导出路径：原生应用 数据管理 -> 导出当前筛选 Excel。
// 本脚本只允许显式 legacy 应急启用，避免误生成旧口径交付物。

const path = require('path');
const Database = require('better-sqlite3');
const XLSX = require('xlsx');
const { BLDG_ORDER, BLDG_META, classifyAreaType } = require('../src/rules');

const ROOT = path.join(__dirname, '..');
const DB_PATH = path.join(ROOT, 'out', 'ac.db');
const LEGACY_ENABLE_ENV = 'EMS_ENABLE_LEGACY_REPORTS';

if (process.env[LEGACY_ENABLE_ENV] !== '1') {
  console.error(
    'Legacy dump-aircons is disabled. Use the native app path: 数据管理 -> 导出当前筛选 Excel.\n' +
    `Emergency legacy use: set ${LEGACY_ENABLE_ENV}=1 and rerun this command.`
  );
  process.exit(2);
}

function localDate() {
  const d = new Date();
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`;
}
const OUT_PATH = path.join(ROOT, '未关闭空调清单_' + localDate() + '.xlsx');

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

function duplicateRowNote(row) {
  const dupes = parseDuplicateNames(row.duplicate_names);
  const found = dupes.find(d => d && d.name === row.name);
  if (!found) return '';
  return `同页重复渲染 x${Number(found.copies) || 2}`;
}

const db = new Database(DB_PATH, { readonly: true });

// 查询所有 ON 设备
const sql = `
  SELECT
    sa.building, sa.id AS sub_id, sa.x, sa.y, sa.text AS sa_text, sa.floor,
    p.id AS page_id, p.page_name, p.layout, p.raw_count, p.unique_count, p.duplicate_names,
    c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan, c.comm
  FROM sub_areas sa
  JOIN pages p ON p.sub_area_id = sa.id
  JOIN cards c ON c.page_id = p.id
  WHERE c.switch = 'ON'
  ORDER BY sa.building, sa.y, sa.x, p.id, c.name
`;

const rows = db.prepare(sql).all();

// 字段富化: 加 area_type + (5/6号) zuo
const enriched = rows.map(r => {
  const areaType = classifyAreaType(r.name, r.layout);
  const meta = BLDG_META[r.building];
  const zuo = meta && meta.getZuo ? meta.getZuo(r.x) : '';
  return { ...r, area_type: areaType, zuo, row_duplicate_note: duplicateRowNote(r) };
});

// 汇总统计 (按楼栋 × 区域类型)
const summary = {};
for (const r of enriched) {
  if (!summary[r.building]) summary[r.building] = { 公区: 0, 非公区: 0 };
  summary[r.building][r.area_type]++;
}

const wb = XLSX.utils.book_new();

// 写入 sheet 辅助: 列定义
function rowsToAOA(items, hasZuo) {
  const header = hasZuo
    ? ['楼栋', '座号', '子区', '楼层', '子页', '设备名', '区域类型', '通讯状态', '模式', '室内温度', '设定温度', '风速', '备注']
    : ['楼栋', '子区', '楼层', '子页', '设备名', '区域类型', '通讯状态', '模式', '室内温度', '设定温度', '风速', '备注'];
  const aoa = [header];
  for (const r of items) {
    const comm = r.comm;
    if (hasZuo) {
      aoa.push([r.building, r.zuo, r.sa_text, r.floor, r.page_name, r.name, r.area_type, comm, r.mode, r.indoor, r.set_temp, r.fan, r.row_duplicate_note]);
    } else {
      aoa.push([r.building, r.sa_text, r.floor, r.page_name, r.name, r.area_type, comm, r.mode, r.indoor, r.set_temp, r.fan, r.row_duplicate_note]);
    }
  }
  return aoa;
}

// 1) 汇总 sheet
const sumHeader = ['楼栋', '楼栋全称', '公区 (台)', '非公区 (台)', '合计 (台)'];
const sumAOA = [sumHeader];
let totalG = 0, totalF = 0;
for (const [b, meta] of Object.entries(BLDG_META)) {
  const g = summary[b] ? summary[b].公区 : 0;
  const f = summary[b] ? summary[b].非公区 : 0;
  sumAOA.push([b, meta.fullName, g, f, g + f]);
  totalG += g; totalF += f;
}
sumAOA.push(['合计', '', totalG, totalF, totalG + totalF]);
const sumWS = XLSX.utils.aoa_to_sheet(sumAOA);
sumWS['!cols'] = [{ wch: 8 }, { wch: 22 }, { wch: 12 }, { wch: 12 }, { wch: 12 }];
XLSX.utils.book_append_sheet(wb, sumWS, '汇总');

// 2) 每栋楼一个 sheet
for (const bldg of BLDG_ORDER) {
  const meta = BLDG_META[bldg];
  const hasZuo = !!meta.getZuo;
  const items = enriched.filter(r => r.building === bldg);

  // 按 area_type 分组, 公区在前
  const gq = items.filter(r => r.area_type === '公区');
  const fg = items.filter(r => r.area_type === '非公区');

  // 排序: floor 升序, x 升序, name 升序
  const sortFn = (a, b) => a.floor - b.floor || a.x - b.x || a.name.localeCompare(b.name);
  gq.sort(sortFn);
  fg.sort(sortFn);

  const aoa = rowsToAOA(gq, hasZuo);

  // 分隔行: 仅当两侧都有数据时
  if (gq.length > 0 && fg.length > 0) {
    const sepRow = hasZuo
      ? ['─'.repeat(8), '─'.repeat(4), '─'.repeat(4), '─'.repeat(4), '─'.repeat(6), '── 非公区空调 ──', '', '', '', '', '', '', '']
      : ['─'.repeat(8), '─'.repeat(4), '─'.repeat(4), '─'.repeat(6), '── 非公区空调 ──', '', '', '', '', '', '', ''];
    aoa.push(sepRow);
  }

  // 非公区行
  for (const r of fg) {
    const comm = r.comm;
    if (hasZuo) {
      aoa.push([r.building, r.zuo, r.sa_text, r.floor, r.page_name, r.name, r.area_type, comm, r.mode, r.indoor, r.set_temp, r.fan, r.row_duplicate_note]);
    } else {
      aoa.push([r.building, r.sa_text, r.floor, r.page_name, r.name, r.area_type, comm, r.mode, r.indoor, r.set_temp, r.fan, r.row_duplicate_note]);
    }
  }

  const ws = XLSX.utils.aoa_to_sheet(aoa);
  // 列宽
  ws['!cols'] = hasZuo
    ? [{ wch: 8 }, { wch: 8 }, { wch: 8 }, { wch: 8 }, { wch: 12 }, { wch: 24 }, { wch: 10 }, { wch: 10 }, { wch: 14 }, { wch: 10 }, { wch: 10 }, { wch: 8 }, { wch: 28 }]
    : [{ wch: 8 }, { wch: 8 }, { wch: 8 }, { wch: 12 }, { wch: 24 }, { wch: 10 }, { wch: 10 }, { wch: 14 }, { wch: 10 }, { wch: 10 }, { wch: 8 }, { wch: 28 }];
  // 自动筛选
  ws['!autofilter'] = { ref: XLSX.utils.encode_range({ s: { r: 0, c: 0 }, e: { r: aoa.length - 1, c: (hasZuo ? 12 : 11) } }) };
  // 冻结首行
  ws['!freeze'] = { xSplit: 0, ySplit: 1 };

  XLSX.utils.book_append_sheet(wb, ws, bldg);
}

XLSX.writeFile(wb, OUT_PATH);
console.log('Saved:', OUT_PATH);
console.log('Total ON (switch) cards:', enriched.length);
console.log('  公区:', enriched.filter(r => r.area_type === '公区').length);
console.log('  非公区:', enriched.filter(r => r.area_type === '非公区').length);
console.log('  开机:', enriched.filter(r => r.comm === '开机').length);
console.log('  关机:', enriched.filter(r => r.comm === '关机').length);
console.log('  离线:', enriched.filter(r => r.comm === '离线').length);
console.log('  重复渲染标注:', enriched.filter(r => r.row_duplicate_note).length);
