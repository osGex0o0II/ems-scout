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
const MAX_SUBAREAS = Number((process.argv.find(a => a.startsWith('--max-subareas=')) || '').split('=')[1] || 0);
const MAX_DEVICES = Number((process.argv.find(a => a.startsWith('--max-devices=')) || '').split('=')[1] || 0);
const READY_TIMEOUT_MS = Number((process.argv.find(a => a.startsWith('--ready-timeout=')) || '').split('=')[1] || 6000);
const FAILED_FROM = (process.argv.find(a => a.startsWith('--failed-from=')) || '').split('=').slice(1).join('=');
const REUSE_MODAL = process.argv.includes('--reuse-modal');
const INVENTORY_ONLY = process.argv.includes('--inventory-only');
const IS_PARTIAL_RUN = MAX_SUBAREAS > 0 || MAX_DEVICES > 0;
const OUT_DIR = path.resolve(process.env.EMS_OUT_DIR || path.resolve(__dirname, '..', 'out'));
installRealtimeLog({ prefix: `realtime_${BUILDING}_details` });

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

function timestamp() {
  const d = new Date();
  const p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}_${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function pause(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
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

function pageOrder(label) {
  const order = { 'default': 0, '一页': 1, '二页': 2, '三页': 3, '四页': 4, '五页': 5, '六页': 6, '七页': 7, '八页': 8, '九页': 9, '十页': 10 };
  return order[label] || 99;
}

function summarize(rows, startedAt) {
  const ok = rows.filter(r => !r.error);
  const failed = rows.length - ok.length;
  const loadTimes = ok.map(r => r.loadMs).filter(Number.isFinite);
  const defaultLike = rows.filter(r => r.defaultLike).length;
  const invalidLock = rows.filter(r => {
    const value = r.fields && r.fields['集控锁定'];
    return value && value !== '开启' && value !== '关闭';
  }).length;
  return {
    building: BUILDING,
    devices: rows.length,
    success: ok.length,
    failed,
    defaultLike,
    invalidLock,
    elapsedMs: Date.now() - startedAt,
    avgLoadMs: loadTimes.length ? Math.round(loadTimes.reduce((a, b) => a + b, 0) / loadTimes.length) : null,
    maxLoadMs: loadTimes.length ? Math.max(...loadTimes) : null,
  };
}

function rowKey(row) {
  return [
    row.subAreaText || '',
    row.tab || '',
    row.pageName || '',
    row.devId || '',
    row.name || '',
  ].join('|');
}

function pageKey(meta) {
  return [
    meta.subAreaText || '',
    meta.tab || '',
    meta.pageName || '',
  ].join('|');
}

function buildFailedTargets(file) {
  if (!file) return null;
  const data = JSON.parse(fs.readFileSync(path.resolve(file), 'utf8'));
  const failedRows = (data.rows || []).filter(r => r.error);
  const pages = new Map();
  for (const row of failedRows) {
    const key = pageKey(row);
    if (!pages.has(key)) pages.set(key, new Set());
    pages.get(key).add(row.name);
  }
  return { source: data, failedRows, pages };
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
    const groupText = g => {
      let txt = '';
      for (const c of g.childNodes) if (c.nodeType === 3) txt += c.textContent;
      txt = txt.trim();
      if (!txt) {
        const t = g.querySelector(':scope > text, :scope > tspan');
        if (t) txt = (t.textContent || '').trim();
      }
      return txt;
    };
    const parsePtPath = s => {
      if (!s) return null;
      if (typeof s === 'object') return s;
      try { return JSON.parse(s); } catch { return null; }
    };
    const toNum = v => {
      const n = Number(v);
      return Number.isFinite(n) && n > 0 ? n : null;
    };

    window.__ems_rt = {
      isReady() {
        const sr = mainShadow();
        const svg = sr && sr.querySelector('svg');
        return !!(svg && svg.querySelectorAll('text').length > 5);
      },
      clickById(id) {
        const sr = mainShadow();
        const el = sr && (sr.getElementById(id) || sr.querySelector('#' + CSS.escape(id)));
        if (!el) return false;
        const r = el.getBoundingClientRect();
        const opts = { bubbles: true, cancelable: true, view: window, clientX: r.left + r.width / 2, clientY: r.top + r.height / 2 };
        el.dispatchEvent(new MouseEvent('mousedown', opts));
        el.dispatchEvent(new MouseEvent('mouseup', opts));
        el.dispatchEvent(new MouseEvent('click', opts));
        return true;
      },
      findSubAreas() {
        const sr = mainShadow();
        if (!sr) return [];
        const re = /^(\d+(?:\.5)?)F$|^B(\d+)F$|^BM$/;
        const groups = [];
        const seen = new Set();
        for (const g of sr.querySelectorAll('g')) {
          if (seen.has(g.id)) continue;
          const txt = groupText(g);
          const m = txt && txt.match(re);
          if (!m) continue;
          seen.add(g.id);
          let floor = 0;
          if (txt === 'BM') floor = -2;
          else if (m[1]) floor = parseFloat(m[1]);
          else if (m[2]) floor = -parseInt(m[2], 10);
          const r = g.getBoundingClientRect();
          groups.push({ id: g.id, floor, text: txt, x: Math.round(r.left), y: Math.round(r.top) });
        }
        if (groups.length > 0) {
          const ys = groups.filter(g => g.text !== 'BM').map(g => g.y).sort((a, b) => a - b);
          if (ys.length > 0) {
            const median = ys[Math.floor(ys.length / 2)];
            return groups.filter(g => g.text === 'BM' || Math.abs(g.y - median) <= 30);
          }
        }
        return groups;
      },
      findPageBtns() {
        const sr = mainShadow();
        if (!sr) return {};
        const out = {};
        const re = /^(首页|上页|末页|下页|[一二三四五六七八九十]页)$/;
        for (const g of sr.querySelectorAll('g')) {
          const txt = groupText(g);
          if (re.test(txt)) out[txt] = g.id;
        }
        return out;
      },
      findSubTabs() {
        const sr = mainShadow();
        if (!sr) return [];
        const tabs = [];
        for (const g of sr.querySelectorAll('g')) {
          const txt = groupText(g);
          if (txt === '裙楼' || txt === '塔楼') {
            const r = g.getBoundingClientRect();
            if (r.width > 0 && r.height > 0) {
              tabs.push({
                txt,
                id: g.id,
                x: Math.round(r.left),
                y: Math.round(r.top),
                isActive: g.classList.contains('is-active') || g.classList.contains('active') || g.getAttribute('aria-selected') === 'true',
              });
            }
          }
        }
        const seen = new Set(tabs.map(t => t.txt));
        for (const el of document.querySelectorAll('[class*="tab"], [class*="Tab"]')) {
          const txt = (el.textContent || '').trim();
          if (!txt || seen.has(txt)) continue;
          if (txt === '群控' || txt === '公区' || txt === '裙楼' || txt === '塔楼') {
            const r = el.getBoundingClientRect();
            if (r.width > 0 && r.height > 0) {
              const active = el.classList.contains('is-active') || el.classList.contains('active') || el.getAttribute('aria-selected') === 'true';
              tabs.push({ txt, id: null, mainDom: true, x: Math.round(r.left), y: Math.round(r.top), isActive: active });
              seen.add(txt);
            }
          }
        }
        return tabs;
      },
      clickMainDomTab(tab) {
        for (const el of document.querySelectorAll('[class*="tab"], [class*="Tab"]')) {
          const txt = (el.textContent || '').trim();
          if (txt === tab.txt && Math.abs(el.getBoundingClientRect().left - tab.x) <= 3) {
            el.click();
            return true;
          }
        }
        return false;
      },
      cardStateFor(x, y) {
        const sr = mainShadow();
        const svg = sr && sr.querySelector('svg');
        if (!svg) return { card_comm: '', card_switch: '', card_indicator: '', card_state_source: 'missing_svg' };
        const imgs = [...svg.querySelectorAll('image')].map(img => {
          const r = img.getBoundingClientRect();
          return {
            x: Math.round(r.left + r.width / 2),
            y: Math.round(r.top + r.height / 2),
            w: Math.round(r.width),
            h: Math.round(r.height),
            href: (img.getAttribute('href') || img.getAttribute('xlink:href') || '').split('/').pop(),
          };
        }).filter(img => img.href);
        const nearest = (arr, tx, ty, xMax, yMax) => {
          let best = null;
          let bestDist = Infinity;
          for (const item of arr) {
            const dx = Math.abs(item.x - tx);
            const dy = Math.abs(item.y - ty);
            if (dx > xMax || dy > yMax) continue;
            const dist = dx * dx + dy * dy;
            if (dist < bestDist) {
              bestDist = dist;
              best = item;
            }
          }
          return best;
        };
        const switchImgs = imgs.filter(img => img.w >= 38 && img.w <= 50 && img.h >= 17 && img.h <= 30);
        const switchCounts = {};
        for (const img of switchImgs) switchCounts[img.href] = (switchCounts[img.href] || 0) + 1;
        const switchHrefs = Object.keys(switchCounts).sort((a, b) => switchCounts[b] - switchCounts[a]);
        const offHref = switchHrefs[0] || '';
        const onHref = switchHrefs[1] || '';
        const indicatorImgs = imgs.filter(img => img.w >= 25 && img.w <= 33 && img.h >= 23 && img.h <= 31);
        const sw = nearest(switchImgs, x, y + 60, 90, 90) || nearest(switchImgs, x, y + 100, 90, 70);
        const indicator = nearest(indicatorImgs, x, y - 30, 90, 55);
        const indMap = {
          '3bdc38eda0ae77f26807b2b6cdde4456.png': '关机',
          '56f45bb314d74cc8da6c6c8e5942d08d.png': '开机',
          '833bea6e66e7ab0e55704d655e135c7c.png': '离线',
        };
        const cardComm = indicator ? (indMap[indicator.href] || '') : '';
        let cardSwitch = '';
        if (cardComm === '开机') cardSwitch = 'ON';
        else if (cardComm === '关机') cardSwitch = 'OFF';
        else if (cardComm === '离线') cardSwitch = '-';
        else if (sw && onHref && sw.href === onHref) cardSwitch = 'ON';
        else if (sw && offHref && sw.href === offHref) cardSwitch = 'OFF';
        return {
          card_comm: cardComm,
          card_switch: cardSwitch,
          card_indicator: indicator ? indicator.href : '',
          card_switch_indicator: sw ? sw.href : '',
          card_state_source: cardComm ? 'svg_indicator' : (sw ? 'svg_switch' : 'unknown'),
        };
      },
      getDevices() {
        const widget = mainWidget();
        const sr = mainShadow();
        const sl = widget?.__vue__?.$data?.svgListDraw || [];
        if (!widget || !sr || !sl.length) return [];
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
            const x = Math.round(r.left + r.width / 2);
            const y = Math.round(r.top + r.height / 2);
            const state = window.__ems_rt.cardStateFor(x, y);
            devices.push({
              name: names[devId] || '',
              devId,
              meterId: toNum(ptPath._CollPointMeterId || ptPath.ptId),
              rtuId: toNum(ptPath._CollPointRtuId),
              ptPath,
              elementId: e.id,
              templateSvgName: ld.ScriptName || '',
              x,
              y,
              ...state,
            });
          }
        }
        return devices.filter(d => d.name && d.name !== '0-0001-KT').sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN'));
      },
    };
  });
}

