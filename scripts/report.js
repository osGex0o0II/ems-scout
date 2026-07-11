const path = require('path');
const fs = require('fs');
const Database = require('better-sqlite3');
const XLSX = require('xlsx');
const { BLDG_ORDER, BLDG_META, isPublic, classifyAreaType } = require('../src/rules');
const { resolveRunId, sourceForRun } = require('../src/panel/history');

const ROOT = path.join(__dirname, '..');
const DB_PATH = path.join(ROOT, 'out', 'ac.db');
const LEGACY_ENABLE_ENV = 'EMS_ENABLE_LEGACY_REPORTS';

function legacyReportsEnabled() {
  return process.env[LEGACY_ENABLE_ENV] === '1';
}

function requireLegacyReportsEnabled() {
  if (legacyReportsEnabled()) return;
  throw new Error(
    'Legacy multi-format reports are disabled. Use the native app path: 数据管理 -> 导出当前筛选 Excel. ' +
    `If this is an emergency legacy run, set ${LEGACY_ENABLE_ENV}=1 explicitly.`
  );
}

function localDateTime() {
  const d = new Date();
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}_${pad(d.getHours())}${pad(d.getMinutes())}`;
}

const PAGE_ORDER = { '一页':1,'二页':2,'三页':3,'四页':4,'五页':5,'六页':6,'七页':7,'八页':8,'九页':9,'十页':10, default:0 };
const NATURAL_COLLATOR = new Intl.Collator('zh-CN', { numeric: true, sensitivity: 'base' });

function pagePrefix(pn) { const i = pn.indexOf('/'); return i >= 0 ? pn.slice(0, i) : ''; }
function pageShortName(pn) { const i = pn.indexOf('/'); return i >= 0 ? pn.slice(i + 1) : pn; }
function naturalCompare(a, b) { return NATURAL_COLLATOR.compare(String(a ?? ''), String(b ?? '')); }
function bldgCompare(a, b) {
  const ia = BLDG_ORDER.indexOf(a);
  const ib = BLDG_ORDER.indexOf(b);
  if (ia !== ib) return (ia < 0 ? 999 : ia) - (ib < 0 ? 999 : ib);
  return naturalCompare(a, b);
}
function pageNameCompare(a, b) {
  const oa = PAGE_ORDER[a] !== undefined ? PAGE_ORDER[a] : 99;
  const ob = PAGE_ORDER[b] !== undefined ? PAGE_ORDER[b] : 99;
  if (oa !== ob) return oa - ob;
  return naturalCompare(a, b);
}
function rowCompare(a, b) {
  return bldgCompare(a.building, b.building)
    || naturalCompare(a.zuo || '', b.zuo || '')
    || (Number(a.floor) - Number(b.floor))
    || (Number(a.y || 0) - Number(b.y || 0))
    || (Number(a.x || 0) - Number(b.x || 0))
    || naturalCompare(pagePrefix(a.page_name || ''), pagePrefix(b.page_name || ''))
    || pageNameCompare(pageShortName(a.page_name || ''), pageShortName(b.page_name || ''))
    || naturalCompare(a.name, b.name);
}
function sortRows(items) {
  return [...items].sort(rowCompare);
}

function anomalyTag(r) {
  const tags = [];
  if (!r.comm) tags.push('通讯未知');
  if (r.comm === '离线') tags.push('通讯离线');
  if (r.switch === 'ON' && r.comm === '离线') tags.push('开机但离线');
  if (r.switch === '-') tags.push('开关未知');
  if (!r.indicator && r.comm !== '离线') tags.push('缺 indicator');
  if (r.mode === '-' && r.switch === 'ON') tags.push('模式未知');
  if (r.indoor && r.indoor !== '-' && r.indoor !== '0') {
    const iv = parseFloat(r.indoor);
    if (!isNaN(iv) && (iv < 5 || iv > 50)) tags.push('室内温度异常(' + r.indoor + '℃)');
  }
  if (r.set_temp && r.set_temp !== '-') {
    const sv = parseFloat(r.set_temp);
    if (!isNaN(sv) && (sv < 5 || sv > 40)) tags.push('设定温度异常(' + r.set_temp + '℃)');
  }
  return tags;
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

function loadCollectInfo(runId = null) {
  if (!fs.existsSync(DB_PATH)) return null;
  try {
    const db = new Database(DB_PATH, { readonly: true });
    if (runId) {
      const run = db.prepare('SELECT id, completed_at, buildings FROM collection_runs WHERE id = ?').get(runId);
      db.close();
      if (!run) return null;
      const lastDt = new Date(run.completed_at);
      const pad = n => String(n).padStart(2, '0');
      const lastTs = `${lastDt.getFullYear()}-${pad(lastDt.getMonth()+1)}-${pad(lastDt.getDate())} ${pad(lastDt.getHours())}:${pad(lastDt.getMinutes())}`;
      let bldgCount = 0;
      try { bldgCount = JSON.parse(run.buildings || '[]').length; } catch {}
      return { lastTs, lastIso: run.completed_at, bldgCount, durStr: '--' };
    }
    let rows;
    try { rows = db.prepare("SELECT building, updated_at FROM buildings WHERE updated_at IS NOT NULL ORDER BY updated_at").all(); } catch { rows = []; }
    db.close();
    if (rows.length === 0) return null;
    const timestamps = rows.map(r => r.updated_at).sort();
    const last = timestamps[timestamps.length - 1];
    const lastDt = new Date(last);
    const pad = n => String(n).padStart(2, '0');
    const lastTs = `${lastDt.getFullYear()}-${pad(lastDt.getMonth()+1)}-${pad(lastDt.getDate())} ${pad(lastDt.getHours())}:${pad(lastDt.getMinutes())}`;

    // Read actual duration from last collect log
    const lastLog = path.join(ROOT, 'out', '.last_collect.json');
    let durStr = '--';
    try {
      const log = JSON.parse(fs.readFileSync(lastLog, 'utf8'));
      const d = log.duration;
      if (d && !isNaN(d)) durStr = d >= 60 ? (d / 60).toFixed(1) + ' 分钟' : d + ' 秒';
    } catch {}

    return { lastTs, lastIso: timestamps[timestamps.length - 1], bldgCount: rows.length, durStr };
  } catch { return null; }
}

function loadData(runIdInput = null) {
  if (!fs.existsSync(DB_PATH)) return null;
  const db = new Database(DB_PATH, { readonly: true });
  const runId = resolveRunId(db, runIdInput);
  const source = sourceForRun(runId);
  const rows = db.prepare(`
    SELECT sa.building, sa.text AS sa_text, sa.floor, sa.x, sa.y,
           p.page_name, p.layout, p.raw_count, p.unique_count, p.duplicate_names,
           c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan, c.indicator, c.comm
    FROM ${source.subAreas} sa
    JOIN ${source.pages} p ON p.${source.pageSaColumn} = sa.id
    JOIN ${source.cards} c ON c.${source.cardPageColumn} = p.id
    ${source.runWhere ? 'WHERE ' + source.runWhere : ''}
    ORDER BY sa.building, sa.y, sa.x, p.id, c.name
  `).all(...source.runParams);
  db.close();
  return rows.map(r => {
    const meta = BLDG_META[r.building];
    const zuo = meta && meta.zuoFn ? meta.zuoFn(r.x) : '';
    const pub = isPublic(r.name, r.layout);
    const anomaly = anomalyTag(r);
    const pageDuplicateNote = duplicateNote(r.raw_count, r.unique_count, r.duplicate_names);
    const rowDuplicateNote = duplicateRowNote(r);
    const base = { ...r, zuo, pub, anomaly, pageDuplicateNote, rowDuplicateNote };
    const risk = riskInfo(base);
    return { ...base, riskScore: risk.score, riskLevel: risk.level, riskReasons: risk.reasons };
  }).sort(rowCompare);
}

function mdAnchor(text) {
  return '#' + text.toLowerCase().replace(/\s+/g, '-').replace(/[^\w\u4e00-\u9fff-]/g, '').replace(/-+/g, '-');
}

function renderAnomalyBadge(tags) {
  if (tags.length === 0) return '';
  return ' ⚠️ `' + tags.join('`, `') + '`';
}

function switchLabel(sw) {
  return sw === 'ON' ? '开机' : sw === 'OFF' ? '关机' : '-';
}

function riskInfo(r) {
  if (r.switch !== 'ON') return { score: 0, level: '', reasons: [] };
  let score = 0;
  const reasons = [];
  const mode = String(r.mode || '');
  const setTemp = parseFloat(r.set_temp);

  if (r.comm === '离线') { score += 80; reasons.push('开机但通讯离线'); }
  if (r.pub) { score += 50; reasons.push('公区开机'); }
  if (!Number.isNaN(setTemp) && mode.includes('制冷') && setTemp <= 20) {
    score += 30; reasons.push('制冷设定≤20℃');
  } else if (!Number.isNaN(setTemp) && setTemp <= 20) {
    score += 20; reasons.push('设定≤20℃');
  }
  if (['高', '强'].includes(String(r.fan || ''))) { score += 10; reasons.push('高风速'); }
  if (r.mode === '-') { score += 10; reasons.push('模式未知'); }
  if (r.rowDuplicateNote) { score += 5; reasons.push('重复渲染页'); }

  const level = score >= 80 ? '高' : score >= 50 ? '中' : score > 0 ? '低' : '';
  return { score, level, reasons };
}

function riskCompare(a, b) {
  return (Number(b.riskScore || 0) - Number(a.riskScore || 0)) || rowCompare(a, b);
}

function applySheetView(ws, aoa, opts = {}) {
  const rows = aoa.length;
  const cols = rows > 0 ? Math.max(...aoa.map(r => r.length)) : 0;
  if (opts.autofilter !== false && rows > 0 && cols > 0) {
    const headerRow = opts.headerRow || 0;
    ws['!autofilter'] = { ref: XLSX.utils.encode_range({ s: { r: headerRow, c: 0 }, e: { r: Math.max(headerRow, rows - 1), c: cols - 1 } }) };
  }
  if (opts.freeze !== false) ws['!freeze'] = { xSplit: 0, ySplit: opts.freezeRows || 1 };
}

function addSummarySheet(wb, data, ts, sheetName = '汇总') {
  const s = reportSummary(data);
  const risks = topRiskRows(data, 15);
  const floors = s.on > 0 ? topFloorStats(data, 10) : [];
  const zones = zoneStats(data);
  const riskDist = riskDistribution(data);
  const anomalyDist = anomalyDistribution(data);
  const aoa = [
    ['报表摘要', ''],
    ['生成时间', ts],
    ['设备总数', s.total],
    ['开机', s.on],
    ['关机', s.off],
    ['离线', s.offline],
    ['公区', s.pub],
    ['非公区', s.nonPub],
    ['公区开机', s.pubOn],
    ['非公区开机', s.nonPubOn],
    ['未知通讯', s.unknownComm],
    ['缺失 indicator', s.missingIndicator],
    ['重复渲染页', s.duplicatePages.length],
    [],
    ['楼栋', '楼栋全称', '设备总数', '开机', '关机', '离线', '公区开机', '基准'],
  ];
  for (const b of s.buildings) {
    const baseline = b.baselineCards ? `${b.total}/${b.baselineCards}` : `${b.total}`;
    aoa.push([b.building, b.name, b.total, b.on, b.off, b.offline, b.pubOn, baseline]);
  }
  aoa.push([]);
  aoa.push(['数据质量说明']);
  aoa.push(['楼栋', '楼层', '子区', '页面', 'raw', 'unique', '重复卡']);
  if (s.duplicatePages.length === 0) {
    aoa.push(['-', '-', '-', '-', 0, 0, '无']);
  } else {
    for (const p of s.duplicatePages) {
      const names = p.duplicateNames.map(d => `${d.name}x${Number(d.copies) || 2}`).join(', ');
      aoa.push([p.building, `F${p.floor}`, p.subArea, p.page, p.raw, p.unique, names || '-']);
    }
  }
  aoa.push([]);
  aoa.push(['风险分布']);
  aoa.push(['风险等级', '数量']);
  for (const level of ['高', '中', '低']) {
    aoa.push([level, riskDist[level]]);
  }
  aoa.push([]);
  aoa.push(['异常分布']);
  aoa.push(['异常标签', '数量']);
  if (anomalyDist.length === 0) {
    aoa.push(['无', 0]);
  } else {
    for (const r of anomalyDist) aoa.push([r.tag, r.count]);
  }
  aoa.push([]);
  aoa.push(['开机风险 Top']);
  aoa.push(['风险等级', '风险分', '楼栋', '楼层', '子区', '设备名', '区域类型', '模式', '设定温度', '风速', '原因']);
  if (risks.length === 0) {
    aoa.push(['-', 0, '-', '-', '-', '-', '-', '-', '-', '-', '无']);
  } else {
    for (const r of risks) {
      aoa.push([r.riskLevel, r.riskScore, r.building + (r.zuo ? ' ' + r.zuo : ''), `F${r.floor}`, r.sa_text, r.name, r.pub ? '公区' : '非公区', r.mode, r.set_temp, r.fan, r.riskReasons.join(', ')]);
    }
  }
  aoa.push([]);
  aoa.push(['楼层开机 Top']);
  aoa.push(['楼栋', '楼层', '子区', '总数', '开机', '公区开机', '离线']);
  if (floors.length === 0) {
    aoa.push(['-', '-', '-', 0, 0, 0, 0]);
  } else {
    for (const r of floors) {
      aoa.push([r.building + (r.zuo ? ' ' + r.zuo : ''), `F${r.floor}`, r.subArea, r.total, r.on, r.pubOn, r.offline]);
    }
  }
  aoa.push([]);
  aoa.push(['座区统计']);
  aoa.push(['楼栋', '座区', '总数', '开机', '关机', '离线', '公区开机']);
  if (zones.length === 0) {
    aoa.push(['-', '-', 0, 0, 0, 0, 0]);
  } else {
    for (const r of zones) {
      aoa.push([r.building, r.zuo, r.total, r.on, r.off, r.offline, r.pubOn]);
    }
  }
  aoa.push([]);
  aoa.push(['异常标签说明']);
  aoa.push(['通讯未知', 'comm 缺失或为空']);
  aoa.push(['开关未知', 'switch=-，通常表示开关图片或状态未解析']);
  aoa.push(['缺 indicator', '非离线设备缺失通讯指示图']);
  aoa.push(['通讯离线', 'comm=离线']);
  aoa.push(['开机但离线', 'switch=ON 且 comm=离线']);
  aoa.push(['模式未知', '开机设备 mode=-']);
  aoa.push(['室内温度异常', '室内温度小于5℃或大于50℃']);
  aoa.push(['设定温度异常', '设定温度小于5℃或大于40℃']);
  aoa.push([]);
  aoa.push(['口径说明']);
  aoa.push(['开机', 'switch=ON']);
  aoa.push(['未开启', 'switch!=ON，包含普通关机和离线/异常设备']);
  aoa.push(['离线', 'comm=离线或switch=-']);
  aoa.push(['重复渲染页', '按唯一设备入库，raw/unique 在备注中标注']);
  aoa.push(['风险评分', '公区开机、低设定温度、高风速、离线异常、重复渲染等因素累加，仅用于处置优先级排序']);

  const ws = XLSX.utils.aoa_to_sheet(aoa);
  ws['!cols'] = [{ wch: 14 }, { wch: 24 }, { wch: 16 }, { wch: 10 }, { wch: 10 }, { wch: 28 }, { wch: 10 }, { wch: 14 }, { wch: 10 }, { wch: 8 }, { wch: 36 }];
  applySheetView(ws, aoa, { autofilter: false, freezeRows: 1 });
  XLSX.utils.book_append_sheet(wb, ws, sheetName);
}

function reportSummary(data) {
  const total = data.length;
  const on = data.filter(r => r.switch === 'ON').length;
  const off = data.filter(r => r.switch === 'OFF').length;
  const offline = data.filter(r => r.comm === '离线' || r.switch === '-').length;
  const pub = data.filter(r => r.pub).length;
  const nonPub = total - pub;
  const pubOn = data.filter(r => r.pub && r.switch === 'ON').length;
  const nonPubOn = data.filter(r => !r.pub && r.switch === 'ON').length;
  const unknownComm = data.filter(r => !r.comm).length;
  const missingIndicator = data.filter(r => !r.indicator && r.comm !== '离线').length;
  const anomaly = data.filter(r => r.anomaly.length > 0).length;
  const duplicatePages = duplicatePageSummary(data);
  const buildings = BLDG_ORDER.map(building => {
    const items = data.filter(r => r.building === building);
    if (items.length === 0) return null;
    const meta = BLDG_META[building] || {};
    const floors = new Set(items.map(r => String(r.floor))).size;
    return {
      building,
      name: meta.full || '',
      total: items.length,
      on: items.filter(r => r.switch === 'ON').length,
      off: items.filter(r => r.switch === 'OFF').length,
      offline: items.filter(r => r.comm === '离线' || r.switch === '-').length,
      pubOn: items.filter(r => r.pub && r.switch === 'ON').length,
      floors,
      baselineCards: meta.baselineCards || 0,
      baselineSubAreas: meta.baselineSubAreas || 0,
    };
  }).filter(Boolean);
  return { total, on, off, offline, pub, nonPub, pubOn, nonPubOn, unknownComm, missingIndicator, anomaly, duplicatePages, buildings };
}

function duplicatePageSummary(data) {
  const map = new Map();
  for (const r of data) {
    const raw = Number(r.raw_count) || 0;
    const unique = Number(r.unique_count) || 0;
    if (!(raw > unique)) continue;
    const key = [r.building, r.floor, r.sa_text, r.page_name, raw, unique, r.duplicate_names || ''].join('|');
    if (!map.has(key)) {
      map.set(key, {
        building: r.building,
        floor: r.floor,
        subArea: r.sa_text,
        page: r.page_name,
        raw,
        unique,
        duplicateNames: parseDuplicateNames(r.duplicate_names),
        note: duplicateNote(raw, unique, r.duplicate_names),
      });
    }
  }
  return [...map.values()].sort((a, b) => bldgCompare(a.building, b.building)
    || (Number(a.floor) - Number(b.floor))
    || naturalCompare(a.subArea, b.subArea)
    || pageNameCompare(pageShortName(a.page), pageShortName(b.page)));
}

function topFloorStats(data, limit = 10) {
  const map = new Map();
  for (const r of data) {
    const key = [r.building, r.zuo || '', r.floor, r.sa_text || ''].join('|');
    if (!map.has(key)) map.set(key, { building: r.building, zuo: r.zuo || '', floor: r.floor, subArea: r.sa_text || '', total: 0, on: 0, off: 0, offline: 0, pubOn: 0 });
    const row = map.get(key);
    row.total++;
    if (r.switch === 'ON') row.on++;
    if (r.switch === 'OFF') row.off++;
    if (r.comm === '离线' || r.switch === '-') row.offline++;
    if (r.pub && r.switch === 'ON') row.pubOn++;
  }
  return [...map.values()]
    .sort((a, b) => (b.on - a.on) || (b.pubOn - a.pubOn) || bldgCompare(a.building, b.building) || naturalCompare(a.zuo, b.zuo) || (Number(a.floor) - Number(b.floor)))
    .slice(0, limit);
}

function zoneStats(data) {
  const map = new Map();
  for (const r of data.filter(x => x.zuo)) {
    const key = [r.building, r.zuo].join('|');
    if (!map.has(key)) map.set(key, { building: r.building, zuo: r.zuo, total: 0, on: 0, off: 0, offline: 0, pubOn: 0 });
    const row = map.get(key);
    row.total++;
    if (r.switch === 'ON') row.on++;
    if (r.switch === 'OFF') row.off++;
    if (r.comm === '离线' || r.switch === '-') row.offline++;
    if (r.pub && r.switch === 'ON') row.pubOn++;
  }
  return [...map.values()].sort((a, b) => bldgCompare(a.building, b.building) || naturalCompare(a.zuo, b.zuo));
}

function topRiskRows(data, limit = 30) {
  return data.filter(r => r.switch === 'ON' && r.riskScore > 0).sort(riskCompare).slice(0, limit);
}

function allRiskRows(data) {
  return data.filter(r => r.switch === 'ON' && r.riskScore > 0).sort(riskCompare);
}

function allAnomalyRows(data) {
  return data.filter(r => (r.anomaly && r.anomaly.length > 0) || r.rowDuplicateNote).sort((a, b) => {
    const as = a.comm === '离线' ? 2 : (a.anomaly && a.anomaly.length > 0 ? 1 : 0);
    const bs = b.comm === '离线' ? 2 : (b.anomaly && b.anomaly.length > 0 ? 1 : 0);
    return (bs - as) || rowCompare(a, b);
  });
}

function riskDistribution(data) {
  const dist = { 高: 0, 中: 0, 低: 0 };
  for (const r of data) {
    if (r.switch === 'ON' && r.riskLevel && dist[r.riskLevel] !== undefined) dist[r.riskLevel]++;
  }
  return dist;
}

function anomalyDistribution(data) {
  const map = new Map();
  for (const r of data) {
    for (const tag of r.anomaly || []) map.set(tag, (map.get(tag) || 0) + 1);
    if (r.rowDuplicateNote) map.set('同页重复渲染', (map.get('同页重复渲染') || 0) + 1);
  }
  return [...map.entries()].map(([tag, count]) => ({ tag, count }))
    .sort((a, b) => (b.count - a.count) || naturalCompare(a.tag, b.tag));
}

function detailRowsToAOA(rows, mode) {
  const isRisk = mode === 'risk';
  const header = isRisk
    ? ['风险等级', '风险分', '风险原因', '楼栋', '座号', '子区', '楼层', '子页', '设备名', '区域类型', '通讯状态', '模式', '室内温度', '设定温度', '风速', '备注']
    : ['异常标签', '楼栋', '座号', '子区', '楼层', '子页', '设备名', '区域类型', '开关', '通讯状态', '模式', '室内温度', '设定温度', '风速', '风险等级', '风险分', '备注'];
  const aoa = [header];
  if (rows.length === 0) {
    aoa.push(isRisk
      ? ['-', 0, '无', '-', '-', '-', '-', '-', '-', '-', '-', '-', '-', '-', '-', '-']
      : ['无', '-', '-', '-', '-', '-', '-', '-', '-', '-', '-', '-', '-', '-', '-', 0, '-']);
    return aoa;
  }
  for (const r of rows) {
    const areaType = r.area_type || (r.pub ? '公区' : '非公区');
    const note = [r.rowDuplicateNote || '', r.pageDuplicateNote || ''].filter(Boolean).join('；');
    if (isRisk) {
      aoa.push([r.riskLevel || '', r.riskScore || 0, (r.riskReasons || []).join(', '), r.building, r.zuo || '', r.sa_text, r.floor, r.page_name, r.name, areaType, r.comm, r.mode, r.indoor, r.set_temp, r.fan, note]);
    } else {
      const tags = [...(r.anomaly || [])];
      if (r.rowDuplicateNote) tags.push('同页重复渲染');
      aoa.push([tags.join(', ') || '-', r.building, r.zuo || '', r.sa_text, r.floor, r.page_name, r.name, areaType, switchLabel(r.switch), r.comm, r.mode, r.indoor, r.set_temp, r.fan, r.riskLevel || '', r.riskScore || 0, note]);
    }
  }
  return aoa;
}

function addDetailSheets(wb, data) {
  const riskAOA = detailRowsToAOA(allRiskRows(data), 'risk');
  const riskWS = XLSX.utils.aoa_to_sheet(riskAOA);
  riskWS['!cols'] = [{ wch: 10 }, { wch: 8 }, { wch: 36 }, { wch: 8 }, { wch: 8 }, { wch: 10 }, { wch: 8 }, { wch: 12 }, { wch: 24 }, { wch: 10 }, { wch: 10 }, { wch: 14 }, { wch: 10 }, { wch: 10 }, { wch: 8 }, { wch: 32 }];
  applySheetView(riskWS, riskAOA);
  XLSX.utils.book_append_sheet(wb, riskWS, '风险明细');

  const anomalyAOA = detailRowsToAOA(allAnomalyRows(data), 'anomaly');
  const anomalyWS = XLSX.utils.aoa_to_sheet(anomalyAOA);
  anomalyWS['!cols'] = [{ wch: 24 }, { wch: 8 }, { wch: 8 }, { wch: 10 }, { wch: 8 }, { wch: 12 }, { wch: 24 }, { wch: 10 }, { wch: 8 }, { wch: 10 }, { wch: 14 }, { wch: 10 }, { wch: 10 }, { wch: 8 }, { wch: 10 }, { wch: 8 }, { wch: 32 }];
  applySheetView(anomalyWS, anomalyAOA);
  XLSX.utils.book_append_sheet(wb, anomalyWS, '异常明细');
}

function appendMdOverview(lines, data, ts, label, filteredCount = data.length) {
  const s = reportSummary(data);
  const risks = topRiskRows(data, 15);
  const floors = topFloorStats(data, 10);
  const zones = zoneStats(data);
  const riskDist = riskDistribution(data);
  const anomalyDist = anomalyDistribution(data);
  lines.push('## 报表摘要');
  lines.push('');
  lines.push(`- 生成时间：${ts}`);
  lines.push(`- 本报表设备数：${filteredCount} 台`);
  lines.push(`- 全库设备数：${s.total} 台；开机 ${s.on} 台，关机 ${s.off} 台，离线 ${s.offline} 台`);
  lines.push(`- 公区 ${s.pub} 台，非公区 ${s.nonPub} 台；公区开机 ${s.pubOn} 台，非公区开机 ${s.nonPubOn} 台`);
  lines.push(`- 数据质量：未知通讯 ${s.unknownComm}，缺失 indicator ${s.missingIndicator}，重复渲染页 ${s.duplicatePages.length}`);
  lines.push(`- 风险分布：高 ${riskDist.高}，中 ${riskDist.中}，低 ${riskDist.低}`);
  lines.push('');
  lines.push('### 楼栋汇总');
  lines.push('');
  lines.push('| 楼栋 | 设备数 | 开机 | 关机 | 离线 | 公区开机 | 基准 |');
  lines.push('|------|--------|------|------|------|----------|------|');
  for (const b of s.buildings) {
    const baseline = b.baselineCards ? `${b.total}/${b.baselineCards}` : `${b.total}`;
    lines.push(`| ${b.building} ${b.name} | ${b.total} | ${b.on} | ${b.off} | ${b.offline} | ${b.pubOn} | ${baseline} |`);
  }
  lines.push('');
  if (s.duplicatePages.length > 0) {
    lines.push('### 数据质量说明');
    lines.push('');
    lines.push('| 楼栋 | 楼层 | 子区 | 页面 | raw | unique | 重复卡 |');
    lines.push('|------|------|------|------|-----|--------|--------|');
    for (const p of s.duplicatePages) {
      const names = p.duplicateNames.map(d => `${d.name}x${Number(d.copies) || 2}`).join(', ');
      lines.push(`| ${p.building} | F${p.floor} | ${p.subArea} | ${p.page} | ${p.raw} | ${p.unique} | ${names || '-'} |`);
    }
    lines.push('');
  }
  lines.push('### 风险分布');
  lines.push('');
  lines.push('| 等级 | 数量 |');
  lines.push('|------|------|');
  for (const level of ['高', '中', '低']) {
    lines.push(`| ${level} | ${riskDist[level]} |`);
  }
  lines.push('');
  lines.push('### 异常分布');
  lines.push('');
  lines.push('| 标签 | 数量 |');
  lines.push('|------|------|');
  if (anomalyDist.length === 0) {
    lines.push('| 无 | 0 |');
  } else {
    for (const r of anomalyDist) lines.push(`| ${r.tag} | ${r.count} |`);
  }
  lines.push('');
  if (risks.length > 0) {
    lines.push('### 开机风险 Top');
    lines.push('');
    lines.push('| 风险 | 楼栋 | 楼层 | 设备名 | 类型 | 模式 | 设定温度 | 风速 | 原因 |');
    lines.push('|------|------|------|--------|------|------|----------|------|------|');
    for (const r of risks) {
      lines.push(`| ${r.riskLevel}(${r.riskScore}) | ${r.building}${r.zuo ? ' ' + r.zuo : ''} | F${r.floor} | ${r.name} | ${r.pub ? '公区' : '非公区'} | ${r.mode} | ${r.set_temp}℃ | ${r.fan} | ${r.riskReasons.join(', ')} |`);
    }
    lines.push('');
  }
  if (floors.length > 0) {
    lines.push('### 楼层开机 Top');
    lines.push('');
    lines.push('| 楼栋 | 楼层 | 子区 | 总数 | 开机 | 公区开机 | 离线 |');
    lines.push('|------|------|------|------|------|----------|------|');
    for (const r of floors) {
      lines.push(`| ${r.building}${r.zuo ? ' ' + r.zuo : ''} | F${r.floor} | ${r.subArea} | ${r.total} | ${r.on} | ${r.pubOn} | ${r.offline} |`);
    }
    lines.push('');
  }
  if (zones.length > 0) {
    lines.push('### 座区统计');
    lines.push('');
    lines.push('| 楼栋 | 座区 | 总数 | 开机 | 关机 | 离线 | 公区开机 |');
    lines.push('|------|------|------|------|------|------|----------|');
    for (const r of zones) {
      lines.push(`| ${r.building} | ${r.zuo} | ${r.total} | ${r.on} | ${r.off} | ${r.offline} | ${r.pubOn} |`);
    }
    lines.push('');
  }
  lines.push('### 口径说明');
  lines.push('');
  lines.push('- 开机：`switch=ON`。');
  lines.push('- 未开启：`switch != ON`，其中包含普通关机和离线/异常设备。');
  lines.push('- 离线：`comm=离线` 或开关状态为 `-`。');
  lines.push('- 重复渲染页按唯一设备入库，raw/unique 在备注和数据质量说明中标注。');
  lines.push('- 风险评分：公区开机、低设定温度、高风速、离线异常等因素累加，仅用于处置优先级排序。');
  lines.push('');
}

// ─── Building → Floor → SubTab → Page hierarchy writer (shared by MD/TXT) ───
function writeHierarchy(items, out, opts = {}) {
  const {
    heading: headingFn,
    row: rowFn,
    tableHeader,
    tblHeader,
  } = opts;
  const header = tableHeader || tblHeader;

  const bldgMap = {};
  for (const r of items) {
    const key = r.building + '|' + (r.zuo || '');
    if (!bldgMap[key]) bldgMap[key] = { building: r.building, zuo: r.zuo, items: [] };
    bldgMap[key].items.push(r);
  }
  const bldgKeys = Object.keys(bldgMap).sort((a, b) => {
    const ba = BLDG_ORDER.indexOf(bldgMap[a].building);
    const bb = BLDG_ORDER.indexOf(bldgMap[b].building);
    if (ba !== bb) return ba - bb;
    return naturalCompare(bldgMap[a].zuo || '', bldgMap[b].zuo || '');
  });

  for (const key of bldgKeys) {
    const grp = bldgMap[key];
    const meta = BLDG_META[grp.building];
    let label = grp.building + ' ' + (meta ? meta.full : '');
    if (grp.zuo) label += ' ' + grp.zuo;

    if (headingFn) { const h = headingFn(label, grp); if (h) out.push(h); }

    const floorMap = {};
    for (const r of grp.items) {
      if (!floorMap[r.floor]) floorMap[r.floor] = [];
      floorMap[r.floor].push(r);
    }
    const floors = Object.keys(floorMap).map(Number).sort((a, b) => a - b);

    for (const f of floors) {
      const allItems = floorMap[f];
      const subTabMap = {};
      for (const r of allItems) {
        const pfix = pagePrefix(r.page_name);
        if (!subTabMap[pfix]) subTabMap[pfix] = [];
        subTabMap[pfix].push(r);
      }
      const prefixes = Object.keys(subTabMap).sort(naturalCompare);

      for (const prefix of prefixes) {
        const pItems = subTabMap[prefix];
        let subLabel = '';
        if (prefixes.length > 1) subLabel = prefix || '塔楼';

        const pageMap = {};
        for (const r of pItems) {
          const sn = pageShortName(r.page_name);
          if (!pageMap[sn]) pageMap[sn] = [];
          pageMap[sn].push(r);
        }
        const pageNames = Object.keys(pageMap).sort(pageNameCompare);

        for (const pn of pageNames) {
          const cardList = sortRows(pageMap[pn]);
          const pageLabel = pn === 'default' ? '全部' : pn;
          const dupNote = duplicateNote(cardList[0]?.raw_count, cardList[0]?.unique_count, cardList[0]?.duplicate_names);
          const section = `F${f}` + (subLabel ? ` ${subLabel}` : '') + (pageLabel !== '全部' ? ` — ${pageLabel}` : '') + (dupNote ? `  [${dupNote}]` : '');

          if (rowFn) {
            out.push(`### ${section}`);
            out.push('');
            if (header) out.push(header);
            for (const r of cardList) {
              out.push(rowFn(r, section));
            }
            out.push('');
          } else {
            out.push({ section, items: cardList });
          }
        }
      }
    }
  }
}

