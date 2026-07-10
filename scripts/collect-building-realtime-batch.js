#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const { installRealtimeLog } = require('./realtime-logger');
const { ensureRealtimeBrowser } = require('./realtime-browser');

const CDP_URL = process.env.CDP_URL || 'http://127.0.0.1:9222';
const BUILDING = (process.argv.find(a => a.startsWith('--building=')) || '--building=1号').split('=')[1];
const BROWSER_MODE = (process.argv.find(a => a.startsWith('--browser-mode=')) || `--browser-mode=${process.env.REALTIME_BROWSER_MODE || 'persistent'}`)
  .split('=')
  .slice(1)
  .join('=') || 'persistent';
const CDP_ARG = process.argv.find(a => a.startsWith('--cdp-url='));
const EFFECTIVE_CDP_URL = CDP_ARG ? CDP_ARG.split('=').slice(1).join('=') : CDP_URL;
const STRICT_CDP = process.argv.includes('--strict-cdp');
const BATCH_SIZE = Number((process.argv.find(a => a.startsWith('--batch-size=')) || '').split('=')[1] || 50);
const MAX_DEVICES = Number((process.argv.find(a => a.startsWith('--max-devices=')) || '').split('=')[1] || 0);
const SKIP_DEVICES = Number((process.argv.find(a => a.startsWith('--skip-devices=')) || '').split('=')[1] || 0);
const REOPEN_EVERY = Number((process.argv.find(a => a.startsWith('--reopen-every=')) || '').split('=')[1] || 0);
const TIMEOUT_MS = Number((process.argv.find(a => a.startsWith('--timeout=')) || '').split('=')[1] || 12000);
const OVERWRITE_LATEST = process.argv.includes('--write-latest');
const OUT_DIR = path.resolve(process.env.EMS_OUT_DIR || path.resolve(__dirname, '..', 'out'));
installRealtimeLog({ prefix: `realtime_${BUILDING}_batch` });

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

const FIELD_ALIAS = {
  '当前开关机模式': '当前开关机状态',
  '当前设置温度': '设定温度',
};

const CELSIUS_FIELDS = new Set([
  '室内温度',
  '设定温度',
  '设定温度上限',
  '设定温度下限',
]);

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

