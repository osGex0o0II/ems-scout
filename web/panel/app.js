'use strict';

const BUILDINGS = ['1号', '2号', '3号', '4号', '5号', '6号'];
const BASELINE_CARDS = {
  '1号': 1493,
  '2号': 110,
  '3号': 1106,
  '4号': 1096,
  '5号': 288,
  '6号': 2482,
};
const COMMON_TAGS = ['重点关注', '已核实', '待复采', '疑似错误'];
const ZUO_OPTIONS = {
  '5号': ['A座', 'B座', 'C座', 'D座', 'E座', 'F座'],
  '6号': ['A座', 'B座', 'C座'],
};
const TASK_KIND_META = {
  collectImport: { title: '历史兼容采集', badge: '维护任务', action: '开始历史采集', plan: ['采集', '采集结果校验', '导入数据库', '旧质量审计'] },
  collectSafe: { title: '历史安全采集', badge: '维护任务', action: '开始历史采集', plan: ['采集', '采集结果校验'] },
  realtimeDetails: { title: '详情采集', badge: '详情采集', action: '开始采集', plan: ['详情批量采集', '质量审计'] },
  enumerate: { title: '仅采集到 JSON', badge: '维护任务', action: '开始采集', plan: ['采集'] },
  validate: { title: '仅校验采集结果', badge: '维护任务', action: '开始校验', plan: ['采集结果校验'] },
  import: { title: '仅导入数据库', badge: '维护任务', action: '开始导入', plan: ['导入数据库'] },
  quality: { title: '仅质量审计', badge: '维护任务', action: '开始审计', plan: ['质量审计'] },
};
const titles = {
  overview: '总览',
  tasks: '采集任务',
  monitor: '区域管理',
  data: '数据管理',
  quality: '质量审计',
  reports: '说明 / 关于',
};

const state = {
  view: 'overview',
  summary: null,
  groups: { groups: [], items: [], stats: [] },
  editingGroupId: null,
  cards: { total: 0, rows: [] },
  runs: [],
  latestCollection: null,
  floors: { catalog: [], discovered: [] },
  areaOptions: { sub_areas: [], devices: [] },
  filterOptions: { modes: [], fans: [], zuos: [] },
  realtimeSummary: { file_count: 0, total_rows: 0, files: [] },
  taskPreflight: null,
  realtimeCoverage: { db_rows: 0, realtime_rows: 0, matched: 0, db_missing_realtime: 0, realtime_unmatched: 0, needs_review: 0, delta: 0 },
  runId: '',
  runAutoSelected: false,
  manualRunSelection: false,
  activeRunWarning: '',
  taskTimer: null,
  taskPreflightLoading: false,
  taskPreflightRequestSeq: 0,
  taskBuildings: [...BUILDINGS],
  currentTask: null,
  selectedAreaIds: [],
  dataFilters: {
    buildings: [],
    zuos: [],
    floors: [],
  },
  cardPageOffset: 0,
  cardPageSize: 500,
  runPickerOpen: false,
  cardRequestSeq: 0,
  quickFilter: 'all',
  shortcutCountCache: {},
};

function $(id) {
  return document.getElementById(id);
}

function esc(value) {
  if (window.UI && UI.escapeHtml) return UI.escapeHtml(value);
  return String(value ?? '').replace(/[&<>"']/g, ch => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;',
  }[ch]));
}

function uiBadge(text, type = 'muted', extraClass = '') {
  return window.UI && UI.badge
    ? UI.badge(text, type, extraClass)
    : `<span class="badge ${esc(type)} ${esc(extraClass)}">${esc(text || '--')}</span>`;
}

function uiMetricCard(options) {
  return window.UI && UI.metricCard
    ? UI.metricCard(options)
    : `<div class="kpi ${esc(options.type || '')}"><span>${esc(options.label)}</span><strong>${esc(options.value ?? '--')}</strong></div>`;
}

function semanticStatusType(value) {
  return window.UI && UI.statusType ? UI.statusType(value) : 'muted';
}

function semanticPowerType(value) {
  return window.UI && UI.powerType ? UI.powerType(value) : semanticStatusType(value);
}

function semanticQualityType(value) {
  return window.UI && UI.qualityType ? UI.qualityType(value) : 'muted';
}

function textOrDash(value) {
  const raw = String(value ?? '').trim();
  return raw || '-';
}

async function api(path, options = {}) {
  const init = { ...options };
  if (init.body && typeof init.body !== 'string') {
    init.headers = { 'content-type': 'application/json', ...(init.headers || {}) };
    init.body = JSON.stringify(init.body);
  }
  const res = await fetch(path, init);
  const data = await res.json();
  if (!res.ok || data.ok === false) throw new Error(data.error || res.statusText);
  return data.data !== undefined ? data.data : data;
}