async function waitForReady(page, maxRetries = 30) {
  for (let i = 0; i < maxRetries; i++) {
    const ok = await page.evaluate(() => window.__ems_rt && window.__ems_rt.isReady()).catch(() => false);
    if (ok) return true;
    await pause(200);
  }
  return false;
}

async function waitForDevices(page, opts = {}) {
  const maxRetries = opts.maxRetries || 40;
  const waitMs = opts.waitMs || 150;
  let prevSig = '';
  let stable = 0;
  for (let i = 0; i < maxRetries; i++) {
    const devices = await page.evaluate(() => window.__ems_rt ? window.__ems_rt.getDevices() : []).catch(() => []);
    const real = devices.filter(d => d.name && d.name !== '0-0001-KT');
    const sig = real.map(d => `${d.devId}:${d.name}`).join('|');
    if (real.length > 0 && sig === prevSig) stable++;
    else stable = 0;
    if (real.length > 0 && stable >= 1) return real;
    prevSig = sig;
    await pause(waitMs);
  }
  return await page.evaluate(() => window.__ems_rt ? window.__ems_rt.getDevices() : []).catch(() => []);
}

async function clickMenu(page, menuMatch) {
  await closeModals(page);
  const clicked = await page.evaluate((match) => {
    const r = new RegExp('^' + match);
    for (const el of document.querySelectorAll('.ivu-menu-item')) {
      const t = (el.textContent || '').trim();
      if (r.test(t) && /楼|空调|开闭所|服务/.test(t)) {
        el.dispatchEvent(new MouseEvent('click', { bubbles: true }));
        return t;
      }
    }
    return null;
  }, menuMatch);
  await injectHelpers(page).catch(() => {});
  return clicked;
}