// ─── MD: 设备总清单 ───
function genAllMD(data, ts) {
  const lines = [];
  lines.push('# 空调设备总清单');
  lines.push('');
  lines.push(`> 生成时间: ${ts}  |  设备总数: ${data.length} 台`);
  lines.push('');
  appendMdOverview(lines, data, ts, '设备总清单', data.length);
  lines.push('## 目录');
  for (const b of BLDG_ORDER) {
    const bItems = data.filter(r => r.building === b);
    if (bItems.length === 0) continue;
    const meta = BLDG_META[b];
    if (meta.zuoFn) {
      const zuos = [...new Set(bItems.map(r => r.zuo))].sort(naturalCompare);
      for (const z of zuos) {
        const zItems = bItems.filter(r => r.zuo === z);
        const title = `${b} ${meta.full} ${z}`;
        lines.push(`  - [${title}](${mdAnchor(title)}) — ${zItems.length} 台`);
      }
    } else {
      const title = `${b} ${meta.full}`;
      lines.push(`  - [${title}](${mdAnchor(title)}) — ${bItems.length} 台`);
    }
  }
  lines.push('');

  for (const b of BLDG_ORDER) {
    const bItems = data.filter(r => r.building === b);
    if (bItems.length === 0) continue;
    const meta = BLDG_META[b];

    const processGroup = (items) => {
      const floorMap = {};
      for (const r of items) { if (!floorMap[r.floor]) floorMap[r.floor] = []; floorMap[r.floor].push(r); }
      const floors = Object.keys(floorMap).map(Number).sort((a, b) => a - b);

      for (const f of floors) {
        const allItems = floorMap[f];
        const subTabMap = {};
        for (const r of allItems) { const p = pagePrefix(r.page_name); if (!subTabMap[p]) subTabMap[p] = []; subTabMap[p].push(r); }
        const prefixes = Object.keys(subTabMap).sort(naturalCompare);

        for (const prefix of prefixes) {
          const pItems = subTabMap[prefix];
          let subLabel = '';
          if (prefixes.length > 1) subLabel = prefix || '塔楼';
          const floorTitle = `F${f}` + (subLabel ? ` ${subLabel}` : '');
          lines.push(`### ${floorTitle}`);
          lines.push('');

          const pageMap = {};
          for (const r of pItems) { const sn = pageShortName(r.page_name); if (!pageMap[sn]) pageMap[sn] = []; pageMap[sn].push(r); }
          const pageNames = Object.keys(pageMap).sort(pageNameCompare);

          for (const pn of pageNames) {
            const cardList = sortRows(pageMap[pn]);
            const pageLabel = pn === 'default' ? '全部' : pn;
            const dupNote = cardList[0]?.pageDuplicateNote || duplicateNote(cardList[0]?.raw_count, cardList[0]?.unique_count, cardList[0]?.duplicate_names);
            lines.push(`#### ${pageLabel} (${cardList.length} 台${dupNote ? `, ${dupNote}` : ''})`);
            lines.push('');
            lines.push('| 设备名 | 开关 | 模式 | 室内温度 | 设定温度 | 风速 | 通讯 | 类型 | 备注 |');
            lines.push('|--------|------|------|----------|----------|------|------|------|------|');
            for (const r of cardList) {
              const swLabel = switchLabel(r.switch);
              const zPrefix = r.zuo ? r.zuo + ' ' : '';
              const note = r.rowDuplicateNote || '';
              lines.push(`| ${zPrefix}${r.name} | ${swLabel} | ${r.mode} | ${r.indoor}℃ | ${r.set_temp}℃ | ${r.fan} | ${r.comm} | ${r.pub ? '公区' : ''} | ${note} |`);
            }
            lines.push('');
          }
        }
      }
    };

    if (meta.zuoFn) {
      const zuos = [...new Set(bItems.map(r => r.zuo))].sort(naturalCompare);
      for (const z of zuos) {
        const zItems = bItems.filter(r => r.zuo === z);
        const h1 = `${b} ${meta.full} ${z}`;
        lines.push(`## ${h1}`);
        lines.push('');
        lines.push(`> ${zItems.length} 台设备`);
        lines.push('');
        processGroup(zItems);
      }
    } else {
      const h1 = `${b} ${meta.full}`;
      lines.push(`## ${h1}`);
      lines.push('');
      lines.push(`> ${bItems.length} 台设备`);
      lines.push('');
      processGroup(bItems);
    }
  }

  lines.push('---');
  lines.push(`_共 ${data.length} 台设备，由 EMS Scout 自动生成于 ${ts}_`);
  return lines.join('\n');
}