function fmtDate(value) {
  if (!value) return '-';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return `${d.getMonth() + 1}-${String(d.getDate()).padStart(2, '0')} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

function fmtDateTime(value) {
  if (!value) return '-';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function fmtDurationMs(value) {
  const ms = Number(value || 0);
  if (!Number.isFinite(ms) || ms <= 0) return '-';
  const sec = Math.round(ms / 1000);
  if (sec >= 3600) return `${Math.floor(sec / 3600)}时${String(Math.floor((sec % 3600) / 60)).padStart(2, '0')}分`;
  if (sec >= 60) return `${Math.floor(sec / 60)}分${String(sec % 60).padStart(2, '0')}秒`;
  return `${sec}秒`;
}

function latestCollectionLabel() {
  const meta = state.latestCollection || {};
  const fallbackRun = (state.runs || [])[0];
  const completedAt = meta.completed_at || (fallbackRun && fallbackRun.completed_at);
  if (!completedAt) return '暂无采集时间';
  const hasMetaCount = meta.card_count !== null && meta.card_count !== undefined;
  const count = hasMetaCount && Number.isFinite(Number(meta.card_count))
    ? Number(meta.card_count)
    : (fallbackRun ? Number(fallbackRun.card_count || 0) : null);
  const countText = Number.isFinite(count) ? ` (${count}张)` : '';
  const pending = meta.pending_import ? ' · 待导入' : '';
  return `最新采集：${fmtDateTime(completedAt)}${countText}${pending}`;
}

function currentDataCount() {
  const candidates = [
    state.summary && state.summary.total,
    state.realtimeCoverage && state.realtimeCoverage.realtime_rows,
    state.cards && state.cards.total,
  ];
  const value = candidates.find(v => Number.isFinite(Number(v)) && Number(v) > 0);
  return Number.isFinite(Number(value)) ? Number(value) : null;
}

function currentDbCount() {
  const candidates = [
    state.summary && state.summary.db_total,
    state.realtimeCoverage && state.realtimeCoverage.db_rows,
  ];
  const value = candidates.find(v => Number.isFinite(Number(v)) && Number(v) > 0);
  return Number.isFinite(Number(value)) ? Number(value) : null;
}

function currentDetailCount() {
  const candidates = [
    state.summary && state.summary.detail_total,
    state.realtimeCoverage && state.realtimeCoverage.realtime_rows,
  ];
  const value = candidates.find(v => Number.isFinite(Number(v)) && Number(v) > 0);
  return Number.isFinite(Number(value)) ? Number(value) : currentDataCount();
}

function currentDataMetaText() {
  const total = currentDataCount();
  const latest = state.realtimeSummary && state.realtimeSummary.latest_mtime
    ? fmtDateTime(state.realtimeSummary.latest_mtime)
    : '';
  if (total) return latest ? `${total} 条 · ${latest}` : `${total} 条 · 最新实时详情`;
  return latestCollectionLabel().replace(/^最新采集：/, '最新采集 ');
}

function currentDataDeltaText() {
  const review = Number(state.realtimeCoverage && state.realtimeCoverage.needs_review || 0);
  return review > 0 ? `需排查 ${review}` : '';
}

function parseRunBuildings(run) {
  if (!run) return [];
  if (Array.isArray(run.buildings)) return run.buildings;
  try {
    const parsed = JSON.parse(run.buildings || '[]');
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function expectedCardCount(buildings) {
  const selected = buildings && buildings.length ? buildings : BUILDINGS;
  return selected.reduce((sum, b) => sum + Number(BASELINE_CARDS[b] || 0), 0);
}

function numericInputValue(id, fallback = 0) {
  const el = $(id);
  if (!el) return fallback;
  const value = Number(el.value);
  return Number.isFinite(value) ? value : fallback;
}

function runCardDeltaRatio(run) {
  const expected = expectedCardCount(parseRunBuildings(run));
  const actual = Number(run && run.card_count) || 0;
  if (!expected || !actual) return 0;
  return Math.abs(actual - expected) / expected;
}

function isRunProbablyHealthy(run) {
  if (!run) return false;
  if (Number(run.is_anomaly || 0)) return false;
  if (Number(run.suggested_anomaly || 0)) return false;
  if ((run.scope || 'full') === 'full' && parseRunBuildings(run).length >= BUILDINGS.length) {
    return runCardDeltaRatio(run) <= 0.02;
  }
  return true;
}

function latestRunIsProbablyBad() {
  const latest = (state.runs || [])[0];
  if (!latest) return false;
  if (Number(latest.is_anomaly || 0) || Number(latest.suggested_anomaly || 0)) return true;
  const metaCount = Number(state.latestCollection && state.latestCollection.card_count);
  const latestCount = Number(latest.card_count || 0);
  if (Number.isFinite(metaCount) && metaCount > 0 && Math.abs(metaCount - latestCount) > 0) return false;
  return !isRunProbablyHealthy(latest);
}

function recommendedOverviewRun() {
  return (state.runs || []).find(r => isRunProbablyHealthy(r)) || null;
}

function activeRunLabel() {
  const run = (state.runs || []).find(r => String(r.id) === String(state.runId));
  return state.runId && run ? run.label : `当前数据 · ${currentDataMetaText()}`;
}

function runStatus(run) {
  if (Number(run && run.is_anomaly || 0)) return { text: '已隔离', cls: 'danger' };
  if (Number(run && run.suggested_anomaly || 0)) return { text: '疑似异常', cls: 'warning' };
  return { text: '正常', cls: 'success' };
}

function runQualityLabel(run) {
  const issues = run && run.quality_issue_count;
  if (issues === null || issues === undefined) return { text: '未审计', cls: 'muted' };
  if (Number(issues) > 0) return { text: `问题 ${issues}`, cls: 'warning' };
  return { text: 'OK', cls: 'success' };
}

function runOptionLabel(run) {
  const status = runStatus(run);
  const quality = runQualityLabel(run);
  const flags = [];
  if (status.cls !== 'success') flags.push(status.text);
  if (quality.cls === 'warning') flags.push(quality.text);
  return `${run.label || run.run_key}${flags.length ? ' · ' + flags.join(' · ') : ''}`;
}

function currentDataRun() {
  return (state.runs || []).find(r => !Number(r.is_anomaly || 0) && !Number(r.suggested_anomaly || 0)) || null;
}

function currentRunText() {
  const run = state.runId
    ? (state.runs || []).find(r => String(r.id) === String(state.runId))
    : null;
  if (!run) {
    const delta = currentDataDeltaText();
    return { label: '当前数据', meta: delta ? `${currentDataMetaText()} · ${delta}` : currentDataMetaText() };
  }
  const status = runStatus(run);
  const quality = runQualityLabel(run);
  const parts = [run.card_count ? `${run.card_count}张` : '', quality.text !== '未审计' ? quality.text : '', status.cls !== 'success' ? status.text : ''].filter(Boolean);
  return {
    label: state.runId ? '历史批次' : '当前数据',
    meta: `${run.label || run.run_key}${parts.length ? ' · ' + parts.join(' · ') : ''}`,
  };
}

function bytes(n) {
  const v = Number(n) || 0;
  if (v > 1024 * 1024) return (v / 1024 / 1024).toFixed(1) + ' MB';
  if (v > 1024) return (v / 1024).toFixed(1) + ' KB';
  return v + ' B';
}

function badgeClass(severity) {
  const s = String(severity || '').toUpperCase();
  if (s === 'P1') return 'danger';
  if (s === 'P2' || s === 'P3') return 'warning';
  if (s === 'OK') return 'success';
  if (s === 'INFO') return 'info';
  return 'muted';
}

function qualityIssueTitle(code) {
  const map = {
    realtime_collection_summaryFailed: '实时采集失败',
    realtime_collection_summaryDefaultLike: '默认值疑似',
    realtime_collection_rowCountMismatch: '行数不一致',
    realtime_collection_missingMetadata: '缺少设备标识',
    realtime_collection_duplicateDevId: '重复设备ID',
    realtime_collection_rowError: '设备详情错误',
    realtime_collection_defaultLike: '默认模板疑似',
    realtime_collection_fieldCount: '字段数量异常',
    realtime_collection_realtimeTagCount: '点位数量异常',
    realtime_collection_missingRequiredField: '缺少必需字段',
    realtime_device_invalidRealtimeTags: '有效点位异常',
    realtime_device_partialRealtimeTags: '点位不完整',
    realtime_device_invalidEnum: '枚举字段异常',
    realtime_device_invalidLock: '集控字段异常',
    realtime_device_outOfRange: '数值越界',
    placeholder_names: '占位符卡名',
    state_mismatch: '同步状态冲突',
    baseline_delta: '基线差异',
    unknown_comm: '未知通讯',
    missing_indicator: '缺 indicator',
    unknown_switch: '未知开关',
    duplicate_cards_same_page: '同页重复卡',
    empty_sub_areas: '空子区',
    suspicious_uniform_pages: '疑似默认页',
    duplicate_rendered_pages: '重复渲染页',
    uniform_resolved_pages: '统一完整页',
    inline_sub_area: 'Inline 子区',
    xlsx_advisory: '文件可信性提示',
  };
  return map[code] || code;
}

function qualityAdvice(code, severity) {
  const map = {
    realtime_collection_summaryFailed: '先复查采集日志，必要时重新采集对应楼栋。',
    realtime_collection_summaryDefaultLike: '建议复采对应楼栋，确认不是详情页模板默认值。',
    realtime_collection_rowCountMismatch: '核对批量采集 summary 与明细文件是否同一批次。',
    realtime_collection_missingMetadata: '优先复采缺少设备名或 devId 的设备。',
    realtime_collection_duplicateDevId: '核对是否重复打开详情或页面返回重复设备。',
    realtime_collection_rowError: '打开样例设备详情，确认弹窗是否正常加载。',
    realtime_collection_defaultLike: '复采样例设备，确认字段不是默认模板值。',
    realtime_collection_fieldCount: '检查实时详情弹窗字段是否加载完整。',
    realtime_collection_realtimeTagCount: '检查点位标签采集是否完整，必要时降低批量并重采。',
    realtime_collection_missingRequiredField: '复采对应设备，确认必需字段是否能在页面显示。',
    realtime_device_invalidRealtimeTags: '作为排查项保留；优先看同楼层是否集中出现。',
    realtime_device_partialRealtimeTags: '点位不完整时建议复采对应楼栋或设备。',
    realtime_device_invalidEnum: '核对 EMS 页面是否返回异常枚举值，必要时现场确认。',
    realtime_device_invalidLock: '集控锁定不是异常；仅当字段值超出开启/关闭时复核。',
    realtime_device_outOfRange: '复核温度/设定上下限等字段是否真实越界或详情未加载。',
    placeholder_names: '阻止导入，重新采集对应楼栋或楼层。',
    state_mismatch: '优先确认是否为同一采集时间口径；非同步数据只作趋势参考。',
    baseline_delta: '先不要导出；核对批次是否串页，必要时隔离批次并恢复最近正常批次。',
    unknown_comm: '建议单点复采或现场核对设备通讯状态。',
    missing_indicator: '检查 EMS 页面图片是否加载完整；若集中出现，建议复采对应楼层。',
    unknown_switch: '复采对应设备，确认开关图是否加载完成。',
    duplicate_cards_same_page: '检查去重后明细；若影响数量，复采该页面。',
    empty_sub_areas: '核对该子区是否真实无设备，或是否页面未加载完成。',
    suspicious_uniform_pages: '建议复采该页，确认不是模板默认值被误入库。',
    duplicate_rendered_pages: '已按卡名去重，保留观察；导出前按当前筛选口径复核。',
    uniform_resolved_pages: '多为全离线或模板式真实页，状态完整时作为信息项保留。',
    inline_sub_area: '已知采集结构占位，通常无需处理。',
    xlsx_advisory: '避免读取来源不明的 xlsx 文件。',
  };
  if (map[code]) return map[code];
  return String(severity || '').toUpperCase() === 'P1' ? '先阻断导入并复采。' : '建议复核样例后决定是否复采。';
}

function qualityConclusion(report) {
  if (report && report.kind === 'realtime') {
    const collectionErrors = Number(report.summary && report.summary.collection_errors || 0);
    const anomalyRows = Number(report.summary && report.summary.device_anomaly_rows || 0);
    if (collectionErrors > 0) return { text: '采集需复核', cls: 'danger', detail: `存在 ${collectionErrors} 条采集错误，建议复采相关楼栋。` };
    if (anomalyRows > 0) return { text: '存在排查项', cls: 'warning', detail: `${anomalyRows} 台设备存在点位、枚举或温度类排查项。` };
    return { text: '数据可用', cls: 'success', detail: '实时详情采集完整，未发现排查项。' };
  }
  const issues = report && report.issues ? report.issues : [];
  const p1 = issues.filter(i => String(i.severity).toUpperCase() === 'P1').length;
  const p2 = issues.filter(i => String(i.severity).toUpperCase() === 'P2' || String(i.severity).toUpperCase() === 'P3').length;
  if (p1 > 0) return { text: '阻止导入', cls: 'danger', detail: `存在 ${p1} 类 P1 问题，建议隔离批次并复采。` };
  if (p2 > 0) return { text: '建议复核', cls: 'warning', detail: `存在 ${p2} 类 P2 问题，数据可查看但建议复核样例。` };
  return { text: '数据可用', cls: 'success', detail: '未发现阻断性质量问题。' };
}

function sampleRowsForQuality(report) {
  const samples = report.samples || {};
  if (report.kind === 'realtime') {
    const rows = [];
    for (const issue of report.issues || []) {
      const key = String(issue.code || '').startsWith('realtime_collection_')
        ? 'realtime_collection_errors'
        : 'realtime_device_anomalies';
      const list = samples[key] || [];
      for (const item of list.slice(0, 8)) rows.push({ issue, item });
    }
    return rows.slice(0, 80);
  }
  const rows = [];
  for (const issue of report.issues || []) {
    const keyMap = {
      placeholder_names: 'placeholder_cards',
      state_mismatch: 'inconsistent_state',
      unknown_comm: 'unknown_comm',
      missing_indicator: 'missing_indicator',
      unknown_switch: 'unknown_switch',
      duplicate_cards_same_page: 'duplicate_cards_same_page',
      duplicate_rendered_pages: 'duplicate_rendered_pages',
      empty_sub_areas: 'empty_sub_areas',
      suspicious_uniform_pages: 'suspicious_uniform_pages',
      uniform_resolved_pages: 'uniform_resolved_pages',
      inline_sub_area: 'inline_sub_areas',
    };
    const list = samples[keyMap[issue.code]] || [];
    for (const item of list.slice(0, 8)) rows.push({ issue, item });
  }
  return rows.slice(0, 80);
}

function sampleLocation(item) {
  const floor = item.floor_label || (item.floor !== undefined && item.floor !== null ? `${item.floor}F` : '');
  return [item.building, floor, item.sub_area || item.subAreaText, item.page_name || item.pageName].filter(Boolean).join(' / ');
}

function sampleObject(item) {
  if (item.name) return item.name;
  if (item.duplicate_names) return item.duplicate_names;
  if (item.cards) return `${item.cards} 台`;
  return item.sub_area || '-';
}

function statusText(sw, comm) {
  if (comm) return comm;
  if (sw === 'ON') return '开机';
  if (sw === 'OFF') return '关机';
  return '-';
}

function pct(part, total) {
  const p = Number(part) || 0;
  const t = Number(total) || 0;
  if (t <= 0) return 0;
  return Math.max(0, Math.min(100, (p / t) * 100));
}

function pctText(part, total) {
  return pct(part, total).toFixed(1) + '%';
}

function loadCardsFirstPage() {
  state.cardPageOffset = 0;
  return loadCards();
}

function attrJson(value) {
  return esc(JSON.stringify(value));
}

function normalizeFloorLabel(value) {
  const raw = String(value || '').trim().toUpperCase();
  if (!raw) return '';
  if (raw === 'BM') return raw;
  if (/^B\d+(\.\d+)?F?$/.test(raw)) return raw.endsWith('F') ? raw : raw + 'F';
  if (/^-?\d+(\.\d+)?F?$/.test(raw)) return raw.endsWith('F') ? raw : raw + 'F';
  return raw;
}

function currentTaskKind() {
  return $('taskKind') ? $('taskKind').value : 'realtimeDetails';
}

function setTaskKind(kind) {
  const actualKind = kind || 'realtimeDetails';
  $('taskKind').value = actualKind;
  const maintenance = kind === 'maintenance' || !['collectImport', 'collectSafe', 'realtimeDetails'].includes(actualKind);
  if ($('maintenanceTaskWrap')) $('maintenanceTaskWrap').hidden = !maintenance;
  document.querySelectorAll('.task-mode').forEach(btn => {
    const value = btn.dataset.taskKindValue;
    const selected = value === actualKind || (value === 'maintenance' && maintenance);
    btn.classList.toggle('selected', selected);
  });
  updateTaskOptions();
}

function switchView(view) {
  state.view = view;
  document.querySelectorAll('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.view === view));
  document.querySelectorAll('.view').forEach(el => el.classList.toggle('active', el.id === `view-${view}`));
  $('viewTitle').textContent = titles[view] || view;
  refreshView(view);
}

function initControls() {
  $('monitorBuilding').innerHTML = BUILDINGS.map(b => `<option value="${b}">${b}</option>`).join('');
  renderRangeFilters();
  renderBuildingCards();

  document.querySelectorAll('.nav-item').forEach(btn => btn.addEventListener('click', () => switchView(btn.dataset.view)));
  document.querySelectorAll('[data-view-jump]').forEach(btn => btn.addEventListener('click', () => switchView(btn.dataset.viewJump)));
  document.querySelectorAll('[data-task-kind]').forEach(btn => btn.addEventListener('click', () => startTask(btn.dataset.taskKind)));
  document.querySelectorAll('.task-mode').forEach(btn => {
    btn.addEventListener('click', () => setTaskKind(btn.dataset.taskKindValue));
  });
  if ($('maintenanceTaskKind')) $('maintenanceTaskKind').addEventListener('change', () => setTaskKind('maintenance'));

  $('selectAllBuildingsBtn').addEventListener('click', () => setAllBuildings(true));
  $('clearBuildingsBtn').addEventListener('click', () => setAllBuildings(false));
  $('refreshPreflightBtn').addEventListener('click', () => loadTaskPreflight(true));
  $('openEmsBtn').addEventListener('click', () => openEmsPage());
  $('viewTaskLogBtn').addEventListener('click', () => scrollTaskLogIntoView());
  $('refreshBtn').addEventListener('click', () => refreshAll());
  $('runSelect').addEventListener('change', () => {
    state.manualRunSelection = true;
    state.runAutoSelected = false;
    state.runId = $('runSelect').value;
    refreshAll();
  });
  $('runPickerBtn').addEventListener('click', e => {
    e.stopPropagation();
    toggleRunPicker();
  });
  $('runPickerMenu').addEventListener('click', e => e.stopPropagation());
  $('monitorBuilding').addEventListener('change', async () => {
    await loadFloors();
    await loadAreaOptions();
  });
  [
    ['buildingFilterBtn', 'buildingFilter'],
    ['zuoFilterBtn', 'zuoFilter'],
    ['floorFilterBtn', 'floorFilter'],
  ].forEach(([btnId, rootId]) => {
    $(btnId).addEventListener('click', e => {
      e.stopPropagation();
      toggleMultiFilter(rootId);
    });
  });
  ['buildingFilterMenu', 'zuoFilterMenu', 'floorFilterMenu'].forEach(id => $(id).addEventListener('click', e => e.stopPropagation()));
  $('filterIssue').addEventListener('change', loadCardsFirstPage);
  $('filterCommState').addEventListener('change', loadCardsFirstPage);
  $('filterAreaType').addEventListener('change', loadCardsFirstPage);
  $('filterTempState').addEventListener('change', loadCardsFirstPage);
  $('filterRealtimeMatch').addEventListener('change', loadCardsFirstPage);
  $('filterRealtimePower').addEventListener('change', loadCardsFirstPage);
  $('filterRealtimeMode').addEventListener('change', loadCardsFirstPage);
  $('filterRealtimeFan').addEventListener('change', loadCardsFirstPage);
  $('filterRealtimeLock').addEventListener('change', loadCardsFirstPage);
  $('filterRealtimeSystem').addEventListener('change', loadCardsFirstPage);
  $('filterRealtimePoints').addEventListener('change', loadCardsFirstPage);
  $('filterRealtimeModbus').addEventListener('change', loadCardsFirstPage);
  $('filterRealtimeModbus').addEventListener('keydown', e => { if (e.key === 'Enter') loadCardsFirstPage(); });
  $('filterTag').addEventListener('change', loadCardsFirstPage);
  ['captureMode', 'realtimeBatchSize', 'realtimeReopenEvery', 'realtimeTimeout', 'realtimeMaxDevices', 'realtimeRefreshInventory', 'realtimeSkipInventory'].forEach(id => {
    const el = $(id);
    if (!el) return;
    const eventName = el.type === 'checkbox' || el.tagName === 'SELECT' ? 'change' : 'input';
    el.addEventListener(eventName, () => {
      state.taskPreflight = null;
      loadTaskPreflight(false);
    });
  });
  $('startTaskBtn').addEventListener('click', () => startTask($('taskKind').value));
  $('stopTaskBtn').addEventListener('click', stopTask);
  $('applyFilterBtn').addEventListener('click', loadCardsFirstPage);
  $('clearFiltersBtn').addEventListener('click', clearDataFilters);
  $('filterQ').addEventListener('keydown', e => { if (e.key === 'Enter') loadCardsFirstPage(); });
  $('prevCardsPage').addEventListener('click', () => {
    state.cardPageOffset = Math.max(0, state.cardPageOffset - state.cardPageSize);
    loadCards();
  });
  $('nextCardsPage').addEventListener('click', () => {
    const total = Number(state.cards.total || 0);
    const next = state.cardPageOffset + state.cardPageSize;
    if (next < total) {
      state.cardPageOffset = next;
      loadCards();
    }
  });
  $('cardPageSize').addEventListener('change', () => {
    state.cardPageSize = Number($('cardPageSize').value) || 500;
    state.cardPageOffset = 0;
    loadCards();
  });
  document.querySelectorAll('.compact-filters select, .compact-filters input:not([type="checkbox"]):not([type="hidden"])').forEach(el => {
    const sync = () => syncFilterActiveState(el);
    el.addEventListener('change', sync);
    el.addEventListener('input', sync);
    sync();
  });
  $('areaFilterBtn').addEventListener('click', e => {
    e.stopPropagation();
    toggleAreaFilter();
  });
  $('filterGroups').addEventListener('click', e => e.stopPropagation());
  document.addEventListener('click', closeMultiFilters);
  document.addEventListener('click', closeAreaFilter);
  document.addEventListener('click', closeRunPicker);
  document.querySelectorAll('[data-close-device-detail]').forEach(el => el.addEventListener('click', closeDeviceDetail));
  document.addEventListener('keydown', e => { if (e.key === 'Escape') closeDeviceDetail(); });
  $('saveGroupBtn').addEventListener('click', saveGroup);
  $('clearGroupBtn').addEventListener('click', clearGroupForm);
  $('itemTargetType').addEventListener('change', () => {
    updateItemTargetInputs();
    renderAreaOptionSelects();
  });
  $('monitorFloor').addEventListener('change', loadAreaOptions);
  $('monitorSubArea').addEventListener('change', () => {
    renderDeviceOptions();
    updateAreaPreview();
  });
  $('monitorCardName').addEventListener('change', updateAreaPreview);
  $('addGroupItemBtn').addEventListener('click', addGroupItem);
  $('addFloorBtn').addEventListener('click', addFloorCatalog);
  updateItemTargetInputs();
  updateBuildingSelectionLabel();
  setTaskKind('realtimeDetails');
  renderTaskStatusCards(null);
}

async function refreshAll() {
  try {
    const health = await api('/api/health');
    $('dbState').textContent = health.db_exists ? '数据库已连接' : '数据库未找到';
  } catch (e) {
    $('dbState').textContent = '连接失败';
  }
  await loadRuns();
  await loadFloors();
  await loadAreaOptions();
  await loadFilterOptions();
  await Promise.allSettled([loadRealtimeCoverage(), loadSummary(), loadGroups(), loadQuality(), loadReports(), loadRealtimeSummary(), loadTaskPreflight(true)]);
  if (state.view === 'data') await loadCards();
}

function refreshView(view) {
  if (view === 'overview') Promise.allSettled([loadSummary(), loadGroups()]);
  if (view === 'monitor') loadGroups();
  if (view === 'data') {
    setTimeout(() => loadCards().catch(err => console.error(err)), 0);
    Promise.allSettled([loadFilterOptions(), loadRealtimeCoverage()])
      .then(() => loadCards())
      .catch(err => console.error(err));
  }
  if (view === 'quality') loadQuality();
  if (view === 'reports') loadReports();
  if (view === 'tasks') Promise.allSettled([loadTaskPreflight(true), pollTask()]);
}

async function loadSummary() {
  const qs = state.runId ? '?run_id=' + encodeURIComponent(state.runId) : '';
  const data = await api('/api/summary' + qs);
  state.summary = data;
  renderRunPicker();
  const q = data.quality;
  const qualityMismatch = q && q.summary && !!q.stale;
  const issues = q && q.summary && !qualityMismatch ? q.summary.issue_count : null;
  renderOverviewMetrics(data, { qualityMismatch, issues });
  $('qualityBadge').textContent = qualityMismatch ? '质量口径不一致' : issues === null ? '质量 未生成' : issues > 0 ? `质量 ${issues} 项问题` : '质量 OK';
  $('qualityBadge').className = 'badge ' + (qualityMismatch || issues > 0 ? 'warning' : 'success');

  const buildingAnomalies = detectBuildingAnomalies(data);
  renderOverviewAlerts(data, { qualityMismatch, buildingAnomalies, autoWarning: state.activeRunWarning });
  renderOverviewCharts(data);

  $('buildingRows').innerHTML = (data.buildings || []).map(b => `
    <tr class="${buildingAnomalies.has(b.building) ? 'row-danger' : Math.abs(Number(b.delta || 0)) > 0 ? 'row-warn' : ''}" data-jump-building="${esc(b.building)}">
      <td>${esc(b.building)} ${esc(b.name || '')}</td>
      <td>${b.total}</td>
      <td>${b.on}</td>
      <td>${b.off}</td>
      <td>${b.offline}</td>
      <td>${buildingAnomalies.has(b.building) ? '疑似异常' : b.delta === 0 ? 'OK' : esc(b.delta > 0 ? '+' + b.delta : b.delta)}</td>
      <td>${fmtDate(b.updated_at)}</td>
    </tr>
  `).join('');
  $('buildingRows').querySelectorAll('[data-jump-building]').forEach(row => {
    row.addEventListener('click', () => jumpToDataWithFilter({ building: row.dataset.jumpBuilding }));
  });
  renderBuildingCards();
  renderTaskPreflight();
}

function metricValue(value) {
  return value === null || value === undefined || value === '' ? '--' : value;
}

function renderOverviewMetrics(data, checks) {
  const latest = latestCollectionLabel().replace(/^最新采集：/, '');
  const unknown = Number(data.comm_unknown || 0);
  const offline = data.offline === null || data.offline === undefined ? null : Number(data.offline || 0);
  const offlineAbnormal = offline === null
    ? '--'
    : unknown > 0
      ? `${offline} / ${unknown}`
      : String(offline);
  const cards = [
    { label: '总设备数', value: metricValue(data.total), meta: '当前数据口径', type: 'info', jump: 'all' },
    { label: '实时详情数', value: metricValue(data.detail_total), meta: '最新详情文件', type: 'info' },
    { label: '已纳管', value: metricValue(data.matched), meta: '实时详情可用', type: 'success' },
    { label: '需人工分类', value: metricValue(data.realtime_unmatched), meta: '区域/座号待确认', type: Number(data.realtime_unmatched || 0) > 0 ? 'warning' : 'success', jump: 'unmatched' },
    { label: '运行开机', value: metricValue(data.on), meta: `运行关机 ${metricValue(data.off)}`, type: 'success' },
    { label: '通讯离线 / 未知', value: offlineAbnormal, meta: unknown > 0 ? '右侧为通讯未知' : '通讯离线设备', type: (offline || unknown) ? 'warning' : 'success', jump: 'needs_review' },
    { label: '质量问题数', value: checks.qualityMismatch ? '--' : metricValue(checks.issues), meta: checks.qualityMismatch ? '报告口径不一致' : '质量审计', type: checks.qualityMismatch || Number(checks.issues || 0) > 0 ? 'warning' : 'success', jumpQuality: true },
    { label: '最近采集时间', value: latest || '--', meta: state.runId ? '当前选择批次' : '当前数据', type: 'muted' },
  ];
  const root = document.querySelector('#view-overview .kpi-grid');
  if (root) {
    root.innerHTML = cards.map((card, index) => {
      const html = uiMetricCard(card);
      const attrs = card.jump
        ? ` data-overview-jump="${esc(card.jump)}"`
        : card.jumpQuality
          ? ' data-overview-quality="1"'
          : '';
      return html.replace('<div class="', `<div${attrs} class="`);
    }).join('');
    root.querySelectorAll('[data-overview-jump]').forEach(el => {
      el.addEventListener('click', () => jumpToDataWithFilter({ quick: el.dataset.overviewJump }));
    });
    root.querySelectorAll('[data-overview-quality]').forEach(el => {
      el.addEventListener('click', () => switchView('quality'));
    });
  }
}