async function clickMenuReady(page, menuMatch) {
  for (let i = 0; i < 3; i++) {
    const clicked = await clickMenu(page, menuMatch);
    if (!clicked) return { clicked: null, ready: false };
    if (await waitForReady(page, 30)) return { clicked, ready: true };
    await pause(600);
  }
  return { clicked: null, ready: false };
}

async function clickDeviceIcon(page, dev) {
  const clicked = await page.evaluate((target) => window.__ems_rt && window.__ems_rt.clickById(target.elementId), dev).catch(() => false);
  if (clicked) return 'elementId';
  await page.mouse.click(dev.x, dev.y);
  return 'coordinate';
}

async function readTemplateModal(page, targetName, timeoutMs = READY_TIMEOUT_MS) {
  const started = Date.now();
  await page.waitForFunction(({ name, fieldOrder, defaultTemplateValues }) => {
    const fieldSet = new Set(fieldOrder);
    const normalize = v => String(v || '').replace(/\s+/g, '').replace(/\.0(?=℃?$)/, '');
    const visible = el => {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return r.width > 100 && r.height > 100 && cs.display !== 'none' && cs.visibility !== 'hidden';
    };
    const fieldsFromSvg = svg => {
      const svgBox = svg.getBoundingClientRect();
      const texts = [...svg.querySelectorAll('text')].map(t => {
        const r = t.getBoundingClientRect();
        return { text: (t.textContent || '').trim(), x: r.left - svgBox.left, y: r.top - svgBox.top };
      }).filter(t => t.text);
      const fields = {};
      for (const label of texts.filter(t => fieldSet.has(t.text))) {
        const inLeft = label.x < 200;
        const value = texts.filter(v =>
          Math.abs(v.y - label.y) <= 5 &&
          v.x >= (inLeft ? 150 : 350) &&
          v.x <= (inLeft ? 220 : 455) &&
          !v.text.includes('空调实时数据') &&
          !/KT/.test(v.text) &&
          !fieldSet.has(v.text)
        ).sort((a, b) => a.x - b.x)[0];
        if (value) fields[label.text] = value.text;
      }
      return fields;
    };
    const isDefaultLike = fields => {
      let matched = 0;
      let checked = 0;
      for (const [field, expected] of Object.entries(defaultTemplateValues)) {
        if (!(field in fields)) continue;
        checked++;
        if (normalize(fields[field]) === normalize(expected)) matched++;
      }
      return checked >= 8 && matched >= Math.max(8, Math.floor(checked * 0.75));
    };
    for (const body of [...document.querySelectorAll('.ivu-modal-body')].filter(visible)) {
      const svg = body.querySelector('.pi-svg-container')?.shadowRoot?.querySelector('svg');
      const text = svg ? (svg.textContent || '') : '';
      if (!svg || !text.includes('空调实时数据') || !text.includes(name) || text.includes('0-0001-KT')) continue;
      const widget = [...body.querySelectorAll('.pi-graphics-configuration-svg-new')].find(el => el.__vue__);
      const data = widget?.__vue__?.$data || {};
      const runConfCount = Array.isArray(data.runConfDataProp) ? data.runConfDataProp.length : 0;
      const ws = data.websocketDataProp || {};
      const wsEntries = Array.isArray(ws) ? ws : Object.values(ws);
      const tagCount = wsEntries.filter(item => {
        const tag = item && item.tag ? item.tag : item;
        return tag && tag.value !== undefined && tag.value !== null;
      }).length;
      const minTags = runConfCount >= 40 ? Math.max(20, Math.floor(runConfCount * 0.7)) : 20;
      const fields = fieldsFromSvg(svg);
      if (tagCount >= minTags && !isDefaultLike(fields)) return true;
    }
    return false;
  }, { name: targetName, fieldOrder: FIELD_ORDER, defaultTemplateValues: DEFAULT_TEMPLATE_VALUES }, { timeout: timeoutMs, polling: 50 });

  const detail = await page.evaluate(({ fieldOrder, targetName, defaultTemplateValues }) => {
    const fieldSet = new Set(fieldOrder);
    const normalize = v => String(v || '').replace(/\s+/g, '').replace(/\.0(?=℃?$)/, '');
    const visible = el => {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return r.width > 100 && r.height > 100 && cs.display !== 'none' && cs.visibility !== 'hidden';
    };
    const body = [...document.querySelectorAll('.ivu-modal-body')].find(el => {
      if (!visible(el)) return false;
      const svg = el.querySelector('.pi-svg-container')?.shadowRoot?.querySelector('svg');
      const text = svg ? (svg.textContent || '') : '';
      return text.includes('空调实时数据') && text.includes(targetName) && !text.includes('0-0001-KT');
    });
    if (!body) return { fields: {} };
    const svg = body.querySelector('.pi-svg-container').shadowRoot.querySelector('svg');
    const svgBox = svg.getBoundingClientRect();
    const texts = [...svg.querySelectorAll('text')].map(t => {
      const r = t.getBoundingClientRect();
      return { text: (t.textContent || '').trim(), x: r.left - svgBox.left, y: r.top - svgBox.top };
    }).filter(t => t.text);
    const fields = {};
    const labels = texts.filter(t => fieldSet.has(t.text));
    for (const label of labels) {
      const inLeft = label.x < 200;
      const value = texts.filter(v =>
        Math.abs(v.y - label.y) <= 5 &&
        v.x >= (inLeft ? 150 : 350) &&
        v.x <= (inLeft ? 220 : 455) &&
        !v.text.includes('空调实时数据') &&
        !/KT/.test(v.text) &&
        !fieldSet.has(v.text)
      ).sort((a, b) => a.x - b.x)[0];
      if (value) fields[label.text] = value.text;
    }
    const widget = [...body.querySelectorAll('.pi-graphics-configuration-svg-new')].find(el => el.__vue__);
    const data = widget?.__vue__?.$data || {};
    const ws = data.websocketDataProp || {};
    const wsEntries = Array.isArray(ws) ? ws : Object.values(ws);
    const realtimeTagCount = wsEntries.filter(item => {
      const tag = item && item.tag ? item.tag : item;
      return tag && tag.value !== undefined && tag.value !== null;
    }).length;
    const realtimeValidTagCount = wsEntries.filter(item => {
      const tag = item && item.tag ? item.tag : item;
      return tag && tag.value !== undefined && tag.value !== null && tag.valid !== false;
    }).length;
    const runConfCount = Array.isArray(data.runConfDataProp) ? data.runConfDataProp.length : 0;
    let matched = 0;
    let checked = 0;
    for (const [field, expected] of Object.entries(defaultTemplateValues)) {
      if (!(field in fields)) continue;
      checked++;
      if (normalize(fields[field]) === normalize(expected)) matched++;
    }
    return {
      deviceName: texts.find(t => t.text === targetName)?.text || '',
      fieldCount: Object.keys(fields).length,
      runConfCount,
      realtimeTagCount,
      realtimeValidTagCount,
      defaultMatchCount: matched,
      defaultCheckedCount: checked,
      defaultLike: checked >= 8 && matched >= Math.max(8, Math.floor(checked * 0.75)),
      fields,
    };
  }, { fieldOrder: FIELD_ORDER, targetName, defaultTemplateValues: DEFAULT_TEMPLATE_VALUES });
  return { ...detail, loadMs: Math.max(0, Date.now() - started) };
}