// ─── MD: 未关闭空调 ───
function genOnMD(data, ts) {
  const pubON = data.filter(r => r.switch === 'ON' && r.pub);
  const nonPubON = data.filter(r => r.switch === 'ON' && !r.pub);
  const onAbnormal = data.filter(r => r.switch === 'ON' && r.anomaly.length > 0);

  const lines = [];
  lines.push('# 未关闭空调清单');
  lines.push('');
  const onTotal = pubON.length + nonPubON.length;
  lines.push(`> 生成时间: ${ts}  |  开机总数: ${onTotal} 台  |  开机异常: ${onAbnormal.length} 台`);
  lines.push('');
  appendMdOverview(lines, data, ts, '未关闭空调清单', onTotal);

  const writeSection = (items, heading) => {
    if (items.length === 0) return;
    lines.push(`# ${heading}（${items.length} 台）`);
    lines.push('');

    writeHierarchy(items, lines, {
      heading: (label) => `## ${label}`,
      tblHeader: '| 座号 | 设备名 | 开关 | 模式 | 室内温度 | 设定温度 | 风速 | 通讯 | 异常 | 备注 |\n|------|--------|------|------|----------|----------|------|------|------|------|',
      row: (r, section) => {
        const swLabel = switchLabel(r.switch);
        const badge = renderAnomalyBadge(r.anomaly);
        return `| ${r.zuo || '—'} | ${r.name} | ${swLabel} | ${r.mode} | ${r.indoor}℃ | ${r.set_temp}℃ | ${r.fan} | ${r.comm} | ${badge} | ${r.rowDuplicateNote || ''} |`;
      }
    });
  };

  writeSection(pubON, '公区开机');
  writeSection(nonPubON, '非公区开机');

  lines.push('---');
  lines.push(`_自动生成于 ${ts}_`);
  return lines.join('\n');
}