function buildingRunFor(name) {
  return (state.runs || []).find(r => parseRunBuildings(r).includes(name) && !Number(r.is_anomaly || 0)) || currentDataRun();
}

function renderBuildingCards() {
  const root = $('buildingChecks');
  if (!root) return;
  const selected = new Set(state.taskBuildings && state.taskBuildings.length ? state.taskBuildings : BUILDINGS);
  root.innerHTML = BUILDINGS.map(b => {
    const run = buildingRunFor(b);
    const quality = runQualityLabel(run);
    const summary = state.summary && Array.isArray(state.summary.buildings)
      ? state.summary.buildings.find(x => x.building === b)
      : null;
    const actual = summary ? Number(summary.total || 0) : Number(run && run.card_count || 0);
    const baseline = BASELINE_CARDS[b] || 0;
    const checked = selected.has(b);
    return `
      <label class="building-card ${checked ? 'selected' : ''}">
        <input type="checkbox" value="${esc(b)}" ${checked ? 'checked' : ''}>
        <strong>${esc(b)}</strong>
        <span>${esc(actual || baseline)} / ${esc(baseline)} 张</span>
        <em class="${quality.cls ? 'status-' + quality.cls : ''}">${esc(quality.text)}</em>
      </label>
    `;
  }).join('');
  root.querySelectorAll('input').forEach(input => {
    input.addEventListener('change', () => {
      input.closest('.building-card').classList.toggle('selected', input.checked);
      state.taskBuildings = Array.from(root.querySelectorAll('input:checked')).map(i => i.value);
      updateBuildingSelectionLabel();
    });
  });
  updateBuildingSelectionLabel();
}

function detectBuildingAnomalies(data) {
  const rows = data.buildings || [];
  const signatureCounts = new Map();
  for (const b of rows) {
    const sig = [b.total, b.on, b.off, b.offline].map(v => Number(v) || 0).join(':');
    signatureCounts.set(sig, (signatureCounts.get(sig) || 0) + 1);
  }
  const out = new Set();
  for (const b of rows) {
    const sig = [b.total, b.on, b.off, b.offline].map(v => Number(v) || 0).join(':');
    const repeated = signatureCounts.get(sig) >= 3 && Number(b.total || 0) > 0;
    const baseline = Number(b.baseline || 0);
    const total = Number(b.total || 0);
    const largeDelta = baseline > 0 && Math.abs(total - baseline) / baseline > 0.25;
    if (repeated && largeDelta) out.add(b.building);
  }
  return out;
}

function renderOverviewAlerts(data, checks) {
  const alerts = [];
  if (checks.autoWarning) {
    alerts.push({
      level: 'info',
      text: checks.autoWarning,
    });
  }
  if (checks.qualityMismatch) {
    const reportTotal = data.quality.report_total || data.quality.summary.total_cards;
    alerts.push({
      level: 'warn',
      text: `当前展示批次 ${activeRunLabel()} 共 ${data.total} 张，质量报告 ${reportTotal} 张；请重新运行该批次质量审计后再按质量结论判断。`,
    });
  }
  const coverage = state.realtimeCoverage || {};
  const realtimeRows = Number(coverage.realtime_rows || 0);
  const unmatched = Number(coverage.realtime_unmatched || 0);
  const review = Number(coverage.needs_review || 0);
  if (realtimeRows && (unmatched || review)) {
    alerts.push({
      level: review ? 'warn' : 'info',
      text: `实时详情 ${realtimeRows} 条，需排查 ${review} 条，需人工分类 ${unmatched} 条。数据管理已按实时详情字段筛选和导出。`,
    });
  }
  if (checks.buildingAnomalies.size) {
    alerts.push({
      level: 'danger',
      text: `当前展示批次楼栋数据疑似异常：${Array.from(checks.buildingAnomalies).join('、')}。这些楼栋与其他楼栋统计完全重复且偏离基线，建议切换到最近正常批次或重新采集后再导入。`,
    });
  }
  $('overviewAlerts').innerHTML = alerts.map(a => `<div class="alert ${a.level === 'warn' ? 'warn' : a.level === 'info' ? 'info' : 'danger'}">${esc(a.text)}</div>`).join('');
}

function renderOverviewCharts(data) {
  $('overviewRunLabel').textContent = activeRunLabel();
  renderStackChart('statusChart', [
    ['开机', data.on || 0, 'seg-on'],
    ['关机', data.off || 0, 'seg-off'],
    ['离线', data.offline || 0, 'seg-offline'],
  ], data.total || 0);
  renderStackChart('areaChart', [
    ['公区', data.public || 0, 'seg-public'],
    ['非公区', data.non_public || 0, 'seg-private'],
  ], data.total || 0);
  const maxRate = Math.max(...(data.buildings || []).map(b => pct(b.on, b.total)), 1);
  $('buildingChart').innerHTML = (data.buildings || []).map(b => {
    const rate = pct(b.on, b.total);
    const width = Math.max(2, rate / maxRate * 100);
    return `
      <div class="bar-row">
        <span>${esc(b.building)}</span>
        <div class="bar-track"><div class="bar-fill" style="width:${width.toFixed(1)}%"></div></div>
        <strong>${pctText(b.on, b.total)}</strong>
      </div>
    `;
  }).join('');
}

function renderStackChart(id, items, total) {
  const safeTotal = Number(total) || items.reduce((sum, item) => sum + Number(item[1] || 0), 0);
  const segments = items.map(([label, value, cls]) => {
    const width = pct(value, safeTotal);
    return `<div class="stack-seg ${cls}" title="${esc(label)} ${esc(value)}" style="width:${width.toFixed(2)}%"></div>`;
  }).join('');
  const legend = items.map(([label, value, cls]) => `
    <span class="legend-item"><span class="legend-dot ${cls}"></span>${esc(label)} ${esc(value)} (${pctText(value, safeTotal)})</span>
  `).join('');
  $(id).innerHTML = `<div class="stack-track">${segments}</div><div class="chart-legend">${legend}</div>`;
}

async function loadGroups() {
  const qs = state.runId ? '?run_id=' + encodeURIComponent(state.runId) : '';
  const data = await api('/api/areas' + qs);
  state.groups = data || { groups: [], items: [], stats: [] };
  renderGroups();
  renderAreaSummary();
}

function renderGroups() {
  const groups = state.groups.groups || [];
  const items = state.groups.items || [];
  const statsById = new Map((state.groups.stats || []).map(s => [Number(s.id), s]));
  $('groupRows').innerHTML = groups.map(g => {
    const st = statsById.get(Number(g.id)) || {};
    return `
      <tr>
        <td>${esc(g.name)} ${g.group_kind === 'system' ? '<span class="tag">系统区域</span>' : ''}</td>
        <td>${esc(g.area_label || '')}</td>
        <td>${esc(priorityLabel(g.priority))}</td>
        <td>${st.item_count ?? g.item_count ?? 0}</td>
        <td>${st.on_count || 0}/${st.total || 0}</td>
        <td>
          <button class="ghost small" data-edit-group="${g.id}">编辑</button>
          ${g.locked ? '<button class="ghost small" disabled>删除</button>' : `<button class="ghost small" data-delete-group="${g.id}">删除</button>`}
        </td>
      </tr>
    `;
  }).join('') || '<tr><td colspan="6" class="muted">暂无区域</td></tr>';

  const customGroups = groups.filter(g => g.group_kind !== 'system' && !g.locked);
  $('itemGroup').innerHTML = customGroups.map(g => `<option value="${g.id}">${esc(g.name)}</option>`).join('')
    || '<option value="">请先新建自定义区域</option>';
  $('itemGroup').disabled = customGroups.length === 0;
  $('addGroupItemBtn').disabled = customGroups.length === 0;
  renderAreaFilter(groups);
  $('groupItemRows').innerHTML = items.map(i => `
    <tr>
      <td>${esc(i.group_name)}</td>
      <td>${esc(targetTypeLabel(i.target_type))}</td>
      <td>${esc(itemTargetLabel(i))}</td>
      <td>${esc(i.note || '')}</td>
      <td><button class="ghost small" data-delete-item="${i.id}">移除</button></td>
    </tr>
  `).join('') || '<tr><td colspan="5" class="muted">暂无区域成员</td></tr>';

  document.querySelectorAll('[data-edit-group]').forEach(btn => btn.addEventListener('click', () => editGroup(btn.dataset.editGroup)));
  document.querySelectorAll('[data-delete-group]').forEach(btn => btn.addEventListener('click', () => deleteGroup(btn.dataset.deleteGroup)));
  document.querySelectorAll('[data-delete-item]').forEach(btn => btn.addEventListener('click', () => deleteGroupItem(btn.dataset.deleteItem)));
}

function targetTypeLabel(type) {
  return type === 'device' ? '设备' : type === 'sub_area' ? '子区' : '楼层';
}

function priorityLabel(value) {
  if (value === '重点') return '关注';
  return value || '普通';
}

function priorityValue(value) {
  if (value === '关注') return '重点';
  return value || '普通';
}

function areaGroups() {
  return state.groups.groups || [];
}

function selectedAreaGroups() {
  const selected = new Set(state.selectedAreaIds.map(String));
  return areaGroups().filter(g => selected.has(String(g.id)));
}

function filterValues(kind) {
  return state.dataFilters[kind] || [];
}

function setFilterValues(kind, values) {
  const unique = [...new Set((values || []).map(String).filter(Boolean))];
  state.dataFilters[kind] = unique;
  const hiddenId = kind === 'buildings' ? 'filterBuilding' : kind === 'zuos' ? 'filterZuo' : 'filterFloor';
  if ($(hiddenId)) $(hiddenId).value = unique.join(',');
}

function toggleFilterValue(kind, value) {
  const current = new Set(filterValues(kind));
  const text = String(value || '');
  if (!text) return;
  if (current.has(text)) current.delete(text);
  else current.add(text);
  setFilterValues(kind, Array.from(current));
}

function filterSummary(label, values) {
  if (!values.length) return label;
  if (values.length === 1) return `${label}：${values[0]}`;
  return `${label}：${values.length}项`;
}

function setMultiFilterUi(rootId, labelId, buttonId, baseLabel, values) {
  const root = $(rootId);
  const btn = $(buttonId);
  const label = $(labelId);
  const active = values.length > 0;
  label.textContent = filterSummary(baseLabel, values);
  btn.title = active ? values.join('、') : baseLabel;
  root.classList.toggle('has-value', active);
}

function toggleMultiFilter(rootId, force) {
  closeMultiFilters(rootId);
  closeAreaFilter();
  const root = $(rootId);
  const open = force === undefined ? !root.classList.contains('open') : !!force;
  root.classList.toggle('open', open);
  const btn = root.querySelector('.multi-filter-btn');
  if (btn) btn.setAttribute('aria-expanded', open ? 'true' : 'false');
}

function closeMultiFilters(exceptId = '') {
  document.querySelectorAll('.multi-filter.open').forEach(root => {
    if (exceptId && root.id === exceptId) return;
    root.classList.remove('open');
    const btn = root.querySelector('.multi-filter-btn');
    if (btn) btn.setAttribute('aria-expanded', 'false');
  });
}

async function onRangeFilterChanged(kind) {
  if (kind === 'buildings') {
    await loadFilterOptions();
    await loadRealtimeCoverage();
    await loadFloors();
  }
  renderRangeFilters();
  await loadCardsFirstPage();
}

function renderMultiFilterMenu({ kind, rootId, menuId, labelId, buttonId, baseLabel, options }) {
  const selected = new Set(filterValues(kind));
  const validValues = new Set(options.map(o => String(o.value)));
  setFilterValues(kind, filterValues(kind).filter(v => validValues.has(v)));
  const currentValues = filterValues(kind);
  const allActive = currentValues.length === 0;
  setMultiFilterUi(rootId, labelId, buttonId, baseLabel, currentValues);
  syncClearFiltersButton();
  const optionRows = options.map(o => {
    const value = String(o.value);
    const active = currentValues.includes(value);
    const count = o.count !== undefined && o.count !== null && o.count !== '' ? `<em>${esc(o.count)}</em>` : '';
    return `
      <button type="button" class="multi-option ${active ? 'selected' : ''}" data-kind="${esc(kind)}" data-value="${esc(value)}">
        <span>${esc(o.label || value)}</span>${count}<strong>${active ? '✓' : ''}</strong>
      </button>`;
  }).join('');
  $(menuId).innerHTML = `
    <button type="button" class="multi-option ${allActive ? 'selected' : ''}" data-kind="${esc(kind)}" data-clear="1">
      <span>${esc(baseLabel)}</span><em>默认</em><strong>${allActive ? '✓' : ''}</strong>
    </button>
    ${optionRows || '<div class="multi-empty">无可选项</div>'}
  `;
  $(menuId).querySelectorAll('[data-clear]').forEach(btn => btn.addEventListener('click', async e => {
    e.stopPropagation();
    setFilterValues(kind, []);
    await onRangeFilterChanged(kind);
  }));
  $(menuId).querySelectorAll('[data-value]').forEach(btn => btn.addEventListener('click', async e => {
    e.stopPropagation();
    toggleFilterValue(kind, btn.dataset.value);
    await onRangeFilterChanged(kind);
  }));
}