async function openReusableModal(page, dev) {
  await closeModals(page);
  const clickMethod = await clickDeviceIcon(page, dev);
  await page.waitForFunction(name => {
    const visible = el => {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return r.width > 100 && r.height > 100 && cs.display !== 'none' && cs.visibility !== 'hidden';
    };
    return [...document.querySelectorAll('.ivu-modal-body')].some(body => {
      if (!visible(body)) return false;
      const svg = body.querySelector('.pi-svg-container')?.shadowRoot?.querySelector('svg');
      const text = svg ? (svg.textContent || '') : '';
      return text.includes('空调实时数据') && text.includes(name);
    });
  }, dev.name, { timeout: READY_TIMEOUT_MS, polling: 100 });
  return clickMethod;
}

async function readReusableModalRuntime(page, dev) {
  return await page.evaluate(({ target, fieldOrder, fieldAlias, celsiusFields }) => {
    const fieldSet = new Set(fieldOrder);
    const celsiusFieldSet = new Set(celsiusFields);
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
    if (!widget) return { error: 'modal widget not found', fields: {} };

    const data = widget.__vue__.$data || {};
    const rc = Array.isArray(data.runConfDataProp) ? data.runConfDataProp : [];
    const ws = data.websocketDataProp || [];
    const entries = Array.isArray(ws) ? ws : Object.values(ws);
    const tagEntries = entries.filter(item => {
      const tag = item && item.tag ? item.tag : item;
      return tag && tag.value !== undefined && tag.value !== null;
    });

    const confByKey = {};
    const confByIndex = {};
    for (let i = 0; i < rc.length; i++) {
      const item = rc[i] || {};
      const conf = item.ptPathConf || item.conf || item;
      if (item.ptPathKey) confByKey[item.ptPathKey] = conf;
      confByIndex[i] = conf;
    }

    const fields = {};
    const rawFields = {};
    const validFields = {};
    const sourceRankByField = {};
    const addField = (name, conf, tag) => {
      if (!name || !tag || tag.value === undefined || tag.value === null) return;
      const fieldName = fieldAlias[name] || name;
      if (!fieldSet.has(fieldName)) return;
      const raw = String(tag.value);
      const enumHit = Array.isArray(conf.enumDefine)
        ? conf.enumDefine.find(item => String(item.Key ?? item.key) === raw)
        : null;
      let value = enumHit ? (enumHit.Value ?? enumHit.value) : raw;
      const unit = celsiusFieldSet.has(fieldName) ? '℃' : '';
      if (!enumHit && unit && value !== '' && !String(value).includes(unit)) value = `${value} ${unit}`;
      const sourceRank = conf.devId === target.devId ? 2 : (conf.devId ? 1 : 0);
      const existingRank = sourceRankByField[fieldName] || 0;
      const existingHasUnit = /\d\s*℃/.test(fields[fieldName] || '');
      const incomingHasUnit = /\d\s*℃/.test(String(value));
      const shouldReplace =
        !fields[fieldName] ||
        sourceRank > existingRank ||
        (sourceRank === existingRank && incomingHasUnit && !existingHasUnit);
      if (shouldReplace) {
        fields[fieldName] = String(value);
        rawFields[fieldName] = raw;
        validFields[fieldName] = tag.valid !== undefined ? !!tag.valid : null;
        sourceRankByField[fieldName] = sourceRank;
      }
    };

    for (const [key, item] of Object.entries(ws || {})) {
      const tag = item && item.tag ? item.tag : item;
      let conf = item?.ptPathKey ? confByKey[item.ptPathKey] : null;
      if (!conf && /^\d+$/.test(key)) conf = confByIndex[Number(key)];
      if (!conf && item?.ptPathKey) conf = confByKey[item.ptPathKey];
      if (!conf) continue;
      addField(conf.name || conf.measurandName || '', conf, tag);
    }

    const svg = body.querySelector('.pi-svg-container')?.shadowRoot?.querySelector('svg');
    const text = (svg?.textContent || '').replace(/\s+/g, ' ');
    return {
      deviceName: text.includes(target.name) ? target.name : '',
      titleHasTarget: text.includes(target.name),
      runConfCount: rc.length,
      realtimeTagCount: tagEntries.length,
      realtimeValidTagCount: tagEntries.filter(item => {
        const tag = item && item.tag ? item.tag : item;
        return tag && tag.value !== undefined && tag.value !== null && tag.valid !== false;
      }).length,
      fieldCount: Object.keys(fields).length,
      fields,
      rawFields,
      validFields,
      runConfDevIds: [...new Set(rc.map(item => item?.ptPathConf?.devId || item?.ptPathConf?.ptId).filter(Boolean))].slice(0, 20),
    };
  }, { target: dev, fieldOrder: FIELD_ORDER, fieldAlias: FIELD_ALIAS, celsiusFields: [...CELSIUS_FIELDS] });
}