function pause(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function timestamp() {
  const d = new Date();
  const p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}_${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function progress(event) {
  console.log(`[PROGRESS] ${JSON.stringify({
    ts: new Date().toISOString(),
    building: BUILDING,
    ...event,
  })}`);
}

function normalizeFieldValue(value) {
  return String(value || '').replace(/\s+/g, '').replace(/\.0(?=℃?$)/, '');
}

function defaultTemplateScore(fields) {
  let matched = 0;
  let checked = 0;
  for (const [name, expected] of Object.entries(DEFAULT_TEMPLATE_VALUES)) {
    if (!(name in fields)) continue;
    checked++;
    if (normalizeFieldValue(fields[name]) === normalizeFieldValue(expected)) matched++;
  }
  return {
    matched,
    checked,
    isDefaultLike: checked >= 8 && matched >= Math.max(8, Math.floor(checked * 0.75)),
  };
}

function summarize(rows, startedAt) {
  const ok = rows.filter(r => !r.error);
  const loadTimes = ok.map(r => r.loadMs).filter(Number.isFinite);
  const invalidLock = rows.filter(r => {
    const value = r.fields && r.fields['集控锁定'];
    return value && value !== '开启' && value !== '关闭';
  }).length;
  return {
    building: BUILDING,
    devices: rows.length,
    success: ok.length,
    failed: rows.length - ok.length,
    defaultLike: rows.filter(r => r.defaultLike).length,
    invalidLock,
    elapsedMs: Date.now() - startedAt,
    avgLoadMs: loadTimes.length ? Math.round(loadTimes.reduce((a, b) => a + b, 0) / loadTimes.length) : null,
    maxLoadMs: loadTimes.length ? Math.max(...loadTimes) : null,
    batchSize: BATCH_SIZE,
  };
}

function devicePathFromRow(row) {
  return {
    ptId: row.devId,
    ptType: 'meter',
    target: 'ems',
    dataType: 'coll',
    cubeParam: {
      cube: null,
      dataTypes: [],
      timeType: null,
      statisType: null,
      statisParam: null,
      convertType: null,
      controlType: null,
      nodes: [],
      attributeFieldID: null,
    },
    isCommStatus: false,
    _CollPointTreeType: '通讯树',
    _CollPointNodeId: 0,
    _CollPointRtuId: row.rtuId || 0,
    _CollPointEnergyTypeId: 0,
    _CollPointMeterId: row.devId,
  };
}

function newestRealtimeFile() {
  if (!fs.existsSync(OUT_DIR)) return '';
  const escaped = BUILDING.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const re = new RegExp(`^realtime_${escaped}_(?:batch_)?\\d{8}_\\d{6}\\.json$`);
  return fs.readdirSync(OUT_DIR)
    .filter(name => re.test(name))
    .map(name => {
      const full = path.join(OUT_DIR, name);
      return { full, mtime: fs.statSync(full).mtimeMs };
    })
    .sort((a, b) => b.mtime - a.mtime)[0]?.full || '';
}

function loadDeviceRows() {
  const realtimeFile = path.join(OUT_DIR, `realtime_${BUILDING}_latest.json`);
  const devicesFile = path.join(OUT_DIR, `devices_${BUILDING}_latest.json`);
  const file = fs.existsSync(devicesFile)
    ? devicesFile
    : fs.existsSync(realtimeFile)
      ? realtimeFile
      : newestRealtimeFile();
  if (!fs.existsSync(file)) {
    throw new Error(`No device source found for ${BUILDING}: expected ${realtimeFile}, ${devicesFile}, or realtime_${BUILDING}_*.json`);
  }
  const data = JSON.parse(fs.readFileSync(file, 'utf8'));
  const seen = new Set();
  const allRows = [];
  for (const row of data.rows || []) {
    if (!row.devId || seen.has(row.devId)) continue;
    seen.add(row.devId);
    allRows.push({
      ...row,
      ptPath: devicePathFromRow(row),
    });
  }
  const start = Math.max(0, SKIP_DEVICES);
  const end = MAX_DEVICES > 0 ? start + MAX_DEVICES : undefined;
  return allRows.slice(start, end);
}

async function closeModals(page) {
  await page.keyboard.press('Escape').catch(() => {});
  await page.evaluate(() => {
    for (const el of document.querySelectorAll('.ivu-modal-close,.ivu-icon-ios-close')) {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      if (r.width > 0 && r.height > 0 && cs.display !== 'none' && cs.visibility !== 'hidden') el.click();
    }
  }).catch(() => {});
  await page.waitForFunction(() => {
    return ![...document.querySelectorAll('.ivu-modal-body')].some(el => {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return r.width > 100 && r.height > 100 && cs.display !== 'none' && cs.visibility !== 'hidden';
    });
  }, { timeout: 1200, polling: 50 }).catch(() => {});
}

async function injectHelpers(page) {
  await page.evaluate(() => {
    const mainWidget = () => [...document.querySelectorAll('.pi-graphics-configuration-svg-new')]
      .filter(el => el.__vue__ && !el.closest('.ivu-modal-body'))
      .map(el => ({ el, area: el.getBoundingClientRect().width * el.getBoundingClientRect().height }))
      .sort((a, b) => b.area - a.area)[0]?.el || null;
    const mainShadow = () => {
      const widget = mainWidget();
      const container = widget && (widget.querySelector('.pi-svg-container') || document.querySelector('.pi-svg-container'));
      return container && container.shadowRoot ? container.shadowRoot : null;
    };
    const parsePtPath = value => {
      if (!value) return null;
      if (typeof value === 'object') return value;
      try { return JSON.parse(value); } catch { return null; }
    };
    const toNum = value => {
      const n = Number(value);
      return Number.isFinite(n) && n > 0 ? n : null;
    };
    window.__emsBatch = {
      getDevices() {
        const widget = mainWidget();
        const sr = mainShadow();
        const sl = widget?.__vue__?.$data?.svgListDraw || [];
        if (!widget || !sr || !Array.isArray(sl)) return [];
        const names = {};
        for (const e of sl) {
          for (const ld of (e.dyn?.listDyn || [])) {
            if (!(ld.DynType === 23 || ld.PropertyName === 'CabinetId')) continue;
            const ptPath = parsePtPath(ld.PtPath);
            const devId = toNum(ptPath && (ptPath._CollPointMeterId || ptPath.ptId));
            const el = sr.getElementById(e.id) || sr.querySelector('#' + CSS.escape(e.id));
            const name = el ? (el.textContent || '').trim() : '';
            if (devId && name) names[devId] = name;
          }
        }
        const devices = [];
        const seen = new Set();
        for (const e of sl) {
          for (const ld of (e.dyn?.listDyn || [])) {
            if (ld.DynType !== 22) continue;
            const ptPath = parsePtPath(ld.PtPath);
            if (!ptPath || ptPath.ptType !== 'meter') continue;
            const devId = toNum(ptPath._CollPointMeterId || ptPath.ptId);
            if (!devId || seen.has(devId)) continue;
            const el = sr.getElementById(e.id) || sr.querySelector('#' + CSS.escape(e.id));
            const r = el && el.getBoundingClientRect();
            if (!r || r.width < 40 || r.height < 30) continue;
            seen.add(devId);
            devices.push({
              name: names[devId] || '',
              devId,
              elementId: e.id,
              x: Math.round(r.left + r.width / 2),
              y: Math.round(r.top + r.height / 2),
            });
          }
        }
        return devices.filter(d => d.name && d.name !== '0-0001-KT');
      },
      clickById(id) {
        const sr = mainShadow();
        const el = sr && (sr.getElementById(id) || sr.querySelector('#' + CSS.escape(id)));
        if (!el) return false;
        const r = el.getBoundingClientRect();
        const opts = {
          bubbles: true,
          cancelable: true,
          view: window,
          clientX: r.left + r.width / 2,
          clientY: r.top + r.height / 2,
        };
        el.dispatchEvent(new MouseEvent('mousedown', opts));
        el.dispatchEvent(new MouseEvent('mouseup', opts));
        el.dispatchEvent(new MouseEvent('click', opts));
        return true;
      },
    };
  });
}

async function ensureTemplateModal(page) {
  const modalHealth = await page.evaluate(({ fieldAlias }) => {
    const visible = el => {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return r.width > 100 && r.height > 100 && cs.display !== 'none' && cs.visibility !== 'hidden';
    };
    const body = [...document.querySelectorAll('.ivu-modal-body')].find(body => {
      if (!visible(body)) return false;
      const svg = body.querySelector('.pi-svg-container')?.shadowRoot?.querySelector('svg');
      return (svg?.textContent || '').includes('空调实时数据');
    });
    if (!body) return { visible: false, healthy: false, reason: 'not visible' };
    const widget = [...body.querySelectorAll('.pi-graphics-configuration-svg-new')].find(el => el.__vue__);
    const vue = widget && widget.__vue__;
    if (!vue) return { visible: true, healthy: false, reason: 'vue missing' };

    const data = vue.$data || {};
    const list = data.ptPathList;
    const runConf = Array.isArray(data.runConfDataProp) ? data.runConfDataProp : [];
    const baseConfByKey = {};
    for (const item of runConf) {
      if (item && item.ptPathKey) baseConfByKey[item.ptPathKey] = item.ptPathConf || item.conf || item;
    }

    let baseEntryCount = 0;
    if (list && typeof list.forEach === 'function') {
      list.forEach(item => {
        if (!item || !item.ptPathValue) return;
        const ptPath = item.ptPathValue.ptPath || {};
        if (item.ptPathValue.propertyName || ptPath.ptType === 'meter' || ptPath.ptType === 'cubeattr' || ptPath.ptType === 'acc') return;
        if (ptPath.dataType !== 'pttype' && !item.ptPathValue.devPath) return;
        const conf = baseConfByKey[item.ptPathKey] || {};
        const rawName = conf.name || conf.measurandName || '';
        const fieldName = fieldAlias[rawName] || rawName;
        if (fieldName) baseEntryCount++;
      });
    }

    return {
      visible: true,
      healthy: baseEntryCount >= 40 && baseEntryCount <= 60,
      reason: baseEntryCount ? 'ok' : 'template entries missing',
      baseEntryCount,
      runConfCount: runConf.length,
      listSize: typeof list?.size === 'number' ? list.size : (Array.isArray(list) ? list.length : null),
    };
  }, { fieldAlias: FIELD_ALIAS }).catch(err => ({ visible: false, healthy: false, reason: err.message }));
  if (modalHealth.visible && modalHealth.healthy) return;
  if (modalHealth.visible) await closeModals(page);

  await closeModals(page);
  const devices = await page.evaluate(() => window.__emsBatch.getDevices()).catch(() => []);
  const opener = devices[0];
  if (!opener) throw new Error('No visible device to open template modal');
  const clicked = await page.evaluate(target => window.__emsBatch.clickById(target.elementId), opener).catch(() => false);
  if (!clicked) await page.mouse.click(opener.x, opener.y);
  await page.waitForFunction(name => {
    const visible = el => {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return r.width > 100 && r.height > 100 && cs.display !== 'none' && cs.visibility !== 'hidden';
    };
    return [...document.querySelectorAll('.ivu-modal-body')].some(body => {
      if (!visible(body)) return false;
      const svg = body.querySelector('.pi-svg-container')?.shadowRoot?.querySelector('svg');
      const text = svg?.textContent || '';
      return text.includes('空调实时数据') && text.includes(name);
    });
  }, opener.name, { timeout: 8000, polling: 100 });
}

async function startBatchSubscription(page, devices) {
  return await page.evaluate(({ devices, fieldAlias, celsiusFields }) => {
    const clone = value => JSON.parse(JSON.stringify(value));
    const visible = el => {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return r.width > 100 && r.height > 100 && cs.display !== 'none' && cs.visibility !== 'hidden';
    };
    const body = [...document.querySelectorAll('.ivu-modal-body')].find(el => {
      if (!visible(el)) return false;
      const svg = el.querySelector('.pi-svg-container')?.shadowRoot?.querySelector('svg');
      return (svg?.textContent || '').includes('空调实时数据');
    });
    const widget = body && [...body.querySelectorAll('.pi-graphics-configuration-svg-new')].find(el => el.__vue__);
    if (!widget) return { error: 'modal widget not found' };

    const vue = widget.__vue__;
    const data = vue.$data || {};

    // A previous batch leaves a synthetic ptPathList in the modal component.
    // Restore the real 46-point template before deriving the next batch.
    const previousProbe = window.__emsBatchProbe;
    if (previousProbe?.originalList) {
      if (vue.clearSubscribe) vue.clearSubscribe();
      data.ptPathList = previousProbe.originalList;
      try { vue.ptPathList = previousProbe.originalList; } catch {}
      if (Array.isArray(data.websocketDataProp)) data.websocketDataProp.splice(0, data.websocketDataProp.length);
      else data.websocketDataProp = [];
      delete window.__emsBatchProbe;
    }

    const originalList = data.ptPathList;
    const baseRunConf = Array.isArray(data.runConfDataProp) ? data.runConfDataProp : [];
    if (!originalList || typeof originalList.forEach !== 'function') return { error: 'ptPathList missing' };
    if (!baseRunConf.length) return { error: 'runConfDataProp missing' };

    const baseConfByKey = {};
    for (const item of baseRunConf) {
      if (item && item.ptPathKey) baseConfByKey[item.ptPathKey] = item.ptPathConf || item.conf || item;
    }

    const baseEntries = [];
    originalList.forEach(item => {
      if (!item || !item.ptPathValue) return;
      const ptPath = item.ptPathValue.ptPath || {};
      if (item.ptPathValue.propertyName || ptPath.ptType === 'meter' || ptPath.ptType === 'cubeattr' || ptPath.ptType === 'acc') return;
      if (ptPath.dataType !== 'pttype' && !item.ptPathValue.devPath) return;
      const conf = baseConfByKey[item.ptPathKey] || {};
      const rawName = conf.name || conf.measurandName || '';
      const fieldName = fieldAlias[rawName] || rawName;
      if (!fieldName) return;
      baseEntries.push({ item, baseKey: item.ptPathKey, conf, fieldName });
    });

    const batchMap = new Map();
    const keyMap = {};
    for (const dev of devices) {
      for (const base of baseEntries) {
        const item = clone(base.item);
        const key = `${dev.devId}::${base.baseKey}`;
        item.ptPathKey = key;
        item.ptPathValue = item.ptPathValue || {};
        item.ptPathValue.devPath = clone(dev.ptPath);
        if (item.ptPathValue.ptPath && item.ptPathValue.ptPath.dataType !== 'pttype') item.ptPathValue.ptPath.dataType = 'pttype';
        batchMap.set(key, item);
        keyMap[key] = {
          devId: dev.devId,
          name: dev.name,
          field: base.fieldName,
          unit: celsiusFields.includes(base.fieldName) ? '℃' : '',
          enumDefine: base.conf.enumDefine || [],
        };
      }
    }

    if (vue.clearSubscribe) vue.clearSubscribe();
    if (Array.isArray(data.websocketDataProp)) data.websocketDataProp.splice(0, data.websocketDataProp.length);
    else data.websocketDataProp = [];

    window.__emsBatchProbe = {
      originalList,
      keyMap,
      startedAt: Date.now(),
    };
    try {
      Object.defineProperty(vue, 'isSVGTemplate', { configurable: true, enumerable: true, value: false });
    } catch {}
    data.ptPathList = batchMap;
    try { vue.ptPathList = batchMap; } catch {}

    if (!vue.subscribeData) return { error: 'subscribeData missing' };
    vue.subscribeData();
    return { ok: true, requestedCount: batchMap.size, baseEntryCount: baseEntries.length };
  }, { devices, fieldAlias: FIELD_ALIAS, celsiusFields: [...CELSIUS_FIELDS] });
}

async function readBatchSubscription(page) {
  return await page.evaluate(({ celsiusFields }) => {
    const visible = el => {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return r.width > 100 && r.height > 100 && cs.display !== 'none' && cs.visibility !== 'hidden';
    };
    const body = [...document.querySelectorAll('.ivu-modal-body')].find(el => {
      if (!visible(el)) return false;
      const svg = el.querySelector('.pi-svg-container')?.shadowRoot?.querySelector('svg');
      return (svg?.textContent || '').includes('空调实时数据');
    });
    const widget = body && [...body.querySelectorAll('.pi-graphics-configuration-svg-new')].find(el => el.__vue__);
    if (!widget) return { error: 'modal widget not found', rows: [] };
    const data = widget.__vue__.$data || {};
    const ws = data.websocketDataProp || [];
    const entries = Array.isArray(ws) ? ws : Object.values(ws);
    const keyMap = window.__emsBatchProbe?.keyMap || {};
    const rowsByDev = {};
    for (const item of entries) {
      const tag = item && item.tag ? item.tag : item;
      const meta = item && keyMap[item.ptPathKey];
      if (!meta || !tag || tag.value === undefined || tag.value === null) continue;
      if (!rowsByDev[meta.devId]) rowsByDev[meta.devId] = {
        name: meta.name,
        devId: meta.devId,
        tagCount: 0,
        validTagCount: 0,
        fields: {},
        rawFields: {},
        validFields: {},
      };
      const row = rowsByDev[meta.devId];
      row.tagCount++;
      if (tag.valid !== false) row.validTagCount++;
      const raw = String(tag.value);
      const enumHit = Array.isArray(meta.enumDefine)
        ? meta.enumDefine.find(e => String(e.Key ?? e.key) === raw)
        : null;
      let value = enumHit ? (enumHit.Value ?? enumHit.value) : raw;
      const unit = meta.unit || (celsiusFields.includes(meta.field) ? '℃' : '');
      if (!enumHit && unit && value !== '' && !String(value).includes(unit)) value = `${value} ${unit}`;
      if (!row.fields[meta.field] || tag.valid !== false) {
        row.fields[meta.field] = String(value);
        row.rawFields[meta.field] = raw;
        row.validFields[meta.field] = tag.valid !== undefined ? !!tag.valid : null;
      }
    }
    return {
      elapsedMs: Date.now() - (window.__emsBatchProbe?.startedAt || Date.now()),
      totalEntries: entries.length,
      knownEntries: entries.filter(item => item && keyMap[item.ptPathKey]).length,
      rows: Object.values(rowsByDev),
    };
  }, { celsiusFields: [...CELSIUS_FIELDS] });
}

async function restoreBatchSubscription(page) {
  await page.evaluate(() => {
    const visible = el => {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return r.width > 100 && r.height > 100 && cs.display !== 'none' && cs.visibility !== 'hidden';
    };
    const body = [...document.querySelectorAll('.ivu-modal-body')].find(el => {
      if (!visible(el)) return false;
      const svg = el.querySelector('.pi-svg-container')?.shadowRoot?.querySelector('svg');
      return (svg?.textContent || '').includes('空调实时数据');
    });
    const widget = body && [...body.querySelectorAll('.pi-graphics-configuration-svg-new')].find(el => el.__vue__);
    const vue = widget && widget.__vue__;
    if (!vue) return;
    if (vue.clearSubscribe) vue.clearSubscribe();
    const probe = window.__emsBatchProbe;
    if (probe?.originalList) {
      vue.$data.ptPathList = probe.originalList;
      try { vue.ptPathList = probe.originalList; } catch {}
    }
    try { delete vue.isSVGTemplate; } catch {}
    delete window.__emsBatchProbe;
  }).catch(() => {});
}

async function captureBatch(page, batch) {
  const started = Date.now();
  const start = await startBatchSubscription(page, batch);
  if (start.error) {
    return {
      loadMs: Math.max(0, Date.now() - started),
      rows: batch.map(dev => ({ dev, error: start.error, fields: {}, tagCount: 0, validTagCount: 0 })),
      start,
    };
  }
  const deadline = Date.now() + TIMEOUT_MS;
  let best = null;
  while (Date.now() < deadline) {
    const snap = await readBatchSubscription(page);
    if (!best || snap.knownEntries > best.knownEntries) best = snap;
    if (snap.rows.length === batch.length && snap.rows.every(row => row.tagCount >= 46)) break;
    await pause(100);
  }
  const rowByDevId = new Map((best?.rows || []).map(row => [row.devId, row]));
  return {
    loadMs: Math.max(0, Date.now() - started),
    start,
    best,
    rows: batch.map(dev => {
      const row = rowByDevId.get(dev.devId) || { fields: {}, rawFields: {}, validFields: {}, tagCount: 0, validTagCount: 0 };
      const score = defaultTemplateScore(row.fields || {});
      const fieldCount = Object.keys(row.fields || {}).length;
      let error = '';
      if (row.tagCount < 46) error = `batch tags incomplete: ${row.tagCount}/46`;
      else if (fieldCount < 20) error = `batch fields incomplete: ${fieldCount}`;
      else if (score.isDefaultLike) error = 'batch default-like values';
      return {
        dev,
        error,
        fields: row.fields || {},
        rawFields: row.rawFields || {},
        validFields: row.validFields || {},
        tagCount: row.tagCount || 0,
        validTagCount: row.validTagCount || 0,
        defaultMatchCount: score.matched,
        defaultCheckedCount: score.checked,
        defaultLike: score.isDefaultLike,
      };
    }),
  };
}

async function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const ts = timestamp();
  const jsonPath = path.join(OUT_DIR, `realtime_${BUILDING}_batch_${ts}.json`);
  const ndjsonPath = path.join(OUT_DIR, `realtime_${BUILDING}_batch_${ts}.ndjson`);
  const latestPath = path.join(OUT_DIR, `realtime_${BUILDING}_latest.json`);
  const ndjson = fs.createWriteStream(ndjsonPath, { flags: 'a' });
  const startedAt = Date.now();
  const sourceRows = loadDeviceRows();
  const { page } = await ensureRealtimeBrowser({
    cdpUrl: EFFECTIVE_CDP_URL,
    mode: BROWSER_MODE,
    strictCdp: STRICT_CDP,
    log: msg => console.log(msg),
  });
  await injectHelpers(page);
  await ensureTemplateModal(page);

  const rows = [];
  console.log(`[BATCH START] ${BUILDING}: devices=${sourceRows.length}, batchSize=${BATCH_SIZE}, out=${jsonPath}`);
  progress({
    phase: 'realtime_batch',
    status: 'running',
    deviceDone: 0,
    deviceTotal: sourceRows.length,
    batchIndex: 0,
    batchTotal: Math.ceil(sourceRows.length / BATCH_SIZE),
    percent: 0,
    message: `${BUILDING} 批量采集开始`,
  });
  for (let i = 0; i < sourceRows.length; i += BATCH_SIZE) {
    const batchIndex = Math.floor(i / BATCH_SIZE);
    const batchTotal = Math.ceil(sourceRows.length / BATCH_SIZE);
    if (REOPEN_EVERY > 0 && batchIndex > 0 && batchIndex % REOPEN_EVERY === 0) {
      await restoreBatchSubscription(page);
      await closeModals(page);
      await ensureTemplateModal(page);
    }
    const batch = sourceRows.slice(i, i + BATCH_SIZE);
    process.stdout.write(`\r[BATCH] ${batchIndex + 1}/${Math.ceil(sourceRows.length / BATCH_SIZE)} ${SKIP_DEVICES + i + 1}-${SKIP_DEVICES + i + batch.length}`.padEnd(100));
    progress({
      phase: 'realtime_batch',
      status: 'running',
      deviceDone: rows.length,
      deviceTotal: sourceRows.length,
      batchIndex: batchIndex + 1,
      batchTotal,
      percent: sourceRows.length ? Math.round((rows.length / sourceRows.length) * 100) : 0,
      message: `${BUILDING} 批次 ${batchIndex + 1}/${batchTotal}`,
    });
    const result = await captureBatch(page, batch);
    for (const item of result.rows) {
      const dev = item.dev;
      const row = {
        building: BUILDING,
        subAreaIdx: dev.subAreaIdx,
        floor: dev.floor,
        subAreaText: dev.subAreaText || '',
        tab: dev.tab || '',
        pageName: dev.pageName || '',
        name: dev.name,
        devId: dev.devId,
        meterId: dev.meterId || dev.devId,
        rtuId: dev.rtuId || 0,
        template: dev.template || '',
        loadMs: result.loadMs,
        fieldCount: Object.keys(item.fields || {}).length,
        realtimeTagCount: item.tagCount || 0,
        realtimeValidTagCount: item.validTagCount || 0,
        runConfCount: 48,
        defaultLike: !!item.defaultLike,
        defaultMatchCount: item.defaultMatchCount || 0,
        defaultCheckedCount: item.defaultCheckedCount || 0,
        retry: 0,
      clickMethod: 'batch-subscribe',
      batchSize: batch.length,
      error: item.error || '',
      cardComm: dev.cardComm || dev.card_comm || '',
      cardSwitch: dev.cardSwitch || dev.card_switch || '',
      cardIndicator: dev.cardIndicator || dev.card_indicator || '',
      cardSwitchIndicator: dev.cardSwitchIndicator || dev.card_switch_indicator || '',
      cardStateSource: dev.cardStateSource || dev.card_state_source || '',
      fields: item.fields || {},
      rawFields: item.rawFields || {},
      validFields: item.validFields || {},
    };
      rows.push(row);
      ndjson.write(JSON.stringify(row) + '\n');
    }
    const partial = { summary: summarize(rows, startedAt), rows };
    fs.writeFileSync(jsonPath, JSON.stringify(partial, null, 2), 'utf8');
    progress({
      phase: 'realtime_batch',
      status: 'running',
      deviceDone: rows.length,
      deviceTotal: sourceRows.length,
      batchIndex: batchIndex + 1,
      batchTotal,
      percent: sourceRows.length ? Math.round((rows.length / sourceRows.length) * 100) : 100,
      message: `${BUILDING} 已采集 ${rows.length}/${sourceRows.length}`,
    });
  }
  process.stdout.write('\n');

  await restoreBatchSubscription(page);
  const result = { summary: summarize(rows, startedAt), rows };
  fs.writeFileSync(jsonPath, JSON.stringify(result, null, 2), 'utf8');
  if (OVERWRITE_LATEST && MAX_DEVICES === 0) fs.writeFileSync(latestPath, JSON.stringify(result, null, 2), 'utf8');
  ndjson.end();
  console.log(`[BATCH DONE] ${jsonPath}`);
  console.log(JSON.stringify(result.summary, null, 2));
  progress({
    phase: 'realtime_batch',
    status: 'done',
    deviceDone: rows.length,
    deviceTotal: sourceRows.length,
    batchIndex: Math.ceil(sourceRows.length / BATCH_SIZE),
    batchTotal: Math.ceil(sourceRows.length / BATCH_SIZE),
    percent: 100,
    message: `${BUILDING} 实时详情完成 ${rows.length}/${sourceRows.length}`,
  });
}

main().then(() => process.exit(0)).catch(err => {
  console.error(err.stack || err);
  process.exit(1);
});