function renderRangeFilters() {
  renderMultiFilterMenu({
    kind: 'buildings',
    rootId: 'buildingFilter',
    menuId: 'buildingFilterMenu',
    labelId: 'buildingFilterLabel',
    buttonId: 'buildingFilterBtn',
    baseLabel: '楼栋',
    options: BUILDINGS.map(b => ({ value: b, label: b })),
  });
  const zuoOptions = state.filterOptions.zuos || [];
  renderMultiFilterMenu({
    kind: 'zuos',
    rootId: 'zuoFilter',
    menuId: 'zuoFilterMenu',
    labelId: 'zuoFilterLabel',
    buttonId: 'zuoFilterBtn',
    baseLabel: '座号',
    options: zuoOptions,
  });
  const floorOptions = state.filterOptions.floors || [];
  renderMultiFilterMenu({
    kind: 'floors',
    rootId: 'floorFilter',
    menuId: 'floorFilterMenu',
    labelId: 'floorFilterLabel',
    buttonId: 'floorFilterBtn',
    baseLabel: '楼层',
    options: floorOptions,
  });
}

function toggleAreaFilter(force) {
  const root = $('areaFilter');
  const open = force === undefined ? !root.classList.contains('open') : !!force;
  if (open) closeMultiFilters();
  root.classList.toggle('open', open);
  $('areaFilterBtn').setAttribute('aria-expanded', open ? 'true' : 'false');
}

function closeAreaFilter() {
  const root = $('areaFilter');
  if (!root || !root.classList.contains('open')) return;
  toggleAreaFilter(false);
}

function renderAreaFilter(groups = areaGroups()) {
  const valid = new Set(groups.map(g => String(g.id)));
  state.selectedAreaIds = state.selectedAreaIds.map(String).filter(id => valid.has(id));
  const selected = new Set(state.selectedAreaIds);
  const allActive = selected.size === 0;

  $('areaFilterLabel').textContent = allActive
    ? '区域组'
    : `区域组 ${selected.size}`;
  $('areaFilter').classList.toggle('has-value', !allActive);
  $('areaFilterBtn').title = allActive ? '区域组' : selectedAreaGroups().map(g => g.name).join('、');

  const selectedGroups = selectedAreaGroups();
  $('areaFilterTags').innerHTML = allActive
    ? ''
    : selectedGroups.map(g => `<span class="area-token">${esc(g.name)}</span>`).join('');

  const rows = [
    `<button type="button" class="area-option ${allActive ? 'selected' : ''}" data-area-all="1">
      <span>区域组</span><em>默认</em><strong>${allActive ? '✓' : ''}</strong>
    </button>`,
    ...groups.map(g => `
      <button type="button" class="area-option ${selected.has(String(g.id)) ? 'selected' : ''}" data-area-id="${g.id}">
        <span>${esc(g.name)}${g.group_kind === 'system' ? '<em>系统</em>' : ''}</span><strong>${selected.has(String(g.id)) ? '✓' : ''}</strong>
      </button>
    `),
  ];
  $('filterGroups').innerHTML = rows.join('');
  $('filterGroups').querySelectorAll('[data-area-all]').forEach(btn => btn.addEventListener('click', e => {
    e.stopPropagation();
    state.selectedAreaIds = [];
    renderAreaFilter();
    loadCardsFirstPage();
  }));
  $('filterGroups').querySelectorAll('[data-area-id]').forEach(btn => btn.addEventListener('click', e => {
    e.stopPropagation();
    const id = String(btn.dataset.areaId);
    const next = new Set(state.selectedAreaIds.map(String));
    if (next.has(id)) next.delete(id);
    else next.add(id);
    state.selectedAreaIds = Array.from(next);
    renderAreaFilter();
    loadCardsFirstPage();
  }));
  syncClearFiltersButton();
}

function renderAreaSummary() {
  const stats = state.groups.stats || [];
  const preferred = ['公区', '非公区'];
  const rows = [
    ...preferred.map(name => stats.find(s => s.name === name)).filter(Boolean),
    ...stats.filter(s => !preferred.includes(s.name)).slice(0, 3),
  ].slice(0, 5);
  const el = $('areaSummary');
  if (!el) return;
  el.innerHTML = rows.length
    ? rows.map(s => `
        <div class="event">
          <strong>${esc(s.name)} ${uiBadge(`开机 ${s.on_count || 0}`, Number(s.on_count || 0) > 0 ? 'info' : 'success')}</strong>
          <span>设备 ${esc(s.total || 0)}，关机 ${esc(s.off_count || 0)}，离线 ${esc(s.offline_count || 0)}，覆盖区域 ${esc(s.covered_areas || 0)}</span>
        </div>
      `).join('')
    : '<div class="event"><strong>暂无区域</strong><span>系统会自动生成公区和非公区，自定义区域可在区域管理页维护。</span></div>';
}

function itemTargetLabel(item) {
  if (item.target_type === 'device') return `${item.building} ${item.card_name}`;
  if (item.target_type === 'sub_area') return `${item.building} ${item.floor_label} ${item.sub_area_text}`;
  return `${item.building} ${item.floor_label}`;
}

function updateItemTargetInputs() {
  const type = $('itemTargetType').value;
  $('monitorSubArea').disabled = type !== 'sub_area';
  $('monitorCardName').disabled = type !== 'device';
  updateAreaPreview();
}

async function loadAreaOptions() {
  const params = new URLSearchParams();
  if (state.runId) params.set('run_id', state.runId);
  if ($('monitorBuilding').value) params.set('building', $('monitorBuilding').value);
  if ($('monitorFloor').value) params.set('floor', $('monitorFloor').value);
  try {
    state.areaOptions = await api('/api/area-options?' + params.toString()) || { sub_areas: [], devices: [] };
  } catch {
    state.areaOptions = { sub_areas: [], devices: [] };
  }
  renderAreaOptionSelects();
}

function renderAreaOptionSelects() {
  const previousSubArea = $('monitorSubArea').value;
  const subAreas = state.areaOptions.sub_areas || [];
  $('monitorSubArea').innerHTML = subAreas.map(sa => `
    <option value="${esc(sa.sub_area_text)}">${esc(sa.sub_area_text || '未命名子区')} · ${esc(sa.card_count || 0)}台</option>
  `).join('') || '<option value="">当前楼层无可选子区</option>';
  if (subAreas.some(sa => String(sa.sub_area_text) === String(previousSubArea))) {
    $('monitorSubArea').value = previousSubArea;
  }
  renderDeviceOptions();
  updateItemTargetInputs();
}

function renderDeviceOptions() {
  const previousDevice = $('monitorCardName').value;
  const selectedSubArea = $('monitorSubArea').value;
  const devices = (state.areaOptions.devices || []).filter(d => {
    return $('itemTargetType').value !== 'device' || !selectedSubArea || String(d.sub_area_text || '') === String(selectedSubArea);
  });
  $('monitorCardName').innerHTML = devices.map(d => `
    <option value="${esc(d.name)}">${esc(d.name || '-')} · ${esc(d.sub_area_text || '')} · ${esc(statusText(d.switch, d.comm))}</option>
  `).join('') || '<option value="">当前条件无可选设备</option>';
  if (devices.some(d => String(d.name) === String(previousDevice))) {
    $('monitorCardName').value = previousDevice;
  }
  updateAreaPreview();
}

function updateAreaPreview() {
  const el = $('areaPreview');
  if (!el) return;
  const type = $('itemTargetType').value;
  const subAreas = state.areaOptions.sub_areas || [];
  const devices = state.areaOptions.devices || [];
  let text = '选择范围后显示预览';
  if (type === 'floor') {
    text = `整层范围：${devices.length} 台设备`;
  } else if (type === 'sub_area') {
    const selected = $('monitorSubArea').value;
    const row = subAreas.find(sa => String(sa.sub_area_text || '') === String(selected));
    text = row ? `子区范围：${row.card_count || 0} 台设备，开机 ${row.on_count || 0}` : '请选择子区';
  } else if (type === 'device') {
    const name = $('monitorCardName').value;
    const device = devices.find(d => String(d.name || '') === String(name));
    text = device ? `单台设备：${device.name} · ${statusText(device.switch, device.comm)}` : '请选择设备';
  }
  el.textContent = text;
}

function editGroup(id) {
  const group = (state.groups.groups || []).find(g => String(g.id) === String(id));
  if (!group) return;
  state.editingGroupId = group.id;
  $('groupName').value = group.name || '';
  $('groupName').disabled = !!group.locked;
  $('groupArea').value = group.area_label || '';
  $('groupPriority').value = priorityLabel(group.priority || '普通');
  $('groupPriority').disabled = !!group.locked;
  $('groupDescription').value = group.description || '';
}

function clearGroupForm() {
  state.editingGroupId = null;
  $('groupName').value = '';
  $('groupName').disabled = false;
  $('groupArea').value = '';
  $('groupPriority').value = '普通';
  $('groupPriority').disabled = false;
  $('groupDescription').value = '';
}

async function saveGroup() {
  try {
    await api('/api/areas', {
      method: 'POST',
      body: {
        id: state.editingGroupId,
        name: $('groupName').value,
        area_label: $('groupArea').value,
        priority: priorityValue($('groupPriority').value),
        description: $('groupDescription').value,
      },
    });
    clearGroupForm();
    await loadGroups();
  } catch (e) {
    window.alert(e.message || e);
  }
}

async function deleteGroup(id) {
  try {
    await api(`/api/areas/${id}`, { method: 'DELETE' });
    await loadGroups();
  } catch (e) {
    window.alert(e.message || e);
  }
}

async function addGroupItem() {
  if (!$('itemGroup').value) {
    window.alert('请先新建自定义区域组。');
    return;
  }
  const targetType = $('itemTargetType').value;
  if (targetType === 'sub_area' && !$('monitorSubArea').value) {
    window.alert('请选择要加入的子区。');
    return;
  }
  if (targetType === 'device' && !$('monitorCardName').value) {
    window.alert('请选择要加入的设备。');
    return;
  }
  try {
    await api('/api/area-items', {
      method: 'POST',
      body: {
        group_id: $('itemGroup').value,
        target_type: targetType,
        building: $('monitorBuilding').value,
        floor_label: $('monitorFloor').value,
        sub_area_text: targetType === 'sub_area' ? $('monitorSubArea').value : '',
        card_name: targetType === 'device' ? $('monitorCardName').value : '',
        note: $('monitorNote').value,
      },
    });
    $('monitorNote').value = '';
    updateItemTargetInputs();
    await loadGroups();
  } catch (e) {
    window.alert(e.message || e);
  }
}

async function deleteGroupItem(id) {
  try {
    await api(`/api/area-items/${id}`, { method: 'DELETE' });
    await loadGroups();
  } catch (e) {
    window.alert(e.message || e);
  }
}

async function loadRuns() {
  const payload = await api('/api/runs');
  const runs = Array.isArray(payload) ? payload : (payload.runs || []);
  state.runs = runs || [];
  state.latestCollection = Array.isArray(payload) ? null : (payload.latest_collection || null);
  if (!Array.isArray(payload) && payload.realtime_summary) {
    state.realtimeSummary = payload.realtime_summary;
  }
  const current = $('runSelect').value;
  const recommended = recommendedOverviewRun();
  const visibleRuns = state.runs.filter(r => !Number(r.is_anomaly || 0));
  state.activeRunWarning = '';
  $('runSelect').innerHTML = `<option value="">当前数据</option>` + visibleRuns.map(r => `
    <option value="${r.id}">${esc(runOptionLabel(r))}</option>
  `).join('');
  if (current && visibleRuns.some(r => String(r.id) === String(current))) {
    $('runSelect').value = current;
    state.runId = current;
  } else if (!state.manualRunSelection && latestRunIsProbablyBad() && recommended) {
    $('runSelect').value = String(recommended.id);
    state.runId = String(recommended.id);
    state.runAutoSelected = true;
    state.activeRunWarning = `最新采集批次 ${latestCollectionLabel()} 与楼栋基线明显不一致，已自动切换到最近正常批次 ${recommended.label}。`;
  } else {
    $('runSelect').value = '';
    state.runId = '';
    state.runAutoSelected = false;
  }
  renderRunPicker();
  renderRunRows();
}

function toggleRunPicker(force) {
  const root = $('runPicker');
  const open = force === undefined ? !root.classList.contains('open') : !!force;
  root.classList.toggle('open', open);
  $('runPickerBtn').setAttribute('aria-expanded', open ? 'true' : 'false');
  state.runPickerOpen = open;
}

function closeRunPicker() {
  const root = $('runPicker');
  if (!root || !root.classList.contains('open')) return;
  toggleRunPicker(false);
}

function selectRunView(value) {
  state.manualRunSelection = !!value;
  state.runAutoSelected = false;
  state.runId = value ? String(value) : '';
  $('runSelect').value = state.runId;
  closeRunPicker();
  refreshAll();
}

function renderRunPicker() {
  const current = currentRunText();
  $('runPickerLabel').textContent = current.label;
  $('runPickerMeta').textContent = current.meta;
  const normalRuns = (state.runs || []).filter(r => !Number(r.is_anomaly || 0) && !Number(r.suggested_anomaly || 0));
  const suggestedRuns = (state.runs || []).filter(r => !Number(r.is_anomaly || 0) && Number(r.suggested_anomaly || 0));
  const latestDetailText = currentDetailCount() ? `详情 ${currentDetailCount()} 条` : '详情 --';
  const deltaText = currentDataDeltaText();
  const latestTime = state.realtimeSummary && state.realtimeSummary.latest_mtime
    ? fmtDateTime(state.realtimeSummary.latest_mtime)
    : '--';
  const rows = [
    `<div class="run-menu-section">
      <span>当前可用数据</span>
      <button type="button" class="run-option ${!state.runId ? 'selected' : ''}" data-run-value="">
        <strong>最新实时详情</strong>
        <em>${esc(currentDataMetaText())}${deltaText ? ` · ${esc(deltaText)}` : ''}</em>
      </button>
    </div>`,
    `<div class="run-menu-section">
      <span>实时详情状态</span>
      <div class="run-file-state">
        <strong>${esc(latestDetailText)}</strong>
        <em>最新时间 ${esc(latestTime)}${deltaText ? ` · ${esc(deltaText)}` : ''}</em>
      </div>
    </div>`,
    `<div class="run-menu-section">
      <span>历史正常批次</span>
      ${normalRuns.map(r => `
        <button type="button" class="run-option ${String(state.runId) === String(r.id) ? 'selected' : ''}" data-run-value="${r.id}">
          <strong>${esc(r.label || r.run_key)}</strong>
          <em>${esc(r.card_count || 0)}张 · 质量${esc(runQualityLabel(r).text)}</em>
        </button>
      `).join('') || '<div class="run-menu-empty">暂无正常历史批次</div>'}
    </div>`,
  ];
  if (suggestedRuns.length) {
    rows.push(`<div class="run-menu-section">
      <span>疑似异常批次</span>
      ${suggestedRuns.map(r => `
        <button type="button" class="run-option warn ${String(state.runId) === String(r.id) ? 'selected' : ''}" data-run-value="${r.id}">
          <strong>${esc(r.label || r.run_key)}</strong>
          <em>${esc(r.anomaly_reason || '疑似异常')} · ${esc(r.card_count || 0)}张</em>
        </button>
      `).join('')}
    </div>`);
  }
  $('runPickerMenu').innerHTML = rows.join('');
  $('runPickerMenu').querySelectorAll('[data-run-value]').forEach(btn => {
    btn.addEventListener('click', () => selectRunView(btn.dataset.runValue || ''));
  });
}