function validateReusableDetail(detail, dev, configReady) {
  const score = defaultTemplateScore(detail.fields || {});
  const minTags = (detail.runConfCount || 48) >= 40 ? Math.max(20, Math.floor((detail.runConfCount || 48) * 0.7)) : 20;
  const fieldCount = detail.fieldCount || Object.keys(detail.fields || {}).length;
  let error = detail.error || '';
  if (!error && !configReady) error = 'reuse config not ready';
  if (!error && (detail.realtimeTagCount || 0) < minTags) error = `reuse tags not ready: ${detail.realtimeTagCount || 0}/${minTags}`;
  if (!error && fieldCount < 20) error = `reuse fields incomplete: ${fieldCount}`;
  if (!error && score.isDefaultLike) error = 'reuse default-like values';
  return { score, minTags, fieldCount, error };
}

function reusableConfigMatches(detail, dev) {
  const ids = detail.runConfDevIds || [];
  return ids.includes(dev.devId) && !ids.some(id => id !== dev.devId && id !== 20000001);
}

async function captureDeviceDetailByReusableModal(page, dev) {
  const started = Date.now();
  const existingDeadline = Date.now() + Math.min(1200, READY_TIMEOUT_MS);
  while (Date.now() < existingDeadline) {
    const existing = await readReusableModalRuntime(page, dev).catch(err => ({ error: err.message, fields: {} }));
    const configReady = reusableConfigMatches(existing, dev);
    if (!configReady) break;
    const check = validateReusableDetail(existing, dev, configReady);
    if (!check.error) {
      return {
        ...existing,
        configReady,
        defaultMatchCount: check.score.matched,
        defaultCheckedCount: check.score.checked,
        defaultLike: check.score.isDefaultLike,
        error: '',
        loadMs: Math.max(0, Date.now() - started),
      };
    }
    await pause(50);
  }

  const switchResult = await page.evaluate(target => {
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
    const list = data.ptPathList;
    if (!list || typeof list.forEach !== 'function') return { error: 'ptPathList missing' };

    const targetPath = clone(target.ptPath);
    const targetPathJson = JSON.stringify(targetPath);
    if (vue.clearSubscribe) vue.clearSubscribe();
    try { vue.devPath = targetPathJson; } catch {}
    if (vue.$props) vue.$props.devPath = targetPathJson;
    if (vue.$options?.propsData) vue.$options.propsData.devPath = targetPathJson;

    if (Array.isArray(data.websocketDataProp)) data.websocketDataProp.splice(0, data.websocketDataProp.length);
    else data.websocketDataProp = [];
    if (Array.isArray(data.runConfDataProp)) data.runConfDataProp.splice(0, data.runConfDataProp.length);

    list.forEach(item => {
      if (!item || !item.ptPathValue) return;
      const pv = item.ptPathValue;
      if (pv.propertyName) {
        pv.ptPath = clone(targetPath);
      } else if (pv.devPath || pv.ptPath?.dataType === 'pttype') {
        pv.devPath = clone(targetPath);
      }
    });

    if (vue.getSvgRunConfData) vue.getSvgRunConfData();
    else if (vue.subscribe) vue.subscribe();
    return { ok: true };
  }, dev).catch(err => ({ error: err.message }));

  if (switchResult.error) return { ...switchResult, fields: {}, loadMs: Math.max(0, Date.now() - started) };

  const configReady = await page.waitForFunction(targetId => {
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
    const rc = widget?.__vue__?.$data?.runConfDataProp || [];
    return Array.isArray(rc) &&
      rc.length >= 40 &&
      rc.some(item => item?.ptPathConf?.devId === targetId) &&
      !rc.some(item => item?.ptPathConf?.devId && item.ptPathConf.devId !== targetId && item.ptPathConf.devId !== 20000001);
  }, dev.devId, { timeout: READY_TIMEOUT_MS, polling: 50 }).then(() => true).catch(() => false);

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
    const data = vue?.$data || {};
    if (!vue) return;
    if (vue.clearSubscribe) vue.clearSubscribe();
    if (Array.isArray(data.websocketDataProp)) data.websocketDataProp.splice(0, data.websocketDataProp.length);
    else data.websocketDataProp = [];
    if (vue.subscribe) vue.subscribe();
  }).catch(() => {});

  let best = null;
  const deadline = Date.now() + READY_TIMEOUT_MS;
  while (Date.now() < deadline) {
    const snap = await readReusableModalRuntime(page, dev).catch(err => ({ error: err.message, fields: {}, realtimeTagCount: 0 }));
    if (!best || (snap.realtimeTagCount || 0) > (best.realtimeTagCount || 0)) best = snap;
    const minTags = (snap.runConfCount || 48) >= 40 ? Math.max(20, Math.floor((snap.runConfCount || 48) * 0.7)) : 20;
    if ((snap.realtimeTagCount || 0) >= minTags) break;
    await pause(50);
  }

  const detail = best || { fields: {}, realtimeTagCount: 0, runConfCount: 0 };
  const check = validateReusableDetail(detail, dev, configReady);

  return {
    ...detail,
    configReady,
    defaultMatchCount: check.score.matched,
    defaultCheckedCount: check.score.checked,
    defaultLike: check.score.isDefaultLike,
    error: check.error,
    loadMs: Math.max(0, Date.now() - started),
  };
}