// ─── MD: 未开启空调 ───
function genOffMD(data, ts) {
  // 未开启 = switch ≠ ON
  const off = data.filter(r => r.switch === 'OFF' && !(r.comm === '离线' || r.anomaly.length > 0));
  const offlineFault = data.filter(r => (r.switch !== 'ON' && (r.comm === '离线' || r.anomaly.length > 0)));
  const offTotal = off.length + offlineFault.length;

  const lines = [];
  lines.push('# 未开启空调清单');
  lines.push('');
  lines.push(`> 生成时间: ${ts}  |  未开启总数: ${offTotal} 台  |  普通关机: ${off.length} 台  |  离线/异常: ${offlineFault.length} 台`);
  lines.push('');
  appendMdOverview(lines, data, ts, '未开启空调清单', offTotal);

  const writeSection = (items, heading) => {
    if (items.length === 0) return;
    lines.push(`# ${heading}（${items.length} 台）`);
    lines.push('');

    writeHierarchy(items, lines, {
      heading: (label) => `## ${label}`,
      tblHeader: '| 座号 | 设备名 | 开关 | 模式 | 室内温度 | 设定温度 | 风速 | 通讯 | 异常 | 备注 |\n|------|--------|------|------|----------|----------|------|------|------|------|',
      row: (r, section) => {
        const swLabel = switchLabel(r.switch);
        const badge = renderAnomalyBadge(r.anomaly);
        return `| ${r.zuo || '—'} | ${r.name} | ${swLabel} | ${r.mode} | ${r.indoor}℃ | ${r.set_temp}℃ | ${r.fan} | ${r.comm} | ${badge} | ${r.rowDuplicateNote || ''} |`;
      }
    });
  };

  writeSection(off, '普通关机设备');
  writeSection(offlineFault, '离线 / 异常设备');

  lines.push('---');
  lines.push(`_自动生成于 ${ts}_`);
  return lines.join('\n');
}