async function loadFilterOptions() {
  const params = new URLSearchParams();
  if (state.runId) params.set('run_id', state.runId);
  const buildings = filterValues('buildings');
  if (buildings.length) params.set('building', buildings.join(','));
  const data = await api('/api/filter-options?' + params.toString());
  state.filterOptions = data || { modes: [], fans: [], zuos: [] };
  renderFilterOptions();
}

async function loadRealtimeSummary() {
  const data = await api('/api/realtime/summary');
  state.realtimeSummary = data || { file_count: 0, total_rows: 0, files: [] };
  renderTaskPreflight();
}

async function loadRealtimeCoverage() {
  const params = new URLSearchParams();
  if (state.runId) params.set('run_id', state.runId);
  const buildings = filterValues('buildings');
  if (buildings.length) params.set('building', buildings.join(','));
  const data = await api('/api/realtime/coverage?' + params.toString());
  state.realtimeCoverage = data || { db_rows: 0, realtime_rows: 0, matched: 0, db_missing_realtime: 0, realtime_unmatched: 0, needs_review: 0, delta: 0 };
  renderRunPicker();
  renderRealtimeCoverageBadge();
  if (state.summary && state.view === 'overview') {
    const qualityMismatch = state.summary.quality && state.summary.quality.summary && (state.summary.quality.stale || Number(state.summary.quality.summary.total_cards || 0) !== Number(state.summary.total || 0));
    renderOverviewAlerts(state.summary, {
      qualityMismatch,
      buildingAnomalies: detectBuildingAnomalies(state.summary),
      autoWarning: state.activeRunWarning,
    });
  }
}

function realtimeFilterActive() {
  return !!(
    $('filterRealtimeMatch').value ||
    $('filterRealtimePower').value ||
    $('filterRealtimeMode').value ||
    $('filterRealtimeFan').value ||
    $('filterRealtimeLock').value ||
    $('filterRealtimeSystem').value ||
    $('filterRealtimePoints').value ||
    $('filterRealtimeModbus').value
  );
}

const SHORTCUT_STORAGE_KEY = 'ems.panel.data_shortcuts.v1';
const SHORTCUT_LIMIT = 10;
const QUICK_DIMENSION_KEYS = [
  'issue',
  'comm_state',
  'area',
  'temp_state',
  'realtime_match',
  'realtime_power',
  'realtime_mode_setting',
  'realtime_fan_setting',
  'realtime_lock',
  'realtime_system_type',
  'realtime_points',
  'realtime_modbus',
];
const FULL_FILTER_KEYS = [
  'building',
  'zuo',
  'floor',
  ...QUICK_DIMENSION_KEYS,
  'tag',
  'q',
  'groups',
];
const SELECT_FILTER_IDS = {
  issue: 'filterIssue',
  comm_state: 'filterCommState',
  area: 'filterAreaType',
  temp_state: 'filterTempState',
  realtime_match: 'filterRealtimeMatch',
  realtime_power: 'filterRealtimePower',
  realtime_mode_setting: 'filterRealtimeMode',
  realtime_fan_setting: 'filterRealtimeFan',
  realtime_lock: 'filterRealtimeLock',
  realtime_system_type: 'filterRealtimeSystem',
  realtime_points: 'filterRealtimePoints',
  realtime_modbus: 'filterRealtimeModbus',
  tag: 'filterTag',
  q: 'filterQ',
};
const DEFAULT_QUICK_FILTERS = [
  { key: 'all', label: '全部设备', meta: '当前范围', type: 'info', apply: {} },
  { key: 'normal', label: '正常设备', meta: '排除异常/离线', type: 'success', apply: { issue: 'exclude_abnormal' } },
  { key: 'needs_review', label: '需排查', meta: '异常 / 离线 / 不完整', type: 'warning', apply: { issue: 'needs_review' } },
  { key: 'public_on', label: '公区未关闭', meta: '公区开机', type: 'warning', apply: { area: 'public', realtime_power: '开机' } },
  { key: 'private_on', label: '户内未关闭', meta: '非公区开机', type: 'warning', apply: { area: 'private', realtime_power: '开机' } },
  { key: 'public_review', label: '公区需排查', meta: '公区异常/离线', type: 'warning', apply: { area: 'public', issue: 'needs_review' } },
  { key: 'private_offline', label: '户内离线', meta: '非公区通讯离线', type: 'muted', apply: { area: 'private', comm_state: '离线' } },
  { key: 'temp_abnormal', label: '温度异常', meta: '温度缺失/越界', type: 'danger', apply: { temp_state: 'abnormal' } },
  { key: 'points_incomplete', label: '点位不完整', meta: '实时点位缺失', type: 'warning', apply: { issue: 'points_incomplete' } },
  { key: 'locked', label: '集控锁定', meta: '锁定开启', type: 'locked', apply: { realtime_lock: '开启' } },
];
const EXTRA_QUICK_FILTERS = [
  { key: 'matched', label: '已纳管', meta: '实时详情可用', type: 'success', apply: { realtime_match: 'matched' }, inBar: false },
  { key: 'unmatched', label: '需人工分类', meta: '区域/座号待确认', type: 'warning', apply: { issue: 'unmatched' }, inBar: false },
  { key: 'offline', label: '离线', meta: '通讯异常', type: 'muted', apply: { comm_state: '离线' }, inBar: false },
  { key: 'realtime_unmatched', label: '需人工分类', meta: '区域/座号待确认', type: 'warning', apply: { issue: 'unmatched' }, inBar: false },
  { key: 'on', label: '开机设备', meta: '实时开机', type: 'info', apply: { realtime_power: '开机' }, inBar: false },
  { key: 'off', label: '关机设备', meta: '实时关机', type: 'success', apply: { realtime_power: '关机' }, inBar: false },
  { key: 'ignored', label: '已忽略', meta: '重复忽略', type: 'muted', apply: { realtime_match: 'ignored' }, inBar: false },
];
const QUICK_FILTERS = [...DEFAULT_QUICK_FILTERS, ...EXTRA_QUICK_FILTERS];

function compactObject(obj) {
  return Object.fromEntries(Object.entries(obj || {}).filter(([, value]) => String(value ?? '').trim()));
}

function sanitizeShortcutApply(apply) {
  const allowed = new Set(FULL_FILTER_KEYS);
  const clean = {};
  for (const [key, value] of Object.entries(apply || {})) {
    if (!allowed.has(key)) continue;
    const text = String(value ?? '').trim();
    if (text) clean[key] = text;
  }
  return clean;
}

function normalizeShortcut(item, fallback, index) {
  const base = fallback || DEFAULT_QUICK_FILTERS[index] || DEFAULT_QUICK_FILTERS[0];
  const label = String(item && item.label ? item.label : base.label).trim().slice(0, 16) || base.label;
  return {
    key: String(item && item.key ? item.key : base.key),
    label,
    meta: String(item && item.meta ? item.meta : base.meta || '快捷筛选').trim().slice(0, 22),
    type: String(item && item.type ? item.type : base.type || 'info'),
    apply: sanitizeShortcutApply(item && item.apply ? item.apply : base.apply),
    full: !!(item && item.full),
    custom: !!(item && item.custom),
  };
}

function loadSavedShortcuts() {
  try {
    const raw = JSON.parse(localStorage.getItem(SHORTCUT_STORAGE_KEY) || 'null');
    if (!Array.isArray(raw) || raw.length !== SHORTCUT_LIMIT) return null;
    return raw.map((item, index) => normalizeShortcut(item, DEFAULT_QUICK_FILTERS[index], index));
  } catch {
    return null;
  }
}

function visibleQuickShortcuts() {
  return loadSavedShortcuts() || DEFAULT_QUICK_FILTERS.map((item, index) => normalizeShortcut(item, item, index));
}

function saveShortcutConfig(shortcuts) {
  const normalized = shortcuts.slice(0, SHORTCUT_LIMIT).map((item, index) => normalizeShortcut(item, DEFAULT_QUICK_FILTERS[index], index));
  localStorage.setItem(SHORTCUT_STORAGE_KEY, JSON.stringify(normalized));
}

function findQuickFilter(key) {
  return [...visibleQuickShortcuts(), ...QUICK_FILTERS].find(x => x.key === key) || DEFAULT_QUICK_FILTERS[0];
}

function currentFilterValues() {
  return {
    building: filterValues('buildings').join(','),
    zuo: filterValues('zuos').join(','),
    floor: filterValues('floors').join(','),
    issue: $('filterIssue').value,
    comm_state: $('filterCommState').value,
    area: $('filterAreaType').value,
    temp_state: $('filterTempState').value,
    realtime_match: $('filterRealtimeMatch').value,
    realtime_power: $('filterRealtimePower').value,
    realtime_mode_setting: $('filterRealtimeMode').value,
    realtime_fan_setting: $('filterRealtimeFan').value,
    realtime_lock: $('filterRealtimeLock').value,
    realtime_system_type: $('filterRealtimeSystem').value,
    realtime_points: $('filterRealtimePoints').value,
    realtime_modbus: $('filterRealtimeModbus').value,
    tag: $('filterTag').value,
    q: $('filterQ').value,
    groups: selectedGroupIds().join(','),
  };
}

function filterValueForKey(key) {
  if (key === 'building') return filterValues('buildings').join(',');
  if (key === 'zuo') return filterValues('zuos').join(',');
  if (key === 'floor') return filterValues('floors').join(',');
  if (key === 'groups') return selectedGroupIds().join(',');
  const id = SELECT_FILTER_IDS[key];
  return id && $(id) ? $(id).value : '';
}

function setFilterValueForKey(key, value) {
  const text = String(value || '');
  if (key === 'building') return setFilterValues('buildings', text ? text.split(',') : []);
  if (key === 'zuo') return setFilterValues('zuos', text ? text.split(',') : []);
  if (key === 'floor') return setFilterValues('floors', text ? text.split(',') : []);
  if (key === 'groups') {
    state.selectedAreaIds = text ? text.split(',').filter(Boolean) : [];
    renderAreaFilter();
    return;
  }
  const id = SELECT_FILTER_IDS[key];
  if (id) setSelectValueIfExists(id, text);
}

function applyShortcutFilters(item) {
  const apply = item.apply || {};
  const keys = item.full ? FULL_FILTER_KEYS : QUICK_DIMENSION_KEYS;
  keys.forEach(key => setFilterValueForKey(key, apply[key] || ''));
  renderRangeFilters();
  syncFilterActiveStates();
}

function shortcutMatchesCurrent(item) {
  const apply = item.apply || {};
  const keys = item.full ? FULL_FILTER_KEYS : QUICK_DIMENSION_KEYS;
  return keys.every(key => String(filterValueForKey(key) || '') === String(apply[key] || ''));
}

async function saveCurrentShortcut() {
  const payload = compactObject(currentFilterValues());
  if (!Object.keys(payload).length) {
    window.alert('当前没有可保存的筛选条件。');
    return;
  }
  const label = window.prompt('快捷筛选名称（最多 16 个字）', '自定义筛选');
  if (!label) return;
  const slotRaw = window.prompt('替换第几个快捷卡片？请输入 1-10', '10');
  const slot = Math.min(SHORTCUT_LIMIT, Math.max(1, Number(slotRaw) || SHORTCUT_LIMIT));
  const shortcuts = visibleQuickShortcuts();
  shortcuts[slot - 1] = {
    key: `custom_${Date.now()}`,
    label,
    meta: '自定义',
    type: 'info',
    apply: payload,
    full: true,
    custom: true,
  };
  saveShortcutConfig(shortcuts);
  renderDeviceWorkbench();
}

async function resetShortcutConfig() {
  if (!window.confirm('恢复默认 10 个快捷筛选？')) return;
  localStorage.removeItem(SHORTCUT_STORAGE_KEY);
  state.quickFilter = 'all';
  await loadCardsFirstPage();
}

function setSelectValueIfExists(id, value) {
  const el = $(id);
  if (!el) return;
  const text = String(value || '');
  if (text && el.tagName === 'SELECT' && !Array.from(el.options).some(o => o.value === text)) {
    el.appendChild(new Option(text, text));
  }
  el.value = text;
  syncFilterActiveState(el);
}

async function applyQuickFilter(key) {
  const item = findQuickFilter(key);
  state.quickFilter = item.key;
  applyShortcutFilters(item);
  state.cardPageOffset = 0;
  await loadCards();
}

async function jumpToDataWithFilter(filter = {}) {
  if (filter.building) setFilterValues('buildings', [filter.building]);
  if (filter.quick) {
    const item = findQuickFilter(filter.quick);
    state.quickFilter = item.key;
    applyShortcutFilters(item);
  }
  renderRangeFilters();
  state.cardPageOffset = 0;
  switchView('data');
}

function resetQuickFilterIfManual() {
  const active = findQuickFilter(state.quickFilter);
  if (!active || active.key === 'all') return;
  if (!shortcutMatchesCurrent(active)) state.quickFilter = 'all';
}

function syncFilterActiveState(el) {
  if (!el) return;
  if (el.type === 'hidden') return;
  const value = String(el.value || '').trim();
  el.classList.toggle('has-value', !!value);
}

function syncFilterActiveStates() {
  document.querySelectorAll('.compact-filters select, .compact-filters input:not([type="checkbox"]):not([type="hidden"])').forEach(syncFilterActiveState);
  resetQuickFilterIfManual();
  syncClearFiltersButton();
}

function hasActiveDataFilters() {
  return !!(
    filterValues('buildings').length ||
    filterValues('zuos').length ||
    filterValues('floors').length ||
    state.selectedAreaIds.length ||
    $('filterIssue').value ||
    $('filterCommState').value ||
    $('filterAreaType').value ||
    $('filterTempState').value ||
    $('filterRealtimeMatch').value ||
    $('filterRealtimePower').value ||
    $('filterRealtimeMode').value ||
    $('filterRealtimeFan').value ||
    $('filterRealtimeLock').value ||
    $('filterRealtimeSystem').value ||
    $('filterRealtimePoints').value ||
    $('filterRealtimeModbus').value ||
    $('filterTag').value ||
    $('filterQ').value
  );
}

function syncClearFiltersButton() {
  const btn = $('clearFiltersBtn');
  if (!btn) return;
  btn.hidden = !hasActiveDataFilters();
}

async function clearDataFilters() {
  setFilterValues('buildings', []);
  setFilterValues('zuos', []);
  setFilterValues('floors', []);
  state.selectedAreaIds = [];
  state.quickFilter = 'all';
  [
    'filterIssue',
    'filterCommState',
    'filterAreaType',
    'filterTempState',
    'filterRealtimeMatch',
    'filterRealtimePower',
    'filterRealtimeMode',
    'filterRealtimeFan',
    'filterRealtimeLock',
    'filterRealtimeSystem',
    'filterRealtimePoints',
    'filterRealtimeModbus',
    'filterTag',
    'filterQ',
  ].forEach(id => { if ($(id)) $(id).value = ''; });
  renderAreaFilter();
  await loadFilterOptions();
  await loadRealtimeCoverage();
  await loadFloors();
  await loadCardsFirstPage();
}

function realtimeField(row, name) {
  return row && row.realtime && row.realtime.fields ? (row.realtime.fields[name] || '-') : '-';
}