async function captureDeviceDetail(page, dev) {
  const attempts = [
    { reset: false },
    { reset: true },
  ];
  let last = { error: 'not attempted', fields: {} };
  for (let i = 0; i < attempts.length; i++) {
    if (attempts[i].reset) await closeModals(page);
    const clickMethod = await clickDeviceIcon(page, dev);
    const detail = await readTemplateModal(page, dev.name).catch(err => ({ error: err.message, fields: {} }));
    detail.clickMethod = clickMethod;
    detail.retry = i;
    if (!detail.error && detail.deviceName && detail.deviceName !== dev.name) detail.error = `device mismatch: ${detail.deviceName}`;
    last = detail;
    if (!detail.error && Object.keys(detail.fields || {}).length > 0 && !detail.defaultLike) return detail;
  }
  return last;
}

async function collectCurrentPageDetails(page, pageMeta, rows, ndjsonStream, startedAt, targetNames = null) {
  let devices = await waitForDevices(page);
  if (targetNames) devices = devices.filter(d => targetNames.has(d.name));
  if (MAX_DEVICES > 0) devices = devices.slice(0, Math.max(0, MAX_DEVICES - rows.length));
  if (INVENTORY_ONLY) {
    console.log(`[PAGE] ${pageMeta.subAreaText} ${pageMeta.pageName}: ${devices.length} devices (inventory)`);
    for (const dev of devices) {
      if (MAX_DEVICES > 0 && rows.length >= MAX_DEVICES) return;
      const row = {
        building: BUILDING,
        ...pageMeta,
        name: dev.name,
        devId: dev.devId,
        meterId: dev.meterId,
        rtuId: dev.rtuId,
        template: dev.templateSvgName,
        loadMs: null,
        fieldCount: 0,
        realtimeTagCount: 0,
        realtimeValidTagCount: 0,
        runConfCount: 0,
        defaultLike: false,
        defaultMatchCount: 0,
        defaultCheckedCount: 0,
        retry: 0,
        clickMethod: 'inventory',
        reuseError: '',
        error: '',
        cardComm: dev.card_comm || '',
        cardSwitch: dev.card_switch || '',
        cardIndicator: dev.card_indicator || '',
        cardSwitchIndicator: dev.card_switch_indicator || '',
        cardStateSource: dev.card_state_source || '',
        fields: {},
      };
      rows.push(row);
      ndjsonStream.write(JSON.stringify(row) + '\n');
    }
    console.log('[SUMMARY]', JSON.stringify(summarize(rows, startedAt)));
    return;
  }
  console.log(`[PAGE] ${pageMeta.subAreaText} ${pageMeta.pageName}: ${devices.length} devices${REUSE_MODAL ? ' (reuse-modal)' : ''}`);
  let reusableReady = false;
  let reusableOpenError = '';
  if (REUSE_MODAL && devices.length > 0) {
    try {
      await openReusableModal(page, devices[0]);
      reusableReady = true;
    } catch (err) {
      reusableOpenError = err.message || String(err);
      await closeModals(page);
      console.log(`[WARN] reuse modal open failed: ${reusableOpenError}`);
    }
  }
  for (const dev of devices) {
    if (MAX_DEVICES > 0 && rows.length >= MAX_DEVICES) return;
    process.stdout.write(`\r[DEV] ${rows.length + 1} ${pageMeta.subAreaText} ${pageMeta.pageName} ${dev.name}`.padEnd(120));
    let detail = null;
    let reuseError = '';
    if (reusableReady) {
      detail = await captureDeviceDetailByReusableModal(page, dev).catch(err => ({ error: err.message, fields: {} }));
      reuseError = detail.error || '';
      if (reuseError) {
        await closeModals(page);
        detail = await captureDeviceDetail(page, dev);
        if (!detail.error && devices.indexOf(dev) < devices.length - 1) {
          try {
            await openReusableModal(page, dev);
            reusableReady = true;
          } catch (err) {
            reusableReady = false;
            reusableOpenError = err.message || String(err);
          }
        }
      }
    } else {
      detail = await captureDeviceDetail(page, dev);
    }
    const row = {
      building: BUILDING,
      ...pageMeta,
      name: detail.deviceName || dev.name,
      devId: dev.devId,
      meterId: dev.meterId,
      rtuId: dev.rtuId,
      template: dev.templateSvgName,
      loadMs: detail.loadMs || null,
      fieldCount: detail.fieldCount || Object.keys(detail.fields || {}).length,
      realtimeTagCount: detail.realtimeTagCount || 0,
      realtimeValidTagCount: detail.realtimeValidTagCount || 0,
      runConfCount: detail.runConfCount || 0,
      defaultLike: !!detail.defaultLike,
      defaultMatchCount: detail.defaultMatchCount || 0,
      defaultCheckedCount: detail.defaultCheckedCount || 0,
      retry: detail.retry || 0,
      clickMethod: detail.clickMethod || (reuseError ? 'reuse-fallback' : (reusableReady ? 'reuse-modal' : '')),
      reuseError: reuseError || reusableOpenError || '',
      error: detail.error || '',
      cardComm: dev.card_comm || '',
      cardSwitch: dev.card_switch || '',
      cardIndicator: dev.card_indicator || '',
      cardSwitchIndicator: dev.card_switch_indicator || '',
      cardStateSource: dev.card_state_source || '',
      fields: detail.fields || {},
    };
    rows.push(row);
    ndjsonStream.write(JSON.stringify(row) + '\n');
  }
  process.stdout.write('\n');
  console.log('[SUMMARY]', JSON.stringify(summarize(rows, startedAt)));
  await closeModals(page);
}