// ─── TXT: 通用 ───
function genTXT(data, collInfo, label) {
  const bldgSet = new Set(data.map(r => r.building));
  const bldgCount = bldgSet.size;
  const bldgList = BLDG_ORDER.filter(b => bldgSet.has(b)).join('、');
  const lines = [];
  lines.push(`${label}`);
  if (collInfo) {
    lines.push(`采集时间: ${collInfo.lastTs}  |  耗时 ${collInfo.durStr}`);
    lines.push(`大楼 ${collInfo.bldgCount} 栋 (${bldgList})`);
  } else {
    lines.push(`大楼 ${bldgCount} 栋 (${bldgList})`);
  }
  lines.push(`设备数: ${data.length} 台`);
  const s = reportSummary(data);
  const risks = topRiskRows(data, 10);
  const floors = s.on > 0 ? topFloorStats(data, 10) : [];
  const zones = zoneStats(data);
  const riskDist = riskDistribution(data);
  const anomalyDist = anomalyDistribution(data);
  lines.push(`状态: 开机 ${s.on} 台 | 关机 ${s.off} 台 | 离线 ${s.offline} 台 | 公区开机 ${s.pubOn} 台 | 重复渲染页 ${s.duplicatePages.length}`);
  lines.push(`风险分布: 高 ${riskDist.高} | 中 ${riskDist.中} | 低 ${riskDist.低}`);
  lines.push('='.repeat(72));
  lines.push('');
  lines.push('楼栋汇总');
  for (const b of s.buildings) {
    const baseline = b.baselineCards ? `${b.total}/${b.baselineCards}` : `${b.total}`;
    lines.push(`  ${b.building} ${b.name}: ${b.total}台 开机${b.on} 关机${b.off} 离线${b.offline} 公区开机${b.pubOn} 基准${baseline}`);
  }
  if (s.duplicatePages.length > 0) {
    lines.push('');
    lines.push('数据质量说明');
    for (const p of s.duplicatePages) {
      const names = p.duplicateNames.map(d => `${d.name}x${Number(d.copies) || 2}`).join(', ');
      lines.push(`  ${p.building} F${p.floor} ${p.subArea} ${p.page}: raw=${p.raw} unique=${p.unique} ${names}`);
    }
  }
  lines.push('');
  lines.push('异常分布');
  if (anomalyDist.length === 0) {
    lines.push('  无 0');
  } else {
    for (const r of anomalyDist) lines.push(`  ${r.tag}: ${r.count}`);
  }
  if (risks.length > 0) {
    lines.push('');
    lines.push('开机风险 Top');
    for (const r of risks) {
      lines.push(`  ${r.riskLevel}(${r.riskScore}) ${r.building}${r.zuo ? ' ' + r.zuo : ''} F${r.floor} ${r.name} ${r.pub ? '公区' : '非公区'} ${r.mode} ${r.set_temp}℃ ${r.fan} - ${r.riskReasons.join(', ')}`);
    }
  }
  if (floors.length > 0) {
    lines.push('');
    lines.push('楼层开机 Top');
    for (const r of floors) {
      lines.push(`  ${r.building}${r.zuo ? ' ' + r.zuo : ''} F${r.floor} ${r.subArea}: 总${r.total} 开机${r.on} 公区开机${r.pubOn} 离线${r.offline}`);
    }
  }
  if (zones.length > 0) {
    lines.push('');
    lines.push('座区统计');
    for (const r of zones) {
      lines.push(`  ${r.building} ${r.zuo}: 总${r.total} 开机${r.on} 关机${r.off} 离线${r.offline} 公区开机${r.pubOn}`);
    }
  }
  lines.push('');
  lines.push('异常标签: 通讯未知、开关未知、缺indicator、通讯离线、开机但离线、模式未知、温度异常。');
  lines.push('口径: 开机=switch=ON；未开启=switch!=ON；离线=comm=离线或switch=-；重复渲染页按唯一设备入库；风险分仅用于处置优先级排序。');
  lines.push('='.repeat(72));
  lines.push('');

  function padVisual(s, w) {
    let vis = 0;
    for (const ch of s) vis += ch.charCodeAt(0) > 127 ? 2 : 1;
    return s + ' '.repeat(Math.max(0, w - vis));
  }

  function maxVw(values) {
    let m = 0;
    for (const v of values) {
      let vis = 0;
      for (const ch of v) vis += ch.charCodeAt(0) > 127 ? 2 : 1;
      if (vis > m) m = vis;
    }
    return m;
  }

  const allFieldRows = data.map(r => ({
    mode: r.mode || '-',
    indoor: r.indoor != null ? r.indoor + '℃' : '-',
    setTemp: r.set_temp != null ? r.set_temp + '℃' : '-',
    fan: r.fan || '-',
  }));
  const mwMode = maxVw(allFieldRows.map(r => r.mode));
  const mwIndoor = maxVw(allFieldRows.map(r => r.indoor));
  const mwSetTemp = maxVw(allFieldRows.map(r => r.setTemp));
  const mwFan = maxVw(allFieldRows.map(r => r.fan));

  function writeGroup(items, groupLabel) {
    if (items.length === 0) return;
    lines.push(groupLabel);
    lines.push('');

    const bldgMap = {};
    for (const r of items) {
      const key = r.building + '|' + (r.zuo || '');
      if (!bldgMap[key]) bldgMap[key] = { building: r.building, zuo: r.zuo, items: [] };
      bldgMap[key].items.push(r);
    }
    const bldgKeys = Object.keys(bldgMap).sort((a, b) => {
      const ba = BLDG_ORDER.indexOf(bldgMap[a].building);
      const bb = BLDG_ORDER.indexOf(bldgMap[b].building);
      if (ba !== bb) return ba - bb;
      return naturalCompare(bldgMap[a].zuo || '', bldgMap[b].zuo || '');
    });

    let bldgIdx = 0;
    for (const key of bldgKeys) {
      bldgIdx++;
      const grp = bldgMap[key];
      const meta = BLDG_META[grp.building];
      let bLabel = grp.building + ' ' + (meta ? meta.full : '');
      if (grp.zuo) bLabel += ' ' + grp.zuo;

      const anomalies = grp.items.filter(r => r.anomaly && r.anomaly.length > 0);
      const offline = grp.items.filter(r => r.comm === '离线');
      const parts = [];
      if (anomalies.length > 0) parts.push(`异常 ${anomalies.length} 台`);
      if (offline.length > 0) parts.push(`离线 ${offline.length} 台`);
      const summary = parts.length > 0 ? '  (' + parts.join('，') + ')' : '';

      lines.push('  ' + '═'.repeat(60));
      lines.push(`  [${bldgIdx}] ${bLabel}${summary}`);
      lines.push('');

      const floorMap = {};
      for (const r of grp.items) {
        if (!floorMap[r.floor]) floorMap[r.floor] = [];
        floorMap[r.floor].push(r);
      }
      const floors = Object.keys(floorMap).map(Number).sort((a, b) => a - b);

      for (const f of floors) {
        lines.push(`  [F${f}]`);
        lines.push('  ' + '─'.repeat(56));

        const allItems = floorMap[f];
        const subTabMap = {};
        for (const r of allItems) {
          const p = pagePrefix(r.page_name);
          if (!subTabMap[p]) subTabMap[p] = [];
          subTabMap[p].push(r);
        }
        const prefixes = Object.keys(subTabMap).sort(naturalCompare);

        for (const prefix of prefixes) {
          const pItems = subTabMap[prefix];
          let subLabel = '';
          if (prefixes.length > 1) subLabel = prefix || '塔楼';

          const pageMap = {};
          for (const r of pItems) {
            const sn = pageShortName(r.page_name);
            if (!pageMap[sn]) pageMap[sn] = [];
            pageMap[sn].push(r);
          }
          const pageNames = Object.keys(pageMap).sort(pageNameCompare);

          for (const pn of pageNames) {
            const cardList = sortRows(pageMap[pn]);
            const pageLabel = pn === 'default' ? '全部' : pn;
            const dupNote = cardList[0]?.pageDuplicateNote || duplicateNote(cardList[0]?.raw_count, cardList[0]?.unique_count, cardList[0]?.duplicate_names);
            const section = `F${f}` + (subLabel ? ` ${subLabel}` : '') + (pageLabel !== '全部' ? ` — ${pageLabel}` : '') + (dupNote ? `  [${dupNote}]` : '');
            lines.push(`    ${section}`);

            for (const r of cardList) {
              const swLabel = switchLabel(r.switch);
              const commTag = r.comm === '离线' ? ' [离线]' : '';
              const anomalyTagStr = r.anomaly && r.anomaly.length > 0 ? ' ⚠' : '';
              const mode = padVisual(r.mode || '-', mwMode);
              const indoor = padVisual(r.indoor != null ? r.indoor + '℃' : '-', mwIndoor);
              const setTemp = padVisual(r.set_temp != null ? r.set_temp + '℃' : '-', mwSetTemp);
              const fan = padVisual(r.fan || '-', mwFan);
              const state = [mode, indoor, setTemp, fan].join('  ');
              const zuoPrefix = r.zuo ? r.zuo + ' ' : '';
              const namePart = (zuoPrefix + r.name).padEnd(22);
              const statusPart = (swLabel + commTag + anomalyTagStr).padEnd(10);
              const notePart = r.rowDuplicateNote ? `  [${r.rowDuplicateNote}]` : '';
              lines.push(`      ${namePart}  ${statusPart}  ${state}${notePart}`);
            }
          }
        }
      }
    }
  }

  writeGroup(data.filter(r => r.pub), '公区');
  writeGroup(data.filter(r => !r.pub), '非公区');

  // ─── 异常/离线明细 ───
  const trouble = data.filter(r => r.comm === '离线' || r.anomaly.length > 0);
  if (trouble.length > 0) {
    const offCount = trouble.filter(r => r.comm === '离线').length;
    const anomCount = trouble.filter(r => r.anomaly.length > 0 && r.comm !== '离线').length;
    const parts = [];
    if (offCount > 0) parts.push(`离线 ${offCount} 台`);
    if (anomCount > 0) parts.push(`异常 ${anomCount} 台`);
    lines.push('');
    lines.push('═'.repeat(72));
    lines.push(`异常 / 离线设备明细  (共 ${trouble.length} 台${parts.length ? ' — ' + parts.join('，') : ''})`);
    lines.push('─'.repeat(72));

    const rows = trouble.map(r => ({
      bldg: r.building,
      zuo: r.zuo || '—',
      floor: 'F' + r.floor,
      name: r.name,
      sw: switchLabel(r.switch),
      tags: r.anomaly.length > 0 ? r.anomaly.join(', ') : '—',
    })).sort((a, b) => bldgCompare(a.bldg, b.bldg)
      || naturalCompare(a.zuo, b.zuo)
      || naturalCompare(a.floor, b.floor)
      || naturalCompare(a.name, b.name));

    const mwBldg = maxVw(rows.map(r => r.bldg));
    const mwZuo = maxVw(rows.map(r => r.zuo));
    const mwFloor = maxVw(rows.map(r => r.floor));
    const mwName = maxVw(rows.map(r => r.name));

    for (const r of rows) {
      const b = padVisual(r.bldg, mwBldg);
      const z = padVisual(r.zuo, mwZuo);
      const f = padVisual(r.floor, mwFloor);
      const n = padVisual(r.name, mwName);
      lines.push(` ${b}  ${z}  ${f}  ${n}  ${r.sw.padEnd(4)}  ${r.tags}`);
    }
  }

  lines.push('');
  lines.push('='.repeat(72) + '  END');
  return lines.join('\n');
}