function renderRealtimeCoverageBadge() {
  const el = $('realtimeCoverageBadge');
  if (!el) return;
  const c = state.realtimeCoverage || {};
  const realtimeRows = Number(c.realtime_rows || 0);
  const unmatched = Number(c.realtime_unmatched || 0);
  const review = Number(c.needs_review || 0);
  if (!realtimeRows) {
    el.textContent = '实时详情 --';
    el.className = 'badge';
    return;
  }
  el.textContent = `实时详情 ${realtimeRows}，需排查 ${review}`;
  el.title = `实时详情数据 ${realtimeRows}；需排查 ${review}；需人工分类 ${unmatched}`;
  el.className = 'badge ' + (review ? 'warning' : 'success');
}

function renderFilterOptions() {
  const currentIssue = $('filterIssue').value;
  const currentCommState = $('filterCommState').value;
  const currentAreaType = $('filterAreaType').value;
  const currentTempState = $('filterTempState').value;
  const currentRealtimePower = $('filterRealtimePower').value;
  const currentRealtimeMode = $('filterRealtimeMode').value;
  const currentRealtimeFan = $('filterRealtimeFan').value;
  const currentRealtimeLock = $('filterRealtimeLock').value;
  const currentRealtimeSystem = $('filterRealtimeSystem').value;
  const issueOptions = state.filterOptions.issues || [];
  const commStateOptions = state.filterOptions.comm_states || [];
  const areaTypeOptions = state.filterOptions.area_types || [];
  const tempStateOptions = state.filterOptions.temp_states || [];
  const realtimePower = state.filterOptions.realtime_power || [];
  const realtimeModes = state.filterOptions.realtime_mode_settings || [];
  const realtimeFans = state.filterOptions.realtime_fan_settings || [];
  const realtimeLocks = state.filterOptions.realtime_locks || [];
  const realtimeSystems = state.filterOptions.realtime_system_types || [];
  const optionHtml = options => options.map(o => `
    <option value="${esc(o.value)}">${esc(o.label)} (${esc(o.count)})</option>
  `).join('');
  const realtimeLockLabel = value => {
    if (value === '关闭') return '集控锁定：未开';
    if (value === '开启') return '集控锁定：已开';
    if (value === '__abnormal') return '集控锁定：异常';
    return `集控异常：${value}`;
  };
  const realtimeLockOptions = realtimeLocks.map(o => ({
    ...o,
    label: realtimeLockLabel(o.value),
  }));
  $('filterIssue').innerHTML = '<option value="">健康/排查</option>' + optionHtml(issueOptions);
  $('filterCommState').innerHTML = '<option value="">通讯</option>' + optionHtml(commStateOptions);
  $('filterAreaType').innerHTML = '<option value="">区域</option>' + optionHtml(areaTypeOptions);
  $('filterTempState').innerHTML = '<option value="">温度</option>' + optionHtml(tempStateOptions);
  $('filterRealtimePower').innerHTML = '<option value="">开关</option>' + optionHtml(realtimePower);
  $('filterRealtimeMode').innerHTML = '<option value="">模式</option>' + optionHtml(realtimeModes);
  $('filterRealtimeFan').innerHTML = '<option value="">风速</option>' + optionHtml(realtimeFans);
  $('filterRealtimeLock').innerHTML = '<option value="">集控</option>' + optionHtml(realtimeLockOptions);
  $('filterRealtimeSystem').innerHTML = '<option value="">系统</option>' + optionHtml(realtimeSystems);
  if (issueOptions.some(o => String(o.value) === String(currentIssue))) $('filterIssue').value = currentIssue;
  if (commStateOptions.some(o => String(o.value) === String(currentCommState))) $('filterCommState').value = currentCommState;
  if (areaTypeOptions.some(o => String(o.value) === String(currentAreaType))) $('filterAreaType').value = currentAreaType;
  if (tempStateOptions.some(o => String(o.value) === String(currentTempState))) $('filterTempState').value = currentTempState;
  if (realtimePower.some(o => String(o.value) === String(currentRealtimePower))) $('filterRealtimePower').value = currentRealtimePower;
  if (realtimeModes.some(o => String(o.value) === String(currentRealtimeMode))) $('filterRealtimeMode').value = currentRealtimeMode;
  if (realtimeFans.some(o => String(o.value) === String(currentRealtimeFan))) $('filterRealtimeFan').value = currentRealtimeFan;
  if (realtimeLocks.some(o => String(o.value) === String(currentRealtimeLock))) $('filterRealtimeLock').value = currentRealtimeLock;
  if (realtimeSystems.some(o => String(o.value) === String(currentRealtimeSystem))) $('filterRealtimeSystem').value = currentRealtimeSystem;
  renderRangeFilters();
  syncFilterActiveStates();
}

async function loadFloors() {
  const params = new URLSearchParams();
  if (state.runId) params.set('run_id', state.runId);
  const data = await api('/api/floors?' + params.toString());
  state.floors = data || { catalog: [], discovered: [] };
  renderFloorOptions();
}

function floorOptionsFor(building) {
  const map = new Map();
  for (const f of state.floors.catalog || []) {
    if (!building || f.building === building) map.set(`${f.building}:${f.floor_label}`, f);
  }
  for (const f of state.floors.discovered || []) {
    if (!building || f.building === building) map.set(`${f.building}:${f.floor_label}`, f);
  }
  return Array.from(map.values()).sort((a, b) => {
    if (a.building !== b.building) return BUILDINGS.indexOf(a.building) - BUILDINGS.indexOf(b.building);
    return Number(a.floor_value) - Number(b.floor_value);
  });
}

function renderFloorOptions() {
  const monitorBuilding = $('monitorBuilding').value;
  const monitorFloors = floorOptionsFor(monitorBuilding);
  $('monitorFloor').innerHTML = monitorFloors.map(f => `
    <option value="${esc(f.floor_label)}">${esc(f.floor_label)}${f.source ? ' · ' + esc(f.source) : ''}</option>
  `).join('') || '<option value="">请先新增楼层目录</option>';
  renderRangeFilters();
}

async function addFloorCatalog() {
  const building = $('monitorBuilding').value;
  const floor = window.prompt('输入要加入目录的楼层，例如 18F、B1F、2.5F');
  if (!floor) return;
  await api('/api/floors', { method: 'POST', body: { building, floor_label: floor, note: $('monitorNote').value || '人工新增楼层目录' } });
  await loadFloors();
  $('monitorFloor').value = normalizeFloorLabel(floor);
}

function renderCardsPager(data) {
  const total = Number(data.total || 0);
  const shown = Array.isArray(data.rows) ? data.rows.length : 0;
  if (total > 0 && state.cardPageOffset >= total) {
    state.cardPageOffset = Math.floor((total - 1) / state.cardPageSize) * state.cardPageSize;
    loadCards();
    return;
  }
  const start = shown ? state.cardPageOffset + 1 : 0;
  const end = shown ? state.cardPageOffset + shown : 0;
  const pageCount = Math.max(1, Math.ceil(total / state.cardPageSize));
  const pageNo = shown ? Math.floor(state.cardPageOffset / state.cardPageSize) + 1 : 1;
  $('cardCount').textContent = `共 ${total} 条，显示 ${start}-${end}`;
  $('cardPageInfo').textContent = `${pageNo} / ${pageCount}`;
  $('prevCardsPage').disabled = state.cardPageOffset <= 0;
  $('nextCardsPage').disabled = state.cardPageOffset + state.cardPageSize >= total;
  $('cardPageSize').value = String(state.cardPageSize);
}

function cardRowsForStats() {
  return Array.isArray(state.cards.rows) ? state.cards.rows : [];
}

function countRows(predicate) {
  return cardRowsForStats().reduce((sum, row) => sum + (predicate(row) ? 1 : 0), 0);
}

function shortcutCountParams(item) {
  const params = new URLSearchParams();
  const apply = item.apply || {};
  const source = item.full ? apply : { ...compactObject(currentFilterValues()), ...apply };
  for (const key of FULL_FILTER_KEYS) {
    const value = source[key];
    if (value) params.set(key, value);
  }
  if (state.runId) params.set('run_id', state.runId);
  params.set('include_realtime', '1');
  params.set('limit', '1');
  return params;
}

async function hydrateCustomShortcutCounts(items, requestSeq) {
  const customItems = items.filter(item => item.custom && !Number.isFinite(Number(state.shortcutCountCache[item.key])));
  if (!customItems.length) return;
  const entries = await Promise.all(customItems.map(async item => {
    try {
      const data = await api('/api/cards?' + shortcutCountParams(item).toString());
      return [item.key, Number(data.total || 0)];
    } catch {
      return [item.key, null];
    }
  }));
  if (requestSeq !== state.cardRequestSeq) return;
  entries.forEach(([key, value]) => {
    if (value !== null) state.shortcutCountCache[key] = value;
  });
  renderDeviceWorkbench();
}

function renderDeviceWorkbench() {
  const root = $('deviceWorkbench');
  if (!root) return;
  const counts = state.cards.shortcut_counts || {};
  const displayCards = visibleQuickShortcuts().slice(0, SHORTCUT_LIMIT).map(item => ({
    ...item,
    value: Number.isFinite(Number(counts[item.key]))
      ? Number(counts[item.key])
      : Number.isFinite(Number(state.shortcutCountCache[item.key]))
        ? Number(state.shortcutCountCache[item.key])
        : '--',
  }));
  root.innerHTML = `
    <div class="shortcut-toolbar">
      <span>快捷筛选 · 数量为点击后结果</span>
      <div>
        <button type="button" class="ghost small" data-save-shortcut>保存当前筛选</button>
        <button type="button" class="ghost small" data-reset-shortcuts>恢复默认</button>
      </div>
    </div>
    <div class="workbench-summary">
      ${displayCards.map(card => `
        <button type="button" class="workbench-card ${state.quickFilter === card.key ? 'active' : ''} ${esc(card.type || 'muted')}" data-quick-filter="${esc(card.key)}">
          <span>${esc(card.label)}</span>
          <strong>${esc(card.value ?? '--')}</strong>
          <em>${esc(card.meta || '当前筛选')}</em>
        </button>
      `).join('')}
    </div>
  `;
  root.querySelectorAll('[data-quick-filter]').forEach(btn => {
    btn.addEventListener('click', () => applyQuickFilter(btn.dataset.quickFilter));
  });
  root.querySelector('[data-save-shortcut]')?.addEventListener('click', saveCurrentShortcut);
  root.querySelector('[data-reset-shortcuts]')?.addEventListener('click', resetShortcutConfig);
  hydrateCustomShortcutCounts(displayCards, state.cardRequestSeq);
}

async function loadCards() {
  const requestSeq = ++state.cardRequestSeq;
  const params = new URLSearchParams();
  const mapping = [
    ['building', filterValues('buildings').join(',')],
    ['zuo', filterValues('zuos').join(',')],
    ['floor', filterValues('floors').join(',')],
    ['issue', $('filterIssue').value],
    ['comm_state', $('filterCommState').value],
    ['area', $('filterAreaType').value],
    ['temp_state', $('filterTempState').value],
    ['realtime_match', $('filterRealtimeMatch').value],
    ['realtime_power', $('filterRealtimePower').value],
    ['realtime_mode_setting', $('filterRealtimeMode').value],
    ['realtime_fan_setting', $('filterRealtimeFan').value],
    ['realtime_lock', $('filterRealtimeLock').value],
    ['realtime_system_type', $('filterRealtimeSystem').value],
    ['realtime_points', $('filterRealtimePoints').value],
    ['realtime_modbus', $('filterRealtimeModbus').value],
    ['tag', $('filterTag').value],
    ['q', $('filterQ').value],
  ];
  for (const [k, v] of mapping) if (v) params.set(k, v);
  syncFilterActiveStates();
  const groups = selectedGroupIds();
  if (groups.length) params.set('groups', groups.join(','));
  if (state.runId) params.set('run_id', state.runId);
  params.set('include_realtime', '1');
  params.set('offset', String(state.cardPageOffset));
  params.set('limit', String(state.cardPageSize));
  const data = await api('/api/cards?' + params.toString());
  if (requestSeq !== state.cardRequestSeq) return;
  state.cards = data;
  state.shortcutCountCache = {};
  renderCardsPager(data);
  renderRealtimeCoverageBadge();
  renderDeviceWorkbench();
  $('cardHeaderRow').innerHTML = `
    <th>旧 Web 数据表已停用</th>
  `;
  $('cardRows').innerHTML = `
    <tr>
      <td class="muted">
        当前唯一面向用户的数据导出入口是原生应用“数据管理 -> 导出当前筛选 Excel”。旧 Web 面板不再展示历史数据表。
      </td>
    </tr>
  `;
}

function selectedGroupIds() {
  return state.selectedAreaIds.map(String).filter(Boolean);
}

function parseActionData(value) {
  try {
    return JSON.parse(value || '{}');
  } catch {
    return {};
  }
}

function bindCardActions() {
  document.querySelectorAll('[data-device-detail]').forEach(btn => {
    btn.addEventListener('click', () => {
      const index = Number(btn.dataset.deviceDetail);
      const row = state.cards.rows && state.cards.rows[index];
      if (row) openDeviceDetail(row);
    });
  });
  document.querySelectorAll('[data-tag-select]').forEach(sel => {
    sel.addEventListener('change', async () => {
      const tag = sel.value;
      if (!tag) return;
      const key = parseActionData(sel.dataset.tagSelect);
      await api('/api/device/tag', { method: 'POST', body: { building: key.building, card_name: key.name, tag } });
      await loadCards();
    });
  });
  document.querySelectorAll('[data-note-card]').forEach(btn => {
    btn.addEventListener('click', async () => {
      const key = parseActionData(btn.dataset.noteCard);
      const note = window.prompt('设备备注', key.note || '');
      if (note === null) return;
      await api('/api/device/note', { method: 'POST', body: { building: key.building, card_name: key.name, note } });
      await loadCards();
    });
  });
  document.querySelectorAll('[data-realtime-zuo]').forEach(sel => {
    sel.addEventListener('change', async () => {
      if (!sel.value) return;
      const key = parseActionData(sel.dataset.realtimeZuo);
      await api('/api/realtime-match/override', {
        method: 'POST',
        body: {
          ...key,
          zuo_override: sel.value === '__clear' ? '' : sel.value,
        },
      });
      await loadFilterOptions();
      await loadCards();
    });
  });
  document.querySelectorAll('[data-realtime-area]').forEach(sel => {
    sel.addEventListener('change', async () => {
      if (!sel.value) return;
      const key = parseActionData(sel.dataset.realtimeArea);
      await api('/api/realtime-match/override', {
        method: 'POST',
        body: {
          ...key,
          area_type_override: sel.value === '__clear' ? '' : sel.value,
        },
      });
      await loadFilterOptions();
      await loadCards();
    });
  });
  document.querySelectorAll('[data-realtime-ignore]').forEach(btn => {
    btn.addEventListener('click', async () => {
      const key = parseActionData(btn.dataset.realtimeIgnore);
      await api('/api/realtime-match/override', {
        method: 'POST',
        body: {
          ...key,
          action: key.ignored ? 'classify_only' : 'ignore_duplicate',
        },
      });
      await loadFilterOptions();
      await loadCards();
    });
  });
}

function openDeviceDetail(row) {
  const modal = $('deviceDetailModal');
  const content = $('deviceDetailContent');
  if (!modal || !content) return;
  content.innerHTML = DeviceTable.renderDetail(row);
  modal.hidden = false;
  document.body.classList.add('detail-open');
}

function closeDeviceDetail() {
  const modal = $('deviceDetailModal');
  if (!modal) return;
  modal.hidden = true;
  $('deviceDetailContent').innerHTML = '';
  document.body.classList.remove('detail-open');
}