async function collectPagesForCurrentArea(page, pageMetaBase, rows, ndjsonStream, startedAt, targetPages = null) {
  await waitForReady(page);
  await waitForDevices(page);
  let btns = await page.evaluate(() => window.__ems_rt.findPageBtns()).catch(() => ({}));
  let pageLabels = [...new Set(Object.keys(btns))]
    .filter(k => !['首页', '上页', '下页', '末页'].includes(k))
    .sort((a, b) => pageOrder(a) - pageOrder(b));

  if (pageLabels.length === 0) {
    await pause(300);
    btns = await page.evaluate(() => window.__ems_rt.findPageBtns()).catch(() => ({}));
    pageLabels = [...new Set(Object.keys(btns))]
      .filter(k => !['首页', '上页', '下页', '末页'].includes(k))
      .sort((a, b) => pageOrder(a) - pageOrder(b));
  }

  if (pageLabels.length === 0) {
    if (btns['下页']) {
      let pageNum = 1;
      let previousSig = '';
      while (true) {
        const cn = ['', '一', '二', '三', '四', '五', '六', '七', '八', '九', '十'][pageNum] || String(pageNum);
        const meta = { ...pageMetaBase, pageName: `${cn}页` };
        const beforeCount = rows.length;
        if (!targetPages || targetPages.has(pageKey(meta))) {
          await collectCurrentPageDetails(page, meta, rows, ndjsonStream, startedAt, targetPages && targetPages.get(pageKey(meta)));
        }
        const pageRows = rows.slice(beforeCount);
        const sig = pageRows.map(r => `${r.devId}:${r.name}`).join('|');
        if (sig && sig === previousSig) {
          rows.splice(beforeCount, rows.length - beforeCount);
          break;
        }
        previousSig = sig;

        const curBtns = await page.evaluate(() => window.__ems_rt.findPageBtns()).catch(() => ({}));
        const nextId = curBtns['下页'];
        if (!nextId) break;
        await closeModals(page);
        const clicked = await page.evaluate(id => window.__ems_rt.clickById(id), nextId).catch(() => false);
        if (!clicked) break;
        await pause(250);
        await waitForReady(page, 20);
        await waitForDevices(page);
        pageNum++;
        if (pageNum > 20) break;
      }
      return;
    }
    const meta = { ...pageMetaBase, pageName: 'default' };
    if (!targetPages || targetPages.has(pageKey(meta))) {
      await collectCurrentPageDetails(page, meta, rows, ndjsonStream, startedAt, targetPages && targetPages.get(pageKey(meta)));
    }
    return;
  }

  const firstLabel = pageLabels[0] || '一页';
  const firstMeta = { ...pageMetaBase, pageName: firstLabel };
  if (!targetPages || targetPages.has(pageKey(firstMeta))) {
    await collectCurrentPageDetails(page, firstMeta, rows, ndjsonStream, startedAt, targetPages && targetPages.get(pageKey(firstMeta)));
  }
  for (const label of pageLabels.slice(1)) {
    const meta = { ...pageMetaBase, pageName: label };
    if (targetPages && !targetPages.has(pageKey(meta))) continue;
    const curBtns = await page.evaluate(() => window.__ems_rt.findPageBtns()).catch(() => ({}));
    const id = curBtns[label];
    if (!id) {
      console.log(`[WARN] page button not found: ${pageMetaBase.subAreaText} ${label}`);
      continue;
    }
    await closeModals(page);
    await page.evaluate(btnId => window.__ems_rt.clickById(btnId), id).catch(() => false);
    await pause(150);
    await waitForReady(page, 20);
    await waitForDevices(page);
    await collectCurrentPageDetails(page, meta, rows, ndjsonStream, startedAt, targetPages && targetPages.get(pageKey(meta)));
  }
}