// ─── XLSX: 设备总清单 ───
function genAllXLSX(data, ts, outDirPath) {
  const wb = XLSX.utils.book_new();
  const outPath = path.join(outDirPath || ROOT, `设备总清单_${ts}.xlsx`);

  function buildSheetData(items) {
    const floorMap = {};
    for (const r of items) { if (!floorMap[r.floor]) floorMap[r.floor] = []; floorMap[r.floor].push(r); }
    const floors = Object.keys(floorMap).map(Number).sort((a, b) => a - b);
    const aoa = [];
    const merges = [];
    aoa.push(['楼层', '子标签', '页数', '座号', '设备名', '开关', '模式', '室内温度', '设定温度', '风速', '通讯', '区域类型', '备注']);

    for (const f of floors) {
      const allItems = floorMap[f];
      const subTabMap = {};
      for (const r of allItems) { const p = pagePrefix(r.page_name); if (!subTabMap[p]) subTabMap[p] = []; subTabMap[p].push(r); }
      const prefixes = Object.keys(subTabMap).sort(naturalCompare);

      for (const prefix of prefixes) {
        const pItems = subTabMap[prefix];
        let subLabel = '';
        if (prefixes.length > 1) subLabel = prefix || '塔楼';

        const pageMap = {};
        for (const r of pItems) { const sn = pageShortName(r.page_name); if (!pageMap[sn]) pageMap[sn] = []; pageMap[sn].push(r); }
          const pageNames = Object.keys(pageMap).sort(pageNameCompare);

          for (const pn of pageNames) {
            const cardList = sortRows(pageMap[pn]);
          const pageLabel = pn === 'default' ? '全部' : pn;
          const dupNote = cardList[0]?.pageDuplicateNote || duplicateNote(cardList[0]?.raw_count, cardList[0]?.unique_count, cardList[0]?.duplicate_names);
          const sectionTitle = `F${f}` + (subLabel ? ` ${subLabel}` : '') + (pageLabel !== '全部' ? ` - ${pageLabel}` : '') + (dupNote ? `  [${dupNote}]` : '');
          const headerRow = aoa.length;
          aoa.push([sectionTitle, '', '', '', '', '', '', '', '', '', '', '', '']);
          merges.push({ s: { r: headerRow, c: 0 }, e: { r: headerRow, c: 12 } });

          for (const r of cardList) {
            const swLabel = switchLabel(r.switch);
            aoa.push(['', '', '', r.zuo, r.name, swLabel, r.mode, r.indoor, r.set_temp, r.fan, r.comm, r.pub ? '公区' : '', r.rowDuplicateNote || '']);
          }
        }
      }
    }
    return { aoa, merges };
  }

  for (const b of BLDG_ORDER) {
    const bItems = data.filter(r => r.building === b);
    if (bItems.length === 0) continue;
    const meta = BLDG_META[b];

    if (meta.zuoFn) {
      const zuos = [...new Set(bItems.map(r => r.zuo))].sort(naturalCompare);
      for (const z of zuos) {
        const zItems = bItems.filter(r => r.zuo === z);
        const sName = (b + ' ' + z).length > 31 ? (b + ' ' + z).slice(0, 31) : b + ' ' + z;
        const { aoa, merges } = buildSheetData(zItems);
        const ws = XLSX.utils.aoa_to_sheet(aoa);
        ws['!merges'] = merges;
        ws['!cols'] = [{ wch: 20 }, { wch: 7 }, { wch: 7 }, { wch: 7 }, { wch: 24 }, { wch: 7 }, { wch: 12 }, { wch: 10 }, { wch: 10 }, { wch: 7 }, { wch: 7 }, { wch: 7 }, { wch: 28 }];
        applySheetView(ws, aoa);
        XLSX.utils.book_append_sheet(wb, ws, sName);
      }
    } else {
      const sName = b.length > 31 ? b.slice(0, 31) : b;
      const { aoa, merges } = buildSheetData(bItems);
      const ws = XLSX.utils.aoa_to_sheet(aoa);
      ws['!merges'] = merges;
      ws['!cols'] = [{ wch: 20 }, { wch: 7 }, { wch: 7 }, { wch: 7 }, { wch: 24 }, { wch: 7 }, { wch: 12 }, { wch: 10 }, { wch: 10 }, { wch: 7 }, { wch: 7 }, { wch: 7 }, { wch: 28 }];
      applySheetView(ws, aoa);
      XLSX.utils.book_append_sheet(wb, ws, sName);
    }
  }

  const summary = [];
  summary.push(['楼栋', '座号', '设备总数', '开机', '关机', '离线', '公区', '非公区']);
  let grandTotal = 0, grandON = 0, grandOFF = 0, grandOffline = 0, grandPub = 0, grandNonPub = 0;
  for (const b of BLDG_ORDER) {
    const bItems = data.filter(r => r.building === b);
    if (bItems.length === 0) continue;
    const meta = BLDG_META[b];
    if (meta.zuoFn) {
      const zuos = [...new Set(bItems.map(r => r.zuo))].sort(naturalCompare);
      for (const z of zuos) {
        const zItems = bItems.filter(r => r.zuo === z);
        const on = zItems.filter(r => r.switch === 'ON').length;
        const off = zItems.filter(r => r.switch === 'OFF').length;
        const offline = zItems.filter(r => r.switch === '-' || r.comm === '离线').length;
        const pub = zItems.filter(r => r.pub).length;
        const nonPub = zItems.length - pub;
        summary.push([b, z, zItems.length, on, off, offline, pub, nonPub]);
        grandTotal += zItems.length; grandON += on; grandOFF += off; grandOffline += offline;
        grandPub += pub; grandNonPub += nonPub;
      }
    } else {
      const on = bItems.filter(r => r.switch === 'ON').length;
      const off = bItems.filter(r => r.switch === 'OFF').length;
      const offline = bItems.filter(r => r.switch === '-' || r.comm === '离线').length;
      const pub = bItems.filter(r => r.pub).length;
      const nonPub = bItems.length - pub;
      summary.push([b, '', bItems.length, on, off, offline, pub, nonPub]);
      grandTotal += bItems.length; grandON += on; grandOFF += off; grandOffline += offline;
      grandPub += pub; grandNonPub += nonPub;
    }
  }
  summary.push(['合计', '', grandTotal, grandON, grandOFF, grandOffline, grandPub, grandNonPub]);

  const sumWS = XLSX.utils.aoa_to_sheet(summary);
  sumWS['!cols'] = [{ wch: 8 }, { wch: 7 }, { wch: 10 }, { wch: 8 }, { wch: 8 }, { wch: 8 }, { wch: 8 }, { wch: 8 }];
  applySheetView(sumWS, summary);
  XLSX.utils.book_append_sheet(wb, sumWS, '汇总');
  addSummarySheet(wb, data, ts, '报表说明');
  addDetailSheets(wb, data);

  XLSX.writeFile(wb, outPath);
  console.log('Saved:', outPath);
  return outPath;
}