async function loadQuality() {
  const qs = state.runId ? '?run_id=' + encodeURIComponent(state.runId) : '';
  const q = await api('/api/quality' + qs);
  if (!q) {
    $('qualityContent').innerHTML = '<div class="quality-card"><strong>未生成质量审计</strong><span>运行质量审计任务后显示结果。</span></div>';
    renderRunRows();
    return;
  }
  const s = q.summary || {};
  const conclusion = qualityConclusion(q);
  const metrics = q.kind === 'realtime'
    ? [
        ['实时设备数', s.total_cards],
        ['采集错误', s.collection_errors],
        ['设备异常行', s.device_anomaly_rows],
        ['异常事件', s.device_anomaly_events],
        ['有效点位异常', s.invalid_realtime_tags],
        ['枚举异常', s.invalid_enum],
        ['数值越界', s.out_of_range],
        ['集控字段异常', s.invalid_lock],
      ]
    : [
        ['设备总数', s.total_cards],
        ['问题类型', s.issue_count],
        ['占位符卡', s.placeholder_cards],
        ['状态冲突', s.state_mismatch],
        ['未知通讯', s.unknown_comm],
        ['缺 indicator', s.missing_indicator],
        ['重复渲染页', s.duplicate_rendered_pages],
        ['空子区', s.empty_sub_areas],
      ];
  const groups = ['P1', 'P2', 'INFO'].map(sev => ({
    sev,
    issues: (q.issues || []).filter(i => String(i.severity || '').toUpperCase() === sev || (sev === 'P2' && String(i.severity || '').toUpperCase() === 'P3')),
  }));
  const sampleRows = sampleRowsForQuality(q);
  const issueGroupsHtml = groups.map(group => `
    <div class="quality-section">
      <div class="quality-section-head">
        <strong>${esc(group.sev === 'INFO' ? '信息项' : group.sev + ' 问题')}</strong>
        <span class="badge ${badgeClass(group.sev)}">${esc(group.issues.length)} 类</span>
      </div>
      ${group.issues.length ? group.issues.map(issue => `
        <div class="quality-issue">
          <strong><span class="badge ${badgeClass(issue.severity)}">${esc(issue.severity)}</span> ${esc(qualityIssueTitle(issue.code))}：${esc(issue.count)}</strong>
          <span>${esc(issue.message)}</span>
          <em>${esc(qualityAdvice(issue.code, issue.severity))}</em>
        </div>
      `).join('') : '<div class="quality-issue muted">无</div>'}
    </div>
  `).join('');
  const samplesHtml = `
    <div class="quality-section quality-samples">
      <div class="quality-section-head">
        <strong>问题明细样例</strong>
        <span class="badge">${esc(sampleRows.length)} 条</span>
      </div>
      <div class="table-wrap compact">
        <table>
          <thead><tr><th>严重度</th><th>问题</th><th>位置</th><th>对象</th><th>建议</th></tr></thead>
          <tbody>
            ${sampleRows.map(row => `
              <tr>
                <td><span class="badge ${badgeClass(row.issue.severity)}">${esc(row.issue.severity)}</span></td>
                <td>${esc(qualityIssueTitle(row.issue.code))}</td>
                <td>${esc(sampleLocation(row.item))}</td>
                <td>${esc(sampleObject(row.item))}</td>
                <td>${esc(qualityAdvice(row.issue.code, row.issue.severity))}</td>
              </tr>
            `).join('') || '<tr><td colspan="5" class="muted">暂无样例</td></tr>'}
          </tbody>
        </table>
      </div>
    </div>
  `;
  $('qualityContent').innerHTML = `
    <div class="quality-hero">
      <div>
        <span class="badge ${conclusion.cls}">${esc(conclusion.text)}</span>
        <strong>${esc(q.run ? (q.run.completed_at || q.run.run_key) : activeRunLabel())}</strong>
        <span>${esc(conclusion.detail)} 生成时间：${esc(q.generated_at_local || q.generated_at || '-')}</span>
      </div>
      <button class="ghost small" data-task-kind="quality">重新审计</button>
    </div>
    <div class="quality-metrics">
      ${metrics.map(([label, value]) => `
        <div class="quality-card">
          <strong>${esc(value ?? 0)}</strong>
          <span>${esc(label)}</span>
        </div>
      `).join('')}
    </div>
    <div class="quality-groups">${issueGroupsHtml}</div>
    ${samplesHtml}
  `;
  $('qualityContent').querySelectorAll('[data-task-kind]').forEach(btn => btn.addEventListener('click', () => startTask(btn.dataset.taskKind)));
  renderRunRows();
}

function renderRunRows() {
  const tbody = $('runRows');
  if (!tbody) return;
  const rows = state.runs || [];
  tbody.innerHTML = rows.map(r => {
    const status = runStatus(r);
    const quality = runQualityLabel(r);
    const active = state.runId && String(state.runId) === String(r.id);
    const noteParts = [];
    if (r.anomaly_reason) noteParts.push(r.anomaly_reason);
    if (r.note) noteParts.push(r.note);
    if (Number.isFinite(Number(r.card_delta || 0)) && Number(r.card_delta || 0) !== 0) {
      noteParts.push(`基线差异 ${Number(r.card_delta) > 0 ? '+' : ''}${r.card_delta}`);
    }
    return `
      <tr class="${status.cls === 'danger' ? 'row-danger' : status.cls === 'warning' ? 'row-warn' : ''}">
        <td>
          <strong>${esc(r.label || r.run_key)}</strong>
          ${active ? '<span class="tag">当前筛选</span>' : ''}
        </td>
        <td>${esc(r.card_count || 0)} / ${esc(r.expected_card_count || '-')}</td>
        <td><span class="badge ${status.cls}">${esc(status.text)}</span></td>
        <td><span class="badge ${quality.cls}">${esc(quality.text)}</span></td>
        <td>${esc(noteParts.join('；') || '-')}</td>
        <td>
          <div class="row-actions wide">
            <button class="ghost small" data-run-anomaly="${r.id}" data-next="${Number(r.is_anomaly || 0) ? 0 : 1}">
              ${Number(r.is_anomaly || 0) ? '取消隔离' : '隔离'}
            </button>
            <button class="ghost small" data-run-restore="${r.id}">恢复当前</button>
            <button class="ghost small" data-run-quality="${r.id}">审计</button>
            <button class="danger-btn small" data-run-delete="${r.id}">删除</button>
          </div>
        </td>
      </tr>
    `;
  }).join('') || '<tr><td colspan="6" class="muted">暂无采集批次</td></tr>';
  bindRunActions();
}

function bindRunActions() {
  document.querySelectorAll('[data-run-anomaly]').forEach(btn => {
    btn.addEventListener('click', async () => {
      const id = btn.dataset.runAnomaly;
      const next = btn.dataset.next !== '0';
      const note = next ? window.prompt('隔离原因', '采集数据异常，已隔离') : '';
      if (next && note === null) return;
      await api(`/api/runs/${id}/anomaly`, { method: 'POST', body: { is_anomaly: next, note: note || '' } });
      await refreshAfterRunChange();
    });
  });
  document.querySelectorAll('[data-run-restore]').forEach(btn => {
    btn.addEventListener('click', async () => {
      const id = btn.dataset.runRestore;
      const run = (state.runs || []).find(r => String(r.id) === String(id));
      const label = run ? (run.label || run.run_key) : `run ${id}`;
      if (!window.confirm(`确定要把当前数据库恢复为这个批次吗？\n${label}\n\n该操作会替换当前数据管理和总览使用的当前数据。`)) return;
      await api(`/api/runs/${id}/restore`, { method: 'POST' });
      state.runId = '';
      state.manualRunSelection = false;
      await refreshAll();
      if (state.view === 'data') await loadCards();
    });
  });
  document.querySelectorAll('[data-run-quality]').forEach(btn => {
    btn.addEventListener('click', async () => {
      const previous = state.runId;
      state.runId = String(btn.dataset.runQuality);
      await startTask('quality');
      state.runId = previous;
    });
  });
  document.querySelectorAll('[data-run-delete]').forEach(btn => {
    btn.addEventListener('click', async () => {
      const id = btn.dataset.runDelete;
      const run = (state.runs || []).find(r => String(r.id) === String(id));
      const label = run ? (run.label || run.run_key) : `run ${id}`;
      const confirmText = window.prompt(`删除批次不可恢复：\n${label}\n\n请输入“确认删除”继续。`);
      if (confirmText === null) return;
      if (confirmText.trim() !== '确认删除') {
        window.alert('确认文字不匹配，已取消删除。');
        return;
      }
      await api(`/api/runs/${id}`, { method: 'DELETE', body: { confirm: confirmText.trim() } });
      if (String(state.runId) === String(id)) {
        state.runId = '';
        state.manualRunSelection = false;
      }
      await refreshAll();
    });
  });
}

async function refreshAfterRunChange() {
  await loadRuns();
  await Promise.allSettled([loadSummary(), loadQuality()]);
  if (state.view === 'data') await loadCards();
}

async function loadReports() {
  const data = await api('/api/about');
  const current = data.current_data || {};
  $('helpDoc').innerHTML = `
    <div class="doc-list">
      <p><strong>当前数据口径：</strong>数据管理默认使用最新详情采集结果，当前为 ${esc(current.total || '--')} 条。</p>
      <p><strong>历史兼容：</strong>SQLite 仍保留备注、标签、区域组和历史批次信息，当前历史基表为 ${esc(current.db_total || '--')} 条。</p>
      <p><strong>右上角数据选择：</strong>“当前数据”表示最新详情采集结果；历史批次仅用于兼容查看和批次管理。</p>
      <p><strong>快捷筛选：</strong>顶部 10 个快捷项显示“点击后结果数”，可在数据管理中保存当前筛选为本机快捷项。</p>
      <p><strong>导出：</strong>当前唯一面向用户的导出入口是原生应用“数据管理 -> 导出当前筛选 Excel”。</p>
    </div>
  `;
  $('aboutInfo').innerHTML = `
    <div class="about-kv"><span>工具</span><strong>${esc(data.name || 'EMS HVAC 控制台')}</strong></div>
    <div class="about-kv"><span>工作目录</span><strong>${esc(data.root || '-')}</strong></div>
    <div class="about-kv"><span>数据库</span><strong>${esc(data.db_path || '-')}</strong></div>
    <div class="about-kv"><span>端口</span><strong>${esc(data.port || '-')}</strong></div>
    <div class="about-kv"><span>技术栈</span><strong>${esc((data.tech_stack || []).join(' / '))}</strong></div>
    <div class="about-kv"><span>详情/历史</span><strong>${esc(current.detail_total || '--')} / ${esc(current.db_total || '--')}</strong></div>
  `;
  const docs = data.docs || {};
  $('changeLogPreview').textContent = [
    '# 当前面板修改摘要',
    '- 数据管理和总览默认使用详情采集结果作为唯一业务口径。',
    '- 设备总数按详情文件统计，当前应为 6575 条。',
    '- 旧 Web 数据表已停用；请在原生应用数据管理页查看 12 字段表格。',
    '- 质量审计默认读取详情质量报告，展示采集错误、点位、枚举和温度类排查项。',
    '',
    '# 导出入口',
    '当前唯一面向用户的导出入口是原生应用“数据管理 -> 导出当前筛选 Excel”。',
  ].join('\n');
}

function selectedBuildings() {
  const root = $('buildingChecks');
  if (!root) return state.taskBuildings || [];
  const selected = Array.from(root.querySelectorAll('input:checked')).map(i => i.value);
  state.taskBuildings = selected;
  return selected;
}

function setAllBuildings(checked) {
  $('buildingChecks').querySelectorAll('input').forEach(input => {
    input.checked = checked;
    input.closest('.building-card').classList.toggle('selected', checked);
  });
  state.taskBuildings = checked ? [...BUILDINGS] : [];
  updateBuildingSelectionLabel();
}

function updateBuildingSelectionLabel() {
  const selected = selectedBuildings();
  const label = selected.length === BUILDINGS.length
    ? '已选全部 6 栋'
    : `已选 ${selected.length} 栋`;
  $('buildingSelectedCount').textContent = label;
  state.taskPreflight = null;
  renderTaskPreflight();
}

async function loadTaskPreflight(force = false) {
  if (!$('taskPreflight')) return null;
  if (!force && state.taskPreflight && state.taskPreflight.summary && state.taskPreflight.summary.checkedAt) {
    renderTaskPreflight();
    return state.taskPreflight;
  }
  const seq = ++state.taskPreflightRequestSeq;
  state.taskPreflightLoading = true;
  $('taskPreflightSummary').textContent = '检查中...';
  try {
    const data = await api('/api/tasks/preflight');
    if (seq !== state.taskPreflightRequestSeq) return state.taskPreflight;
    state.taskPreflight = data;
    state.taskPreflightLoading = false;
    renderTaskPreflight();
    renderTaskStatusCards(state.currentTask);
    return data;
  } catch (err) {
    if (seq !== state.taskPreflightRequestSeq) return state.taskPreflight;
    state.taskPreflight = {
      ok: false,
      checks: [{
        key: 'preflight_api',
        label: '运行前检查',
        status: 'error',
        message: err.message || String(err),
        suggestion: '请确认面板服务仍在运行，然后刷新页面重试。',
      }],
      summary: { canStart: false, blockingCount: 1, warningCount: 0, checkedAt: new Date().toISOString() },
    };
    state.taskPreflightLoading = false;
    renderTaskPreflight();
    return state.taskPreflight;
  }
}

function openEmsPage() {
  const emsCheck = state.taskPreflight && (state.taskPreflight.checks || []).find(c => c.key === 'ems_url');
  const url = emsCheck && emsCheck.detail ? emsCheck.detail : 'http://172.29.248.4:8000/ui';
  window.open(url, '_blank', 'noopener');
}

function scrollTaskLogIntoView() {
  const detail = document.querySelector('.task-runtime-panel .log-details');
  if (detail) detail.open = true;
  const logEl = $('taskLog');
  if (logEl) logEl.scrollIntoView({ behavior: 'smooth', block: 'center' });
}

function isCollectionTask(kind) {
  return kind === 'collectImport' || kind === 'collectSafe' || kind === 'enumerate';
}

function isRealtimeTask(kind) {
  return kind === 'realtimeDetails';
}

function updateTaskOptions() {
  const kind = currentTaskKind();
  const collection = isCollectionTask(kind);
  const realtime = isRealtimeTask(kind);
  const fullRealtime = realtime;
  const validateOnly = kind === 'validate';
  const importTask = kind === 'import';
  const collectionControls = ['captureMode', 'logLevel', 'logCategory', 'recapture'];
  for (const id of collectionControls) $(id).disabled = !(collection || fullRealtime);
  const realtimeControls = ['realtimeBatchSize', 'realtimeReopenEvery', 'realtimeTimeout', 'realtimeMaxDevices', 'realtimeRefreshInventory', 'realtimeSkipInventory'];
  for (const id of realtimeControls) $(id).disabled = !realtime;
  $('selfDiagnose').disabled = !(collection || fullRealtime);
  $('noNetMonitor').disabled = !(collection || fullRealtime);
  $('logFile').disabled = !(collection || fullRealtime || validateOnly || importTask);
  $('append').disabled = !(collection || fullRealtime || importTask);
  const meta = TASK_KIND_META[kind] || TASK_KIND_META.collectImport;
  $('taskPlanBadge').textContent = meta.badge;
  $('startTaskBtn').textContent = meta.action;
  renderTaskPreflight();
  if (!state.currentTask || !['running', 'stopping'].includes(state.currentTask.status)) {
    $('taskSteps').innerHTML = plannedStepsForCurrentKind().map(s => renderStep(s)).join('');
  }
}