async function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const ts = timestamp();
  const prefix = INVENTORY_ONLY ? 'devices' : 'realtime';
  const jsonPath = path.join(OUT_DIR, `${prefix}_${BUILDING}_${ts}.json`);
  const ndjsonPath = path.join(OUT_DIR, `${prefix}_${BUILDING}_${ts}.ndjson`);
  const latestPath = path.join(OUT_DIR, `${prefix}_${BUILDING}_latest.json`);
  const failedTargets = buildFailedTargets(FAILED_FROM);
  const ndjsonStream = fs.createWriteStream(ndjsonPath, { flags: 'a' });
  const startedAt = Date.now();
  const rows = [];

  const { page } = await ensureRealtimeBrowser({
    cdpUrl: EFFECTIVE_CDP_URL,
    mode: BROWSER_MODE,
    strictCdp: STRICT_CDP,
    log: msg => console.log(msg),
  });
  await injectHelpers(page);

  const menu = await clickMenuReady(page, BUILDING);
  if (!menu.ready) throw new Error(`Building menu not ready: ${BUILDING}`);
  const rawSubAreas = await page.evaluate(() => window.__ems_rt.findSubAreas());
  const subAreas = rawSubAreas.sort((a, b) => a.floor - b.floor || a.x - b.x);
  let selectedSubAreas = MAX_SUBAREAS > 0 ? subAreas.slice(0, MAX_SUBAREAS) : subAreas;
  if (failedTargets) {
    const failedSubAreas = new Set([...failedTargets.pages.keys()].map(k => k.split('|')[0]));
    selectedSubAreas = selectedSubAreas.filter(sa => failedSubAreas.has(sa.text));
    console.log(`[RECAPTURE] failed rows=${failedTargets.failedRows.length}, failed pages=${failedTargets.pages.size}`);
  }
  console.log(`[START] ${BUILDING}: ${selectedSubAreas.length}/${subAreas.length} sub-areas, out=${jsonPath}`);

  const visited = new Set();
  for (let saIdx = 0; saIdx < selectedSubAreas.length; saIdx++) {
    if (MAX_DEVICES > 0 && rows.length >= MAX_DEVICES) break;
    const target = selectedSubAreas[saIdx];
    const key = `${target.floor}|${target.x}|${target.y}`;
    if (visited.has(key)) continue;
    visited.add(key);
    if (saIdx > 0) {
      const reset = await clickMenuReady(page, BUILDING);
      if (!reset.ready) {
        console.log(`[WARN] reset menu failed before ${target.text}`);
        continue;
      }
    }
    await injectHelpers(page);
    const currentSubAreas = await page.evaluate(() => window.__ems_rt.findSubAreas()).catch(() => []);
    let candidates = currentSubAreas.filter(g => g.floor === target.floor);
    const matched = candidates.length === 1 ? candidates[0] :
      (candidates.length > 1 ? candidates.reduce((a, b) => Math.abs(a.x - target.x) < Math.abs(b.x - target.x) ? a : b) : null);
    if (!matched) {
      console.log(`[WARN] sub-area not found: ${target.text}`);
      continue;
    }
    await closeModals(page);
    const clicked = await page.evaluate(id => window.__ems_rt.clickById(id), matched.id).catch(() => false);
    if (!clicked) {
      console.log(`[WARN] sub-area click failed: ${target.text}`);
      continue;
    }
    await pause(250);
    await waitForReady(page, 30);
    await waitForDevices(page);

    console.log(`[AREA] ${saIdx + 1}/${selectedSubAreas.length} ${target.text}`);
    const baseMeta = { subAreaIdx: saIdx, floor: target.floor, subAreaText: target.text, tab: '' };
    await collectPagesForCurrentArea(page, baseMeta, rows, ndjsonStream, startedAt, failedTargets && failedTargets.pages);

    const tabs = await page.evaluate(() => window.__ems_rt.findSubTabs()).catch(() => []);
    if (tabs.length > 0) {
      const defaultNames = new Set(rows.filter(r => r.subAreaIdx === saIdx).map(r => r.name));
      for (const tab of tabs.sort((a, b) => b.txt.localeCompare(a.txt))) {
        if (MAX_DEVICES > 0 && rows.length >= MAX_DEVICES) break;
        if (tab.isActive) continue;
        await closeModals(page);
        const ok = tab.mainDom
          ? await page.evaluate(t => window.__ems_rt.clickMainDomTab(t), tab).catch(() => false)
          : await page.evaluate(id => window.__ems_rt.clickById(id), tab.id).catch(() => false);
        if (!ok) continue;
        await pause(250);
        await waitForReady(page, 30);
        await waitForDevices(page);
        const beforeCount = rows.length;
        await collectPagesForCurrentArea(page, { ...baseMeta, tab: tab.txt }, rows, ndjsonStream, startedAt, failedTargets && failedTargets.pages);
        const added = rows.slice(beforeCount).filter(r => !defaultNames.has(r.name));
        if (added.length !== rows.length - beforeCount) {
          rows.splice(beforeCount, rows.length - beforeCount, ...added);
          console.log(`[TAB] ${target.text} ${tab.txt}: removed duplicate rows`);
        }
      }
    }

    const result = { summary: summarize(rows, startedAt), rows };
    fs.writeFileSync(jsonPath, JSON.stringify(result, null, 2), 'utf8');
    if (!failedTargets && !IS_PARTIAL_RUN) fs.writeFileSync(latestPath, JSON.stringify(result, null, 2), 'utf8');
  }

  let result = { summary: summarize(rows, startedAt), rows };
  if (failedTargets) {
    const mergedRows = failedTargets.source.rows || [];
    const index = new Map(mergedRows.map((row, idx) => [rowKey(row), idx]));
    let replaced = 0;
    for (const row of rows) {
      const idx = index.get(rowKey(row));
      if (idx === undefined) continue;
      mergedRows[idx] = row;
      replaced++;
    }
    result = {
      summary: summarize(mergedRows, startedAt),
      recapture: {
        source: path.resolve(FAILED_FROM),
        attempted: rows.length,
        replaced,
        stillFailed: mergedRows.filter(r => r.error).length,
      },
      rows: mergedRows,
    };
  }
  fs.writeFileSync(jsonPath, JSON.stringify(result, null, 2), 'utf8');
  if (!IS_PARTIAL_RUN) fs.writeFileSync(latestPath, JSON.stringify(result, null, 2), 'utf8');
  ndjsonStream.end();
  console.log(`[DONE] ${jsonPath}`);
  console.log(JSON.stringify(result.summary, null, 2));
  process.exit(0);
}

main().catch(err => {
  console.error(err.stack || err);
  process.exit(1);
});