// ─── XLSX: 未关闭空调 ───
function genOnXLSX(data, ts, outDirPath) {
  const onItems = data.filter(r => r.switch === 'ON');
  if (onItems.length === 0) return null;
  return genStatusXLSX(onItems, ts, "未关闭空调清单", outDirPath);
}

// ─── XLSX: 未开启空调 ───
function genOffXLSX(data, ts, outDirPath) {
  const offItems = data.filter(r => r.switch !== 'ON');
  if (offItems.length === 0) return null;
  return genStatusXLSX(offItems, ts, "未开启空调清单", outDirPath);
}

// ─── XLSX: 通用状态报表（用于 未关闭/未开启） ───
function genStatusXLSX(items, ts, label, outDirPath) {
  const wb = XLSX.utils.book_new();
  const outPath = path.join(outDirPath || ROOT, `${label}_${ts}.xlsx`);
  const isOnReport = label.includes('未关闭');

  const enriched = items.map(r => {
    const areaType = classifyAreaType(r.name, r.layout);
    return { ...r, area_type: areaType };
  });

  const summary = {};
  for (const r of enriched) {
    if (!summary[r.building]) summary[r.building] = { 公区: 0, 非公区: 0 };
    summary[r.building][r.area_type]++;
  }

  const sumAOA = [['楼栋', '楼栋全称', '公区 (台)', '非公区 (台)', '合计 (台)']];
  let totalG = 0, totalF = 0;
  for (const [b, meta] of Object.entries(BLDG_META)) {
    const g = summary[b] ? summary[b].公区 : 0;
    const f = summary[b] ? summary[b].非公区 : 0;
    sumAOA.push([b, meta.full, g, f, g + f]);
    totalG += g; totalF += f;
  }
  sumAOA.push(['合计', '', totalG, totalF, totalG + totalF]);
  const sumWS = XLSX.utils.aoa_to_sheet(sumAOA);
  sumWS['!cols'] = [{ wch: 8 }, { wch: 22 }, { wch: 12 }, { wch: 12 }, { wch: 12 }];
  applySheetView(sumWS, sumAOA);
  XLSX.utils.book_append_sheet(wb, sumWS, '汇总');
  addSummarySheet(wb, enriched, ts, '报表说明');
  addDetailSheets(wb, enriched);

  for (const bldg of BLDG_ORDER) {
    const meta = BLDG_META[bldg];
    const hasZuo = !!meta.zuoFn;
    const bItems = enriched.filter(r => r.building === bldg);
    const gq = bItems.filter(r => r.area_type === '公区');
    const fg = bItems.filter(r => r.area_type === '非公区');
    const sortFn = isOnReport ? riskCompare : rowCompare;
    gq.sort(sortFn); fg.sort(sortFn);

    function rowsToAOA(itemsList, headerZuos) {
      const hdr = headerZuos
        ? ['楼栋', '座号', '子区', '楼层', '子页', '设备名', '区域类型', '通讯状态', '模式', '室内温度', '设定温度', '风速', '备注', '风险等级', '风险分', '风险原因']
        : ['楼栋', '子区', '楼层', '子页', '设备名', '区域类型', '通讯状态', '模式', '室内温度', '设定温度', '风速', '备注', '风险等级', '风险分', '风险原因'];
      const aoa = [hdr];
      for (const r of itemsList) {
        const comm = r.comm;
        const riskCols = [r.riskLevel || '', r.riskScore || 0, (r.riskReasons || []).join(', ')];
        if (headerZuos) {
          aoa.push([r.building, r.zuo, r.sa_text, r.floor, r.page_name, r.name, r.area_type, comm, r.mode, r.indoor, r.set_temp, r.fan, r.rowDuplicateNote || '', ...riskCols]);
        } else {
          aoa.push([r.building, r.sa_text, r.floor, r.page_name, r.name, r.area_type, comm, r.mode, r.indoor, r.set_temp, r.fan, r.rowDuplicateNote || '', ...riskCols]);
        }
      }
      return aoa;
    }

    const aoa = rowsToAOA(gq, hasZuo);
    if (gq.length > 0 && fg.length > 0) {
      const sep = hasZuo
        ? ['─'.repeat(8), '─'.repeat(4), '─'.repeat(4), '─'.repeat(4), '─'.repeat(6), '── 非公区空调 ──', '', '', '', '', '', '', '', '', '', '']
        : ['─'.repeat(8), '─'.repeat(4), '─'.repeat(4), '─'.repeat(6), '── 非公区空调 ──', '', '', '', '', '', '', '', '', '', ''];
      aoa.push(sep);
    }
    for (const r of fg) {
      const comm = r.comm;
      const riskCols = [r.riskLevel || '', r.riskScore || 0, (r.riskReasons || []).join(', ')];
      if (hasZuo) {
        aoa.push([r.building, r.zuo, r.sa_text, r.floor, r.page_name, r.name, r.area_type, comm, r.mode, r.indoor, r.set_temp, r.fan, r.rowDuplicateNote || '', ...riskCols]);
      } else {
        aoa.push([r.building, r.sa_text, r.floor, r.page_name, r.name, r.area_type, comm, r.mode, r.indoor, r.set_temp, r.fan, r.rowDuplicateNote || '', ...riskCols]);
      }
    }

    const ws = XLSX.utils.aoa_to_sheet(aoa);
    ws['!cols'] = hasZuo
      ? [{ wch: 8 }, { wch: 8 }, { wch: 8 }, { wch: 8 }, { wch: 12 }, { wch: 24 }, { wch: 10 }, { wch: 10 }, { wch: 14 }, { wch: 10 }, { wch: 10 }, { wch: 8 }, { wch: 28 }, { wch: 10 }, { wch: 8 }, { wch: 36 }]
      : [{ wch: 8 }, { wch: 8 }, { wch: 8 }, { wch: 12 }, { wch: 24 }, { wch: 10 }, { wch: 10 }, { wch: 14 }, { wch: 10 }, { wch: 10 }, { wch: 8 }, { wch: 28 }, { wch: 10 }, { wch: 8 }, { wch: 36 }];
    applySheetView(ws, aoa);
    XLSX.utils.book_append_sheet(wb, ws, bldg.length > 31 ? bldg.slice(0, 31) : bldg);
  }

  XLSX.writeFile(wb, outPath);
  console.log('Saved:', outPath);
  return outPath;
}