function preflightRows() {
  const selected = selectedBuildings();
  const expected = expectedCardCount(selected);
  const rows = [{
    status: selected.length ? 'ok' : 'error',
    label: '采集范围',
    message: selected.length ? `${selected.length} 栋，预计 ${expected} 台` : '未选择楼栋',
    suggestion: selected.length ? '' : '请至少选择一栋楼后再开始采集。',
  }];
  const remoteChecks = state.taskPreflight && Array.isArray(state.taskPreflight.checks)
    ? state.taskPreflight.checks
    : [];
  rows.push(...remoteChecks);
  if (!remoteChecks.length) {
    rows.push({
      status: 'unknown',
      label: '环境检查',
      message: '等待后端预检结果',
      suggestion: '点击“重新检测环境”刷新。',
    });
  }
  return rows;
}

function renderTaskPreflight() {
  if (!$('taskPreflight')) return;
  if (state.taskPreflightLoading) {
    $('taskPreflightSummary').textContent = '检查中...';
    return;
  }
  const rows = preflightRows();
  const blocking = rows.filter(r => r.status === 'error').length;
  const warning = rows.filter(r => r.status === 'warning').length;
  $('taskPreflightSummary').textContent = blocking
    ? `${blocking} 项阻断`
    : warning
      ? `${warning} 项提醒`
      : '可以开始';
  $('taskPreflight').innerHTML = rows.map(r => `
    <div class="preflight-item ${preflightStatusClass(r.status)}">
      <div class="preflight-main">
        <span>${esc(r.label)}</span>
        ${uiBadge(preflightStatusText(r.status), preflightBadgeType(r.status))}
      </div>
      <strong>${esc(r.message || r.text || '')}</strong>
      ${r.detail ? `<small>${esc(r.detail)}</small>` : ''}
      ${r.suggestion ? `<em>${esc(r.suggestion)}</em>` : ''}
    </div>
  `).join('');
}

function preflightStatusClass(status) {
  if (status === 'error') return 'danger';
  if (status === 'warning') return 'warn';
  if (status === 'ok') return 'ok';
  return 'unknown';
}

function preflightBadgeType(status) {
  if (status === 'error') return 'danger';
  if (status === 'warning') return 'warning';
  if (status === 'ok') return 'success';
  return 'muted';
}

function preflightStatusText(status) {
  if (status === 'error') return '阻断';
  if (status === 'warning') return '提醒';
  if (status === 'ok') return '正常';
  return '未知';
}

async function startTask(kind) {
  const startBtn = $('startTaskBtn');
  const originalText = startBtn ? startBtn.textContent : '';
  const appendEnabled = $('append') ? $('append').checked : false;
  const buildings = selectedBuildings();
  if ((isCollectionTask(kind) || isRealtimeTask(kind) || kind === 'validate' || kind === 'import') && buildings.length === 0) {
    window.alert('请至少选择一栋楼。');
    return;
  }
  const preflight = await loadTaskPreflight(true);
  const blockingChecks = preflightRows().filter(r => r.status === 'error');
  if (blockingChecks.length) {
    window.alert(`运行前检查存在阻断项：${blockingChecks.map(r => `${r.label} - ${r.message || r.text || ''}`).join('；')}`);
    return;
  }
  const body = {
    kind,
    buildings,
    append: appendEnabled,
    logFile: $('logFile') ? $('logFile').checked : true,
    selfDiagnose: (isCollectionTask(kind) || isRealtimeTask(kind)) && $('selfDiagnose') ? $('selfDiagnose').checked : false,
    noNetMonitor: (isCollectionTask(kind) || isRealtimeTask(kind)) && $('noNetMonitor') ? $('noNetMonitor').checked : false,
    logLevel: (isCollectionTask(kind) || isRealtimeTask(kind)) && $('logLevel') ? $('logLevel').value : '',
    logCategory: (isCollectionTask(kind) || isRealtimeTask(kind)) && $('logCategory') ? $('logCategory').value : '',
    recapture: (isCollectionTask(kind) || isRealtimeTask(kind)) && $('recapture') ? $('recapture').value : '',
    captureMode: (isCollectionTask(kind) || isRealtimeTask(kind)) && $('captureMode') ? $('captureMode').value : '',
    realtimeBatchSize: isRealtimeTask(kind) && $('realtimeBatchSize') ? $('realtimeBatchSize').value : '',
    realtimeReopenEvery: isRealtimeTask(kind) && $('realtimeReopenEvery') ? $('realtimeReopenEvery').value : '',
    realtimeTimeout: isRealtimeTask(kind) && $('realtimeTimeout') ? $('realtimeTimeout').value : '',
    realtimeMaxDevices: isRealtimeTask(kind) && $('realtimeMaxDevices') ? $('realtimeMaxDevices').value : '',
    realtimeRefreshInventory: isRealtimeTask(kind) && $('realtimeRefreshInventory') ? $('realtimeRefreshInventory').checked : false,
    realtimeSkipInventory: isRealtimeTask(kind) && $('realtimeSkipInventory') ? $('realtimeSkipInventory').checked : false,
    run_id: state.runId,
  };
  try {
    if (startBtn) {
      startBtn.disabled = true;
      startBtn.textContent = '启动中...';
    }
    await api('/api/tasks', { method: 'POST', body });
    switchView('tasks');
    await pollTask();
  } catch (err) {
    window.alert(`启动采集失败：${err.message || err}`);
    await pollTask().catch(() => {});
  } finally {
    if (startBtn) {
      startBtn.disabled = false;
      startBtn.textContent = originalText || ((TASK_KIND_META[currentTaskKind()] || {}).action || '开始采集');
    }
  }
}

async function stopTask() {
  await api('/api/tasks/stop', { method: 'POST' });
  pollTask();
}

async function pollTask() {
  const payload = await api('/api/tasks/current');
  const task = payload && payload.current !== undefined ? payload.current : payload;
  const history = payload && Array.isArray(payload.history) ? payload.history : [];
  renderTask(task, history);
  const running = !!(task && (task.status === 'running' || task.status === 'stopping'));
  if ($('stopTaskBtn')) $('stopTaskBtn').disabled = !running;
  if (state.taskTimer) clearTimeout(state.taskTimer);
  if (running) {
    state.taskTimer = setTimeout(pollTask, 1200);
  } else {
    state.taskTimer = null;
  }
}

function taskStatusClass(status) {
  if (window.UI && UI.taskStatusType) return UI.taskStatusType(status);
  if (status === 'done') return 'success';
  if (status === 'failed') return 'danger';
  if (status === 'stopped') return 'muted';
  if (status === 'running' || status === 'stopping') return 'info';
  return 'muted';
}

function taskDuration(task) {
  if (!task || !task.started_at) return '-';
  const end = task.ended_at ? new Date(task.ended_at).getTime() : Date.now();
  const start = new Date(task.started_at).getTime();
  if (!Number.isFinite(start) || !Number.isFinite(end)) return '-';
  const sec = Math.max(0, Math.round((end - start) / 1000));
  if (sec >= 60) return `${Math.floor(sec / 60)}分${String(sec % 60).padStart(2, '0')}秒`;
  return `${sec}秒`;
}

function preflightCheck(key) {
  return state.taskPreflight && (state.taskPreflight.checks || []).find(c => c.key === key) || null;
}

function taskQualitySummaryText() {
  const qualityCheck = preflightCheck('quality');
  if (qualityCheck) return qualityCheck.message || '--';
  const q = state.summary && state.summary.quality && state.summary.quality.summary;
  if (!q) return '--';
  const issues = Number(q.issue_count || 0);
  return issues > 0 ? `${issues} 项待复核` : '质量 OK';
}

function taskLastCountText() {
  const latestRealtime = preflightCheck('latest_realtime');
  if (latestRealtime && latestRealtime.message) {
    const matched = latestRealtime.message.match(/(\d+)/);
    if (matched) return matched[1];
  }
  const detail = currentDetailCount();
  return detail ? String(detail) : '--';
}

function taskLastCollectText() {
  const rt = state.realtimeSummary || {};
  if (rt.latest_mtime) return fmtDateTime(rt.latest_mtime);
  const latestRealtime = preflightCheck('latest_realtime');
  if (latestRealtime && latestRealtime.detail) return fmtDateTime(latestRealtime.detail);
  return latestCollectionLabel().replace(/^最新采集：/, '') || '--';
}

function taskStatusCards(task) {
  const preflight = state.taskPreflight || {};
  const blocking = Number(preflight.summary && preflight.summary.blockingCount || 0);
  const warnings = Number(preflight.summary && preflight.summary.warningCount || 0);
  const ems = preflightCheck('ems_url');
  const edge = preflightCheck('edge_path');
  const profile = preflightCheck('edge_profile');
  return [
    {
      label: '当前状态',
      value: task ? (task.statusText || task.status || '--') : '未开始',
      meta: task ? (task.progressText || '详情见日志') : '等待开始采集',
      type: task ? taskStatusClass(task.status) : 'muted',
    },
    {
      label: '上次采集时间',
      value: taskLastCollectText(),
      meta: state.realtimeSummary && state.realtimeSummary.file_count ? `${state.realtimeSummary.file_count} 个实时文件` : '最近实时详情',
      type: 'info',
    },
    {
      label: '上次设备数量',
      value: taskLastCountText(),
      meta: `当前口径 ${currentDataCount() || '--'}`,
      type: 'info',
    },
    {
      label: '质量审计摘要',
      value: taskQualitySummaryText(),
      meta: warnings ? `${warnings} 项提醒` : '运行前检查',
      type: blocking ? 'danger' : warnings ? 'warning' : 'success',
    },
    {
      label: 'EMS 页面',
      value: ems ? preflightStatusText(ems.status) : '--',
      meta: ems ? ems.message : '等待检查',
      type: ems ? preflightBadgeType(ems.status) : 'muted',
    },
    {
      label: 'Edge 浏览器',
      value: edge ? preflightStatusText(edge.status) : '--',
      meta: edge ? edge.message : '等待检查',
      type: edge ? preflightBadgeType(edge.status) : 'muted',
    },
    {
      label: '配置目录',
      value: profile ? preflightStatusText(profile.status) : '--',
      meta: profile ? profile.message : '等待检查',
      type: profile ? preflightBadgeType(profile.status) : 'muted',
    },
    {
      label: '当前阶段',
      value: task ? (task.statusText || '--') : '未开始',
      meta: task ? `耗时 ${fmtDurationMs(task.elapsedMs)}` : '未运行',
      type: task ? taskStatusClass(task.status) : 'muted',
    },
  ];
}

function renderTaskStatusCards(task) {
  const root = $('taskStatusCards');
  if (!root) return;
  root.innerHTML = taskStatusCards(task).map(card => uiMetricCard(card)).join('');
}

function progressPercent(progress) {
  const n = Number(progress && progress.percent);
  if (Number.isFinite(n)) return Math.max(0, Math.min(100, Math.round(n)));
  const done = Number(progress && (progress.deviceDone ?? progress.buildingIndex));
  const total = Number(progress && (progress.deviceTotal ?? progress.buildingTotal));
  if (Number.isFinite(done) && Number.isFinite(total) && total > 0) {
    return Math.max(0, Math.min(100, Math.round((done / total) * 100)));
  }
  return 0;
}

function renderTaskProgress(progress) {
  if (!progress) return '';
  const percent = progressPercent(progress);
  const building = progress.building ? String(progress.building) : '-';
  const buildingText = progress.buildingTotal
    ? `${progress.buildingIndex || 0}/${progress.buildingTotal}`
    : '-';
  const deviceText = progress.deviceTotal
    ? `${progress.deviceDone || 0}/${progress.deviceTotal}`
    : '-';
  const batchText = progress.batchTotal
    ? `${progress.batchIndex || 0}/${progress.batchTotal}`
    : '-';
  return `
    <div class="task-progress">
      <div class="task-progress-head">
        <strong>${esc(progress.message || progress.phase || '任务进行中')}</strong>
        <span>${percent}%</span>
      </div>
      <div class="task-progress-bar"><i style="width:${percent}%"></i></div>
      <div class="task-progress-grid">
        <span>阶段 <strong>${esc(progress.phase || '-')}</strong></span>
        <span>楼栋 <strong>${esc(building)}</strong></span>
        <span>楼栋进度 <strong>${esc(buildingText)}</strong></span>
        <span>设备 <strong>${esc(deviceText)}</strong></span>
        <span>批次 <strong>${esc(batchText)}</strong></span>
      </div>
    </div>
  `;
}

function renderTaskError(error) {
  if (!error) return '';
  return `
    <div class="task-error-card ${esc(error.severity || 'error')}">
      <strong>${esc(error.title || '任务失败')}</strong>
      <span>${esc(error.message || '')}</span>
      ${error.suggestion ? `<em>${esc(error.suggestion)}</em>` : ''}
      ${error.raw ? `<details><summary>诊断详情</summary><pre>${esc(error.raw)}</pre></details>` : ''}
    </div>
  `;
}

function plannedStepsForCurrentKind() {
  const meta = TASK_KIND_META[currentTaskKind()] || TASK_KIND_META.collectImport;
  return meta.plan.map(label => ({ label, status: 'pending' }));
}

function renderTask(task, history = []) {
  state.currentTask = task;
  renderTaskStatusCards(task);
  const logEl = $('taskLog');
  if (!task) {
    $('taskState').textContent = '空闲';
    $('taskState').className = 'badge muted';
    $('taskRuntimeSummary').innerHTML = '<div class="runtime-empty">当前没有运行中的任务。</div>';
    $('taskSteps').innerHTML = plannedStepsForCurrentKind().map(s => renderStep(s)).join('');
    logEl.className = 'logbox idle';
    logEl.textContent = '';
    renderTaskHistory(history);
    return;
  }
  $('taskState').textContent = task.statusText || task.status;
  $('taskState').className = 'badge ' + taskStatusClass(task.status);
  $('taskRuntimeSummary').innerHTML = `
    <div class="runtime-card"><span>任务</span><strong>${esc((TASK_KIND_META[task.kind] || {}).title || task.kind)}</strong></div>
    <div class="runtime-card"><span>开始时间</span><strong>${esc(fmtDateTime(task.started_at))}</strong></div>
    <div class="runtime-card"><span>耗时</span><strong>${esc(fmtDurationMs(task.elapsedMs) || taskDuration(task))}</strong></div>
    <div class="runtime-card"><span>日志文件</span><strong>${esc(task.log_file || '-')}</strong></div>
    <div class="runtime-card wide"><span>当前阶段</span><strong>${esc(task.statusText || task.status || '-')}</strong><em>${esc(task.progressText || '')}</em></div>
    ${renderTaskError(task.normalizedError)}
    ${renderTaskProgress(task.progress)}
  `;
  $('taskSteps').innerHTML = (task.steps || []).map(s => renderStep(s)).join('');
  logEl.className = `logbox ${taskStatusClass(task.status)}`;
  logEl.textContent = (task.logs || []).join('\n');
  logEl.scrollTop = logEl.scrollHeight;
  renderTaskHistory(history);
}

function renderStep(step) {
  return `
    <div class="step ${esc(step.status || 'pending')}">
      <span>${esc(step.label)}</span>
      <strong>${esc(step.status || 'pending')}</strong>
    </div>
  `;
}

function renderTaskHistory(history = []) {
  const root = $('taskHistory');
  if (!root) return;
  const rows = history.slice(0, 8);
  root.innerHTML = rows.map(t => `
    <div class="task-history-item">
      <div>
        <strong>${esc((TASK_KIND_META[t.kind] || {}).title || t.kind)}</strong>
        <span>${esc(fmtDateTime(t.started_at))} · ${esc(taskDuration(t))}</span>
      </div>
      <em class="badge ${taskStatusClass(t.status)}">${esc(t.status)}</em>
    </div>
  `).join('') || '<div class="muted">暂无任务记录</div>';
}

window.addEventListener('DOMContentLoaded', () => {
  initControls();
  refreshAll();
});