// ─── Main dispatcher ───
const REPORT_NAMES = {
  all: { name: '设备总清单', label: '全部设备' },
  on: { name: '未关闭空调清单', label: '未关闭空调' },
  off: { name: '未开启空调清单', label: '未开启空调' },
};

function generateReports({ types = ['all', 'on', 'off'], formats = ['md'], outDir, runId }) {
  requireLegacyReportsEnabled();
  if (!fs.existsSync(DB_PATH)) {
    console.error('Database not found at', DB_PATH);
    return [];
  }
  const genTs = localDateTime();
  const data = loadData(runId);
  if (!data || data.length === 0) {
    console.error('No data in database');
    return [];
  }

  const outDirPath = outDir || ROOT;
  if (!fs.existsSync(outDirPath)) fs.mkdirSync(outDirPath, { recursive: true });

  const resolvedRunId = runId && runId !== 'latest' && runId !== 'current' ? Number(runId) : null;
  const collInfo = loadCollectInfo(Number.isFinite(resolvedRunId) ? resolvedRunId : null);
  const formatTs = (iso) => {
    const d = new Date(iso);
    const pad = n => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}_${pad(d.getHours())}${pad(d.getMinutes())}`;
  };
  const finalTs = collInfo ? formatTs(collInfo.lastIso) : genTs;
  const reportTs = finalTs;

  const generated = [];

  for (const t of types) {
    const meta = REPORT_NAMES[t];
    if (!meta) { console.error('Unknown report type:', t); continue; }

    for (const f of formats) {
      let content;
      let ext;
      let writeFn;

      if (f === 'md') {
        ext = '.md';
        if (t === 'all') content = genAllMD(data, reportTs);
        else if (t === 'on') content = genOnMD(data, reportTs);
        else if (t === 'off') content = genOffMD(data, reportTs);
        writeFn = (p) => { fs.writeFileSync(p, content, 'utf8'); console.log('Saved:', p); return p; };
      } else if (f === 'txt') {
        ext = '.txt';
        const ci = collInfo;
        if (t === 'all') content = genTXT(data.filter(() => true), ci, '空调设备总清单');
        else if (t === 'on') content = genTXT(data.filter(r => r.switch === 'ON'), ci, '未关闭空调清单');
        else if (t === 'off') content = genTXT(data.filter(r => r.switch !== 'ON'), ci, '未开启空调清单');
        writeFn = (p) => { fs.writeFileSync(p, content, 'utf8'); console.log('Saved:', p); return p; };
      } else if (f === 'xlsx') {
        ext = '.xlsx';
        if (t === 'all') content = genAllXLSX(data, finalTs, outDirPath);
        else if (t === 'on') content = genOnXLSX(data, finalTs, outDirPath);
        else if (t === 'off') content = genOffXLSX(data, finalTs, outDirPath);
        if (content) generated.push(content);
        continue;
      } else {
        continue;
      }

      const filePath = path.join(outDirPath, `${meta.name}_${finalTs}${ext}`);
      writeFn(filePath);
      generated.push(filePath);
    }
  }

  return generated;
}

module.exports = { generateReports };

function printHelp() {
  console.log(`EMS legacy report generator

This script is not part of the current product workflow.
Current export path: native app 数据管理 -> 导出当前筛选 Excel.
Emergency legacy use requires ${LEGACY_ENABLE_ENV}=1.

Usage:
  node scripts/report.js [options]

Options:
  --type=all|on|off       Report type. Can be repeated. Default: all,on,off
  --format=xlsx|txt|md    Output format. Can be repeated. Default: md
  --out=DIR               Output directory. Default: project root
  --run-id=ID             Generate from a historical imported run
  --help                  Show this help
`);
}

if (require.main === module) {
  const args = process.argv.slice(2);
  if (args.includes('--help') || args.includes('-h')) {
    printHelp();
    process.exit(0);
  }
  if (!legacyReportsEnabled()) {
    console.error(
      `Legacy report generation is disabled. Use 数据管理 -> 导出当前筛选 Excel.\n` +
      `Emergency legacy use: set ${LEGACY_ENABLE_ENV}=1 and rerun this command.`
    );
    process.exit(2);
  }
  const types = [];
  const formats = [];
  let outDir;
  let runId;

  for (const a of args) {
    if (a.startsWith('--type=')) types.push(a.split('=')[1]);
    else if (a.startsWith('--format=')) formats.push(a.split('=')[1]);
    else if (a.startsWith('--out=')) outDir = a.split('=')[1];
    else if (a.startsWith('--run-id=')) runId = a.split('=')[1];
    else {
      console.error('Unknown option:', a);
      printHelp();
      process.exit(2);
    }
  }

  const result = generateReports({
    types: types.length > 0 ? types : ['all', 'on', 'off'],
    formats: formats.length > 0 ? formats : ['md'],
    outDir,
    runId
  });
  console.log('Generated', result.length, 'files');
}
