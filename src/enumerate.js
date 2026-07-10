#!/usr/bin/env node
'use strict';

const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const { checkCardQuality, getZone, classifyPersistentDeviceAnomalyPage, normalizeKnownSourceDefects, classifyKnownMissingIndicatorPage, isAcceptedCaptureQualityReason } = require('./rules');
const { validateEnumData, formatValidation } = require('./enum-validator');
const { log: loggerLog, setLevel, setCategories, enableFileLog, close, LEVELS, CATEGORIES } = require('./logger');
// Compat: old-style log() defaults to INFO+ENUM
const LOG = { I: m => loggerLog(LEVELS.INFO, 'ENUM', m), D: (c, m, x) => loggerLog(LEVELS.DEBUG, c, m, x), 
  W: (c, m) => loggerLog(LEVELS.WARN, c, m), E: (c, m) => loggerLog(LEVELS.ERROR, c, m) };
function log(...args) { loggerLog(LEVELS.INFO, 'ENUM', ...args); }

// ===== Configuration =====
function argValue(name) {
  const hit = process.argv.find(a => a.startsWith(name + '='));
  return hit ? hit.slice(name.length + 1) : '';
}
const RAW_EMS_URL = argValue('--ems-url') || process.env.EMS_URL || 'http://172.29.248.4:8000/ui';
function normalizeEmsUrl(value) {
  try {
    const url = new URL(value);
    url.hash = '';
    return url.toString();
  } catch {
    return value;
  }
}
const EMS_URL = normalizeEmsUrl(RAW_EMS_URL);
const CDP_URL = argValue('--cdp-url') || process.env.CDP_URL || 'http://127.0.0.1:9222';
function isEmsPageUrl(url) {
  try {
    const expected = new URL(EMS_URL);
    const current = new URL(url);
    return current.host === expected.host && (current.pathname.includes('/ui') || expected.pathname.includes(current.pathname));
  } catch {
    return url.includes('172.29.248.4') || url.includes('localhost') || url.includes('/ui');
  }
}
const OUT_DIR = path.resolve(argValue('--out-dir') || process.env.EMS_OUT_DIR || path.resolve(__dirname, '..', 'out'));
const OUT_FILE = path.join(OUT_DIR, 'enum_full_v5.json');

// Network monitoring & diagnostics
const ENABLE_NETWORK_MONITOR = !process.argv.includes('--no-net-monitor');
const ENABLE_SELF_DIAGNOSE = process.argv.includes('--self-diagnose');
const DIAGNOSE_INTERVAL = 5000; // ms
const NETWORK_LOG_MAX = 500; // max network entries to keep in memory

const W = { PAGE_CLICK: 100, BM_CLICK: 500 };

const BUILDINGS = [
  { menuMatch: '1号', building: '1号' },
  { menuMatch: '2号', building: '2号' },
  { menuMatch: '3号', building: '3号' },
  { menuMatch: '4号', building: '4号' },
  { menuMatch: '5号', building: '5号' },
  { menuMatch: '6号', building: '6号' },
];

// ===== Mode selection =====
const USE_CDP = process.argv.includes('--edge');
const USE_AUTO_LAUNCH = process.argv.includes('--auto-launch');
const DRY_RUN = process.argv.includes('--dry');
const APPEND = process.argv.includes('--append');
const CHECK_LOGIN = !process.argv.includes('--skip-login-check');
const FAIL_IF_NOT_LOGGED_IN = process.argv.includes('--fail-if-not-logged-in');
const BUILDING_FILTER = process.argv.find(a => a.startsWith('--bldg='));
const FILTER = BUILDING_FILTER ? BUILDING_FILTER.split('=')[1].split(',').map(s => s.trim()).filter(Boolean) : null;
const RECAPTURE_ARG = process.argv.find(a => a.startsWith('--recapture='));
// Format: --recapture=3号:1087:144,6号:194:158
let RECAPTURE_TARGETS = [];
if (RECAPTURE_ARG) {
  const val = RECAPTURE_ARG.split('=')[1];
  RECAPTURE_TARGETS = val.split(',').map(s => {
    const [b, x, y] = s.split(':');
    return { building: b, x: parseInt(x), y: parseInt(y) };
  });
}
const RECAPTURE_MODE = RECAPTURE_TARGETS.length > 0;

// Logger configuration
const LOG_LEVEL_ARG = process.argv.find(a => a.startsWith('--log-level='));
if (LOG_LEVEL_ARG) setLevel(LOG_LEVEL_ARG.split('=')[1]);
const LOG_CAT_ARG = process.argv.find(a => a.startsWith('--log-category='));
if (LOG_CAT_ARG) setCategories(LOG_CAT_ARG.split('=')[1]);
const LOG_FILE = process.argv.includes('--log-file');
if (LOG_FILE) enableFileLog(OUT_DIR);

// Output: append mode appends each building run to existing file
let outputInitialized = false;
function loadExisting() {
  try { return JSON.parse(fs.readFileSync(OUT_FILE, 'utf-8')); }
  catch { return { buildings: [] }; }
}
function saveOutput(buildingResult) {
  // Non-append runs start a fresh JSON on first save, then accumulate buildings
  // captured during this process. Append runs keep old, not-yet-recaptured data.
  const existing = (APPEND || outputInitialized) ? loadExisting() : { buildings: [] };
  outputInitialized = true;
  if (!existing.buildings) existing.buildings = [];
  // Remove previous data for this building if exists
  existing.buildings = existing.buildings.filter(b => b.building !== buildingResult.building);
  // Insert in sorted order (1号→6号)
  existing.buildings.push(buildingResult);
  const order = ['1号', '2号', '3号', '4号', '5号', '6号'];
  existing.buildings.sort((a, b) => order.indexOf(a.building) - order.indexOf(b.building));
  existing.completedAt = new Date().toISOString();
  fs.mkdirSync(OUT_DIR, { recursive: true });
  fs.writeFileSync(OUT_FILE, JSON.stringify(existing, null, 2), 'utf-8');
}

// ===== Helpers =====
function pause(ms) { return new Promise(r => setTimeout(r, ms)); }

// ===== Enhanced Quality Assessment =====
function assessDataQuality(cards) {
  const n = cards.length;
  if (n === 0) return { isGood: false, score: 0, details: 'no cards' };

  const activeCards = cards.filter(c => c.comm === '开机' || c.comm === '关机');
  const activeN = activeCards.length;
  const switchRate = activeN > 0 ? activeCards.filter(c => c.switch === 'ON' || c.switch === 'OFF').length / activeN : 1;
  const tempRate = activeN > 0 ? activeCards.filter(c => {
    const indoor = parseFloat(c.indoor);
    const setTemp = parseFloat(c.setTemp);
    return Number.isFinite(indoor) && indoor > 0 && indoor <= 60 &&
      Number.isFinite(setTemp) && setTemp >= 5 && setTemp <= 40;
  }).length / activeN : 1;
  const commRate = cards.filter(c => c.comm).length / n;
  const modeRate = activeN > 0 ? activeCards.filter(c => c.mode !== '-' && c.fan !== '-' && c.fan !== '0').length / activeN : 1;
  const allOffline = cards.every(c => c.comm === '离线');
  const commComplete = cards.every(c => c.comm === '开机' || c.comm === '关机' || c.comm === '离线');
  
  const qualityScore = (switchRate * 0.4 + tempRate * 0.3 + commRate * 0.2 + modeRate * 0.1);
  
  return {
    isGood: (allOffline || commComplete) && qualityScore >= 0.7,
    score: qualityScore,
    details: `score=${qualityScore.toFixed(2)} active=${activeN}/${n} switch=${switchRate.toFixed(2)} temp=${tempRate.toFixed(2)} comm=${commRate.toFixed(2)} mode=${modeRate.toFixed(2)}${allOffline ? ' allOffline' : ''}`
  };
}

function buildPartialSignature(cards = []) {
  return cards.map(c => [
    c.name || '',
    c.switch || '',
    c.indoor || '',
    c.setTemp || '',
    c.mode || '',
    c.fan || '',
    c.indicator || '',
    c.comm || '',
  ].join('|')).join('||');
}

function isOfflineTemplateStable(cards, qc, prev = {}, elapsedMs = 0) {
  if (!cards || cards.length < 2 || !qc || !qc.uniformTemplate || !qc.allOffline) {
    return { accept: false, signature: '', rounds: 0 };
  }
  const signature = buildPartialSignature(cards);
  const rounds = signature && signature === prev.signature ? (prev.rounds || 0) + 1 : 1;
  const accept = rounds >= 3 && elapsedMs >= 600;
  return { accept, signature, rounds };
}

function persistentDeviceAnomalyState(cards, meta, prev = {}) {
  const classification = classifyPersistentDeviceAnomalyPage(cards, meta);
  const rounds = classification.signature && classification.signature === prev.signature
    ? (prev.rounds || 0) + 1
    : (classification.eligible ? 1 : 0);
  return {
    ...classification,
    accept: classification.eligible && rounds >= 3,
    rounds,
  };
}

function isAcceptableCapture(data, qc = null, quality = null) {
  const cards = data && Array.isArray(data.cards) ? data.cards : [];
  const currentQc = qc || checkCardQuality(cards, data || {});
  const currentQuality = quality || assessDataQuality(cards);
  return currentQc.ok && currentQuality.isGood;
}

async function qualityCheckWithProgressiveRetry(page, extractCards, description, maxAttempts = 5) {
  const RETRY_DELAYS = [200, 500, 1000, 2000, 5000];
  let prevOfflineTemplate = { signature: '', rounds: 0 };
  let prevDeviceAnomaly = { signature: '', rounds: 0 };
  const startTime = Date.now();
  
  for (let attempt = 0; attempt < maxAttempts; attempt++) {
    const data = await extractCards();
    const qc = checkCardQuality(data.cards, data);
    const quality = assessDataQuality(data.cards);
    const offlineTemplate = isOfflineTemplateStable(data.cards, qc, prevOfflineTemplate, Date.now() - startTime);
    prevOfflineTemplate = { signature: offlineTemplate.signature, rounds: offlineTemplate.rounds };
    const deviceAnomaly = persistentDeviceAnomalyState(data.cards, data, prevDeviceAnomaly);
    prevDeviceAnomaly = { signature: deviceAnomaly.signature, rounds: deviceAnomaly.rounds };
    
    if (isAcceptableCapture(data, qc, quality)) {
      loggerLog(LEVELS.DEBUG, 'QUALITY', `${description} OK on attempt ${attempt + 1}: ${qc.details}`);
      data.qualityReason = 'quality_pass';
      return { data, qc, reason: 'quality_pass', attempt: attempt + 1 };
    }

    if (offlineTemplate.accept) {
      loggerLog(LEVELS.WARN, 'QUALITY', `${description} offline template stable after attempt ${attempt + 1}: ${qc.details}`);
      data.qualityReason = 'offline_template_stable';
      return { data, qc: { ...qc, ok: true, offlineTemplateStable: true }, reason: 'offline_template_stable', attempt: attempt + 1 };
    }

    if (deviceAnomaly.accept) {
      loggerLog(LEVELS.WARN, 'QUALITY', `${description} preserving stable device anomalies after attempt ${attempt + 1}: ${deviceAnomaly.details}`);
      data.qualityReason = 'device_anomalies_preserved';
      return { data, qc: { ...qc, ok: true, deviceAnomaliesPreserved: true }, reason: 'device_anomalies_preserved', attempt: attempt + 1 };
    }
    
    loggerLog(LEVELS.DEBUG, 'QUALITY', `${description} attempt ${attempt + 1} failed: ${qc.details}`, { n: data.cards.length });
    
    if (attempt < maxAttempts - 1) {
      await pause(RETRY_DELAYS[attempt] || 1000);
    }
  }
  
  loggerLog(LEVELS.WARN, 'QUALITY', `${description} max attempts reached, using best available data`);
  return { data: null, qc: null, attempt: maxAttempts };
}

async function adaptivePolling(page, extractCards, deadline, description) {
  const startTime = Date.now();
  const MIN_POLL_INTERVAL = 200;
  const MAX_POLL_INTERVAL = 3000;
  const QUALITY_IMPROVEMENT_THRESHOLD = 0.1;
  
  let lastQualityScore = 0;
  let pollInterval = MIN_POLL_INTERVAL;
  let prevOfflineTemplate = { signature: '', rounds: 0 };
  let prevDeviceAnomaly = { signature: '', rounds: 0 };
  
  while (Date.now() - startTime < deadline) {
    const data = await extractCards();
    const qc = checkCardQuality(data.cards, data);
    const quality = assessDataQuality(data.cards);
    const offlineTemplate = isOfflineTemplateStable(data.cards, qc, prevOfflineTemplate, Date.now() - startTime);
    prevOfflineTemplate = { signature: offlineTemplate.signature, rounds: offlineTemplate.rounds };
    const deviceAnomaly = persistentDeviceAnomalyState(data.cards, data, prevDeviceAnomaly);
    prevDeviceAnomaly = { signature: deviceAnomaly.signature, rounds: deviceAnomaly.rounds };
    
    if (isAcceptableCapture(data, qc, quality)) {
      loggerLog(LEVELS.DEBUG, 'QUALITY', `${description} OK after ${Date.now()-startTime}ms: ${qc.details}`);
      data.qualityReason = 'quality_pass';
      return { data, qc, quality, reason: 'quality_pass' };
    }

    if (offlineTemplate.accept) {
      loggerLog(LEVELS.WARN, 'QUALITY', `${description} offline template stable after ${Date.now()-startTime}ms: ${qc.details}`);
      data.qualityReason = 'offline_template_stable';
      return { data, qc: { ...qc, ok: true, offlineTemplateStable: true }, quality, reason: 'offline_template_stable' };
    }

    if (deviceAnomaly.accept) {
      loggerLog(LEVELS.WARN, 'QUALITY', `${description} preserving stable device anomalies after ${Date.now()-startTime}ms: ${deviceAnomaly.details}`);
      data.qualityReason = 'device_anomalies_preserved';
      return { data, qc: { ...qc, ok: true, deviceAnomaliesPreserved: true }, quality, reason: 'device_anomalies_preserved' };
    }
    
    // Accept only when communication state is complete; real temperature alone
    // is not enough because indicator images can lag behind SVG text.
    if (data && data.cards && data.cards.length > 0) {
      const phCount = data.cards.filter(c => !c.name || c.name === '0-0001-KT').length;
      // Non-template all-offline pages are a genuine comm state. Template-looking
      // offline pages require the stability window above before acceptance.
      if (phCount === 0 && data.cards.every(c => c.comm === '离线') && !qc.uniformTemplate) {
        loggerLog(LEVELS.DEBUG, 'QUALITY', `${description} all offline after ${Date.now()-startTime}ms: ${qc.details}`);
        data.qualityReason = 'all_offline';
        return { data, qc, quality, reason: 'all_offline' };
      }
    }
    
    if (quality.score > lastQualityScore + QUALITY_IMPROVEMENT_THRESHOLD) {
      lastQualityScore = quality.score;
      pollInterval = Math.max(MIN_POLL_INTERVAL, pollInterval * 0.8);
      loggerLog(LEVELS.DEBUG, 'QUALITY', `${description} quality improving, reducing poll interval to ${pollInterval}ms`, { score: quality.score });
    } else if (quality.score < lastQualityScore - QUALITY_IMPROVEMENT_THRESHOLD) {
      pollInterval = Math.min(MAX_POLL_INTERVAL, pollInterval * 1.2);
      loggerLog(LEVELS.DEBUG, 'QUALITY', `${description} quality not improving, increasing poll interval to ${pollInterval}ms`, { score: quality.score });
    }
    
    lastQualityScore = quality.score;
    await pause(pollInterval);
  }
  
  log(`      ${description} timeout after ${Date.now()-startTime}ms`);
  return { data: null, qc: null, quality: null, reason: 'timeout' };
}

// ===== Inject DOM helpers into page context =====
async function injectHelpers(page) {
  await page.evaluate(() => {
    const loggerLog = () => {};
    const LEVELS = { DEBUG: 3 };
    // Disable iview form validation engine — prevents "用户名密码不能为空" popups
    // that are triggered by shadow-DOM click events bubbling to main DOM
    if (!window.__ems_sentinel) {
      window.__ems_sentinel = true;
      // Override: stop iview form's validate method from showing errors
      const origValidate = Object.getOwnPropertyDescriptor || (() => ({}));
      // Nuke all existing form-item-error elements
      document.querySelectorAll('.ivu-form-item-error-tip').forEach(e => e.remove());
      document.querySelectorAll('.ivu-form-item-error').forEach(e => { e.classList.remove('ivu-form-item-error'); });
      // MutationObserver: kill any form-error elements the instant they appear
      new MutationObserver(ms => {
        for (const m of ms) {
          for (const n of m.addedNodes) {
            if (n.nodeType === 1 && n.classList) {
              if (n.classList.contains('ivu-form-item-error-tip') || n.classList.contains('ivu-message-notice')) {
                n.remove();
              }
              if (n.querySelectorAll) {
                n.querySelectorAll('.ivu-form-item-error-tip, .ivu-message-notice').forEach(t => t.remove());
              }
            }
          }
          // Also handle class mutations (ivu-form-item-error added to existing elements)
          if (m.type === 'attributes' && m.target.classList && m.target.classList.contains('ivu-form-item-error')) {
            m.target.classList.remove('ivu-form-item-error');
          }
        }
      }).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
    }
    const NOISE = /^(BM|1F|2F|3F|B1F|B2F|B3F|F|N|S|E|W|NE|NW|SE|SW|公区|电梯|楼梯|屋顶|机房)$/i;

    function getShadow() {
      const c = document.querySelector('.pi-svg-container');
      return c && c.shadowRoot ? c.shadowRoot : null;
    }

    window.__ems = {
      isReady() {
        const c = document.querySelector('.pi-svg-container');
        if (!c || !c.shadowRoot) return false;
        const svg = c.shadowRoot.querySelector('svg');
        return svg && svg.querySelectorAll('text').length > 5;
      },

      findAllSubAreaGroups() {
        const re = /^(\d+(?:\.5)?)F$|^B(\d+)F$|^BM$/;
        const sr = getShadow();
        if (!sr) return [];
        const groups = [];
        const seenIds = new Set();
        for (const g of sr.querySelectorAll('g')) {
          if (seenIds.has(g.id)) continue;
          let txt = '';
          for (const c2 of g.childNodes) {
            if (c2.nodeType === 3) txt += c2.textContent;
          }
          txt = txt.trim();
          if (!txt) {
            const t = g.querySelector(':scope > text, :scope > tspan');
            if (t) txt = (t.textContent || '').trim();
          }
          if (!txt) continue;
          const m = txt.match(re);
          if (m) {
            seenIds.add(g.id);
            let floor = 0;
            if (txt === 'BM') floor = -2;
            else if (m[1]) floor = parseFloat(m[1]);
            else if (m[2]) floor = -parseInt(m[2]);
            const r = g.getBoundingClientRect();
            groups.push({
              id: g.id,
              floor,
              text: txt,
              x: Math.round(r.left),
              y: Math.round(r.top)
            });
          }
        }
        // Filter out action-bar labels (e.g. "1F" button at y≈226)
        // that are far from the main floor-selector row, keeping BM intact
        if (groups.length > 0) {
          const floorYs = groups.filter(g => g.text !== 'BM').map(g => g.y);
          if (floorYs.length > 0) {
            floorYs.sort((a, b) => a - b);
            const medianY = floorYs[Math.floor(floorYs.length / 2)];
            return groups.filter(g => g.text === 'BM' || Math.abs(g.y - medianY) <= 30);
          }
        }
        return groups;
      },

      findPageBtns() {
        const re = /^(首页|上页|末页|下页|[一二三四五六七八九十]页)$/;
        const sr = getShadow();
        if (!sr) return {};
        const out = {};
        for (const g of sr.querySelectorAll('g')) {
          let txt = '';
          for (const c2 of g.childNodes) {
            if (c2.nodeType === 3) txt += c2.textContent;
          }
          txt = txt.trim();
          if (!txt) {
            const t = g.querySelector(':scope > text, :scope > tspan');
            if (t) txt = (t.textContent || '').trim();
          }
          if (re.test(txt)) out[txt] = g.id;
        }
        return out;
      },

      findSpecialPageBtns() {
        const sr = getShadow();
        if (!sr) return {};
        const out = {};
        for (const g of sr.querySelectorAll('g')) {
          let txt = '';
          for (const c2 of g.childNodes) {
            if (c2.nodeType === 3) txt += c2.textContent;
          }
          txt = txt.trim();
          if (!txt) {
            const t = g.querySelector(':scope > text, :scope > tspan');
            if (t) txt = (t.textContent || '').trim();
          }
          if (txt === 'BM' || txt === '1F') {
            const r = g.getBoundingClientRect();
            if (r.left > 1500) out[txt] = g.id;
          }
        }
        return out;
      },

      clickById(id) {
        const sr = getShadow();
        if (!sr || !id) return false;
        const el = sr.getElementById(id) || sr.querySelector('#' + CSS.escape(id));
        if (!el) return false;
        const rect = el.getBoundingClientRect();
        const ev = new MouseEvent('click', {
          bubbles: true, cancelable: true,
          clientX: rect.left + rect.width / 2,
          clientY: rect.top + rect.height / 2
        });
        el.dispatchEvent(ev);
        return true;
      },

      findSubTabs() {
        const sr = getShadow();
        if (!sr) return [];
        const tabs = [];
        // Sub-tabs can be either SVG <g> (裙楼/塔楼) or HTML elements (群控/公区) in the main DOM
        // 1. SVG sub-tabs (1号 2F/3F)
        for (const g of sr.querySelectorAll('g')) {
          let txt = '';
          for (const c2 of g.childNodes) {
            if (c2.nodeType === 3) txt += c2.textContent;
          }
          txt = txt.trim();
          if (!txt) {
            const t = g.querySelector(':scope > text, :scope > tspan');
            if (t) txt = (t.textContent || '').trim();
          }
          if (txt === '裙楼' || txt === '塔楼') {
            const r = g.getBoundingClientRect();
            if (r.width > 0) {
              const active = g.classList.contains('is-active') || g.classList.contains('active') || g.getAttribute('aria-selected') === 'true';
              tabs.push({ txt, x: Math.round(r.left), y: Math.round(r.top), id: g.id, shadowDom: true, isActive: active });
            }
          }
        }
        // 2. HTML sub-tabs (6号 1F 群控/公区) — main DOM tab elements
        const seen = new Set(tabs.map(t => t.txt));
        for (const el of document.querySelectorAll('[class*="tab"], [class*="Tab"]')) {
          const txt = (el.textContent || '').trim();
          if (!txt || seen.has(txt)) continue;
          if (txt === '群控' || txt === '公区' || txt === '裙楼' || txt === '塔楼') {
            const r = el.getBoundingClientRect();
            if (r.width > 0 && r.height > 0) {
              const active = el.classList.contains('is-active') || el.classList.contains('active') || el.getAttribute('aria-selected') === 'true';
              tabs.push({ txt, x: Math.round(r.left), y: Math.round(r.top), id: null, mainDom: true, isActive: active });
              seen.add(txt);
            }
          }
        }
        return tabs;
      },

      clickMainDomTab(tab) {
        for (const el of document.querySelectorAll('[class*="tab"]')) {
          const txt = (el.textContent || '').trim();
          if (txt === tab.txt && Math.abs(el.getBoundingClientRect().left - tab.x) <= 3) {
            el.click();
            return true;
          }
        }
        return false;
      },

      clickShadowTab(id) {
        const sr = getShadow();
        if (!sr || !id) return false;
        const el = sr.getElementById(id) || sr.querySelector('#' + CSS.escape(id));
        if (!el) return false;
        const ev = new MouseEvent('click', { bubbles: true, cancelable: true });
        el.dispatchEvent(ev);
        return true;
      },

      extractCards() {
        const sr = getShadow();
        if (!sr) return { count: 0, cards: [], layout: 'unknown' };
        const svg = sr.querySelector('svg');
        if (!svg) return { count: 0, cards: [], layout: 'unknown' };

        const texts = Array.from(svg.querySelectorAll('text'));
        const items = texts.map(t => {
          const r = t.getBoundingClientRect();
          return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2), txt: t.textContent.trim() };
        }).filter(i => i.txt);

        const imgs = Array.from(svg.querySelectorAll('image'));
        const imgList = imgs.map(i => {
          const r = i.getBoundingClientRect();
          return {
            x: Math.round(r.left + r.width / 2),
            y: Math.round(r.top + r.height / 2),
            w: Math.round(r.width),
            h: Math.round(r.height),
            href: (i.getAttribute('href') || i.getAttribute('xlink:href') || '').split('/').pop()
          };
        });

        const switchImgs = imgList.filter(i => i.w >= 38 && i.w <= 50 && i.h >= 17 && i.h <= 30);
        const switchByHref = {};
        for (const si of switchImgs) {
          if (!si.href) continue;
          if (!switchByHref[si.href]) switchByHref[si.href] = 0;
          switchByHref[si.href]++;
        }
        const hrefs = Object.keys(switchByHref);
        let offHref = null, onHref = null;
        if (hrefs.length > 1) {
          hrefs.sort((a, b) => switchByHref[b] - switchByHref[a]);
          offHref = hrefs[0];
          onHref = hrefs[1];
        }

        // Indicator images (29x27) for comm status
        const indicatorImgs = imgList.filter(i => i.w >= 25 && i.w <= 33 && i.h >= 23 && i.h <= 31);

        const NOISE = /^(BM|1F|2F|3F|B1F|B2F|B3F|F|N|S|E|W|NE|NW|SE|SW|公区|电梯|楼梯|屋顶|机房)$/i;

        // Detect layout: grid has rows with 2+ alphanumeric items (device-name-like)
        // Lowered threshold from 8 to 2 to catch partial rows (e.g. 6号 A座 1F row 2 has only 4 cards)
        const byY = {};
        for (const it of items) {
          if (NOISE.test(it.txt)) continue;
          const k = it.y;
          if (!byY[k]) byY[k] = [];
          byY[k].push(it);
        }
        let isGrid = false;
        for (const y in byY) {
          const arr = byY[y];
          if (arr.length >= 2 &&
            arr.every(it => /^[A-Z0-9\-]+$/i.test(it.txt) && it.txt.length >= 5 && it.txt.length < 20) &&
            !arr.every(it => /^[A-Z]?\d+F$/i.test(it.txt))) {
            isGrid = true;
            break;
          }
        }

        function nearest(arr, x, y, xMax, yMax) {
          let best = null, bestDist = 9999;
          for (const it of arr) {
            const dx = Math.abs(it.x - x);
            const dy = Math.abs(it.y - y);
            if (dx > xMax || dy > yMax) continue;
            const d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = it; }
          }
          return best;
        }

        const cards = [];
        let rawCardCount = 0;
        const duplicateNames = new Map();

        if (isGrid) {
          const nameRows = [];
          const seenGridNames = new Set();
          for (const yStr in byY) {
            const arr = byY[yStr];
            if (arr.length >= 2 && arr.every(it => /^[A-Z0-9\-]+$/i.test(it.txt) && it.txt.length >= 5 && it.txt.length < 20)) {
              if (arr.every(it => /^[A-Z]?\d+F$/i.test(it.txt))) continue;
              nameRows.push({ y: parseInt(yStr), items: arr });
            }
          }
          nameRows.sort((a, b) => a.y - b.y);

          const modeTexts = items.filter(i => /^(制冷|通风|制热|送暖|地暖|制热\+地暖)$/.test(i.txt));
          const fanTexts = items.filter(i => /^(自动|高|中|低|0|1|2|3)$/.test(i.txt));
          const tempTexts = items.filter(i => /\d+(\.\d+)?\s*℃/.test(i.txt));

          for (let rowIndex = 0; rowIndex < nameRows.length; rowIndex++) {
            const row = nameRows[rowIndex];
            const nextRowY = nameRows[rowIndex + 1] ? nameRows[rowIndex + 1].y : Infinity;
            const rowBottom = Math.min(row.y + 285, nextRowY - 20);
            for (const nameIt of row.items) {
              rawCardCount++;
              if (seenGridNames.has(nameIt.txt)) {
                duplicateNames.set(nameIt.txt, (duplicateNames.get(nameIt.txt) || 1) + 1);
                continue;
              }
              seenGridNames.add(nameIt.txt);
              const x = nameIt.x, ry = row.y;
              const sw = nearest(switchImgs, x, ry + 100, 80, 50);
              let swState = '-';
              if (sw) {
                if (sw.href === onHref) swState = 'ON';
                else if (sw.href === offHref) swState = 'OFF';
              }
              const mode = nearest(modeTexts, x, ry + 140, 100, 60);
              // ℃ texts near this card, sorted top to bottom; upper=indoor, lower=setTemp
              const nearTemps = tempTexts.filter(i =>
                Math.abs(i.x - x) <= 100 && i.y > ry + 100 && i.y < rowBottom
              ).sort((a, b) => a.y - b.y);
              const indoor = nearTemps[0] ? nearTemps[0].txt.replace(/\s*℃/, '') : '-';
              const setT = nearTemps[1] ? nearTemps[1].txt.replace(/\s*℃/, '') : '-';
              const fan = nearest(fanTexts, x, ry + 235, 100, 60);
              const indic = nearest(indicatorImgs, x, ry - 30, 80, 50);
              cards.push({
                name: nameIt.txt,
                switch: swState,
                mode: mode ? mode.txt : '-',
                indoor,
                setTemp: setT,
                fan: fan ? fan.txt : '-',
                indicator: indic ? indic.href : ''
              });
            }
          }
        } else {
          const deviceNames = items.filter(i => {
            if (NOISE.test(i.txt)) return false;
            if (i.txt.length < 5) return false;
            if (!/-/.test(i.txt)) return false;
            if (!/[A-Z]/.test(i.txt)) return false;
            if (!/\d/.test(i.txt)) return false;
            if (/^[A-Z]?\d+F$/.test(i.txt)) return false;
            return true;
          });
          const altDeviceNames = items.filter(i => /^\d-[\w]/.test(i.txt) && /[A-Z]/.test(i.txt));
          const seenCandidatePositions = new Set();
          const deviceCandidates = [];
          for (const d of [...deviceNames, ...altDeviceNames]) {
            const posKey = `${d.txt}|${d.x}|${d.y}`;
            if (seenCandidatePositions.has(posKey)) continue;
            seenCandidatePositions.add(posKey);
            deviceCandidates.push(d);
          }
          rawCardCount = deviceCandidates.length;
          const seen = new Set();
          const allDeviceNames = [];
          for (const d of deviceCandidates) {
            if (seen.has(d.txt)) {
              duplicateNames.set(d.txt, (duplicateNames.get(d.txt) || 1) + 1);
              continue;
            }
            seen.add(d.txt);
            allDeviceNames.push(d);
          }

          // Expanded mode regex to include 地暖 and 制热+地暖
          const modeTextsG = items.filter(i => /^(制冷|通风|制热|送暖|地暖|制热\+地暖)$/.test(i.txt));
          const fanTextsG = items.filter(i => /^(自动|高|中|低|0|1|2|3)$/.test(i.txt));
          const tempTextsG = items.filter(i => /\d+(\.\d+)?\s*℃/.test(i.txt));

          for (const dn of allDeviceNames) {
            const sw = nearest(switchImgs, dn.x, dn.y + 60, 80, 50);
            let swState = '-';
            if (sw) {
              if (sw.href === onHref) swState = 'ON';
              else if (sw.href === offHref) swState = 'OFF';
            }
            // Find mode below device name (y+60 to y+200 range)
            const mode = nearest(modeTextsG, dn.x, dn.y + 110, 80, 60);
            // For temp: take the 2 nearest ℃ texts below device name
            const nearbyTemps = tempTextsG.filter(i =>
              Math.abs(i.x - dn.x) <= 100 && i.y > dn.y + 80 && i.y < dn.y + 300
            ).sort((a, b) => a.y - b.y);
            const indoor = nearbyTemps[0] ? nearbyTemps[0].txt.replace(/\s*℃/, '') : '-';
            const setT = nearbyTemps[1] ? nearbyTemps[1].txt.replace(/\s*℃/, '') : '-';
            const fan = nearest(fanTextsG, dn.x, dn.y + 200, 80, 80);
            const indic = nearest(indicatorImgs, dn.x, dn.y - 30, 80, 50);
            cards.push({ name: dn.txt, switch: swState, mode: mode ? mode.txt : '-', indoor, setTemp: setT, fan: fan ? fan.txt : '-', indicator: indic ? indic.href : '' });
          }
        }

        // ===== Vue state enrichment: override SVG field values with WS data =====
        try {
          const w = document.querySelector('.pi-graphics-configuration-svg-new');
          if (w && w.__vue__) {
            const d = w.__vue__.$data;
            const rc = d.runConfDataProp || [];
            const ws = d.websocketDataProp || {};
            const sl = d.svgListDraw || [];
            const sr2 = (document.querySelector('.pi-svg-container') || {}).shadowRoot;

            // Build ptId → deviceName map from CabinetId draws
            const ptIdToName = {};
            for (const e of sl) {
              if (!e.dyn || !e.dyn.listDyn) continue;
              for (const ld of e.dyn.listDyn) {
                if (ld.DynType !== 23 || ld.PropertyName !== 'CabinetId') continue;
                let pid; try { pid = JSON.parse(ld.PtPath).ptId; } catch { continue; }
                if (!pid) continue;
                const el = sr2 ? (sr2.getElementById(e.id) || sr2.querySelector('#' + CSS.escape(e.id))) : null;
                const nm = el ? (el.textContent || '').trim() : '';
                if (nm) ptIdToName[pid] = nm;
              }
            }

            // Build devId → fields from rc + ws, with value validation
            const FIELD_DEF = {
              '当前开关机模式': { field: 'switch', enum: { '0': 'OFF', '1': 'ON' }, valid: v => v === 'OFF' || v === 'ON' },
              '开关机': { field: 'switch', skip: true },
              '系统模式设置': { field: 'mode', enum: { '0': '通风', '1': '制冷', '2': '制热', '3': '地暖', '4': '制热+地暖' }, valid: v => /^(通风|制冷|制热|地暖|制热\+地暖)$/.test(v) },
              '室内温度': { field: 'indoor', num: true, valid: v => /^\d+(\.\d+)?$/.test(v) },
              '当前设置温度': { field: 'setTemp', num: true, valid: v => /^\d+(\.\d+)?$/.test(v) },
              '设定风速': { field: 'fan', enum: { '1': '低', '2': '中', '3': '高', '4': '自动' }, valid: v => /^(低|中|高|自动)$/.test(v) },
            };
            const devFields = {};
            for (let i = 0; i < rc.length; i++) {
              const c = rc[i].ptPathConf;
              if (!c.devId || !c.name) continue;
              const def = FIELD_DEF[c.name];
              if (!def || def.skip) continue;
              const raw = ws[String(i)] ? ws[String(i)].tag.value : null;
              if (raw === null) continue;
              let val = raw;
              if (def.enum) val = def.enum[raw] || raw;
              if (def.valid && !def.valid(val)) continue;
              if (!devFields[c.devId]) devFields[c.devId] = {};
              devFields[c.devId][def.field] = val;
            }

            // Build name → devId reverse map (ptId IS devId for CabinetId)
            const nameToDev = {};
            for (const [ptIdStr, name] of Object.entries(ptIdToName)) {
              const pid = parseInt(ptIdStr);
              if (devFields[pid]) nameToDev[name] = pid;
            }

            // Enrich SVG cards with Vue values (prefer SVG fallback over Vue)
            for (const card of cards) {
              const devId = nameToDev[card.name];
              if (!devId || !devFields[devId]) continue;
              const f = devFields[devId];
              if (f.switch) card.switch = f.switch;
              // Prefer SVG mode (visual display); only use Vue as fallback when SVG is missing
              if (f.mode && card.mode === '-') { card.mode = f.mode; loggerLog(LEVELS.DEBUG, 'VUE', `mode -→${f.mode}`, { card: card.name }); }
              // Only use Vue for indoor/setTemp/fan if SVG value is missing or unreasonable
              if (f.indoor) {
                const svgVal = parseFloat(card.indoor);
                const vueVal = parseFloat(f.indoor);
                if (card.indoor === '-' || (isNaN(svgVal) && !isNaN(vueVal)) ||
                    (!isNaN(vueVal) && vueVal >= 0 && vueVal <= 60 && (isNaN(svgVal) || svgVal <= 0 || svgVal > 60))) {
                  if (card.indoor !== f.indoor) loggerLog(LEVELS.DEBUG, 'VUE', `indoor ${card.indoor}→${f.indoor}`, { card: card.name });
                  card.indoor = f.indoor;
                }
              }
              if (f.setTemp) {
                const svgVal = parseFloat(card.setTemp);
                const vueVal = parseFloat(f.setTemp);
                if (card.setTemp === '-' || (isNaN(svgVal) && !isNaN(vueVal)) ||
                    (!isNaN(vueVal) && vueVal >= 5 && vueVal <= 40 && (isNaN(svgVal) || svgVal < 5 || svgVal > 40))) {
                  if (card.setTemp !== f.setTemp) loggerLog(LEVELS.DEBUG, 'VUE', `setTemp ${card.setTemp}→${f.setTemp}`, { card: card.name });
                  card.setTemp = f.setTemp;
                }
              }
              if (f.fan) {
                if (card.fan === '-' || /^\d$/.test(f.fan)) {
                  if (card.fan !== f.fan) loggerLog(LEVELS.DEBUG, 'VUE', `fan ${card.fan}→${f.fan}`, { card: card.name });
                  card.fan = f.fan;
                }
              }
            }
          }
        } catch (_) { /* fallback: keep SVG values */ }

        // Determine switch+comm from indicator (29×27 image, 3 colors):
        // 3bdc38eda0ae77f26807b2b6cdde4456.png = GREEN  = 关机 (OFF)
        // 56f45bb314d74cc8da6c6c8e5942d08d.png = RED    = 开机 (ON)
        // 833bea6e66e7ab0e55704d655e135c7c.png = GRAY   = 离线
        const IND_MAP = {
          '3bdc38eda0ae77f26807b2b6cdde4456.png': '关机',
          '56f45bb314d74cc8da6c6c8e5942d08d.png': '开机',
          '833bea6e66e7ab0e55704d655e135c7c.png': '离线',
        };
        for (const c of cards) {
          const mapped = IND_MAP[c.indicator];
          if (!mapped) continue;
          c.comm = mapped;
          if (mapped === '开机')       c.switch = 'ON';
          else if (mapped === '关机')  c.switch = 'OFF';
          else                         c.switch = '-';
        }

        // Name-floor detection: annotate card with _nameFloor if differs from SVG sub-area
        // Helps identify EMS layout quirks (e.g. 5号 B座 1F containing 2F-named cards)
        for (const c of cards) {
          const m = c.name.match(/^(\d{1,2})F-/);
          if (m) {
            const nf = parseInt(m[1]);
            if (nf >= 1 && nf <= 99) c._nameFloor = nf;
          }
        }

        return {
          count: cards.length,
          rawCount: rawCardCount || cards.length,
          uniqueCount: cards.length,
          duplicateNames: [...duplicateNames.entries()].map(([name, copies]) => ({ name, copies })),
          onHref,
          offHref,
          layout: isGrid ? 'grid' : 'group',
          cards,
        };
      },

      checkSvgIndicators() {
        const sr = getShadow();
        if (!sr) return [];
        const svg = sr.querySelector('svg');
        if (!svg) return [];
        const imgs = Array.from(svg.querySelectorAll('image'));
        const indHrefs = [];
        for (const i of imgs) {
          const r = i.getBoundingClientRect();
          const w = Math.round(r.width), h = Math.round(r.height);
          if (w >= 25 && w <= 33 && h >= 23 && h <= 31) {
            const href = (i.getAttribute('href') || i.getAttribute('xlink:href') || '').split('/').pop();
            if (href) indHrefs.push(href);
          }
        }
        return indHrefs;
      }
    };
  });
}

async function waitForReady(page, maxRetries) {
  maxRetries = maxRetries || 10;
  for (let i = 0; i < maxRetries; i++) {
    try {
      const ok = await page.evaluate(() => window.__ems && window.__ems.isReady());
      if (ok) return true;
      const hasHelper = await page.evaluate(() => !!window.__ems).catch(() => false);
      if (!hasHelper) {
        loggerLog(LEVELS.DEBUG, 'CRASH', `__ems missing in waitForReady — re-injecting`);
        await injectHelpers(page).catch(() => {});
      }
    } catch { /* context destroyed by navigation */ }
    await new Promise(r => setTimeout(r, 200));
  }
  return false;
}

async function waitForLoadedCards(page, opts = {}) {
  const maxRetries = opts.maxRetries || 6;
  const waitMs = opts.waitMs || 200;
  for (let i = 0; i < maxRetries; i++) {
    let result;
    try {
      result = await page.evaluate(() => {
        if (!window.__ems) return { ok: true };
        const d = window.__ems.extractCards();
        if (!d.cards || d.cards.length === 0) return { ok: true };
        const hasReal = d.cards.some(c => c.name && c.name !== '0-0001-KT');
        const switchLoaded = d.cards.some(c => c.switch !== '-');
        const withComm = d.cards.filter(c => c.comm === '开机' || c.comm === '关机' || c.comm === '离线').length;
        const allOffline = d.cards.length >= 2 && d.cards.every(c => c.comm === '离线');
        return { ok: hasReal, count: d.cards.length, real: d.cards.filter(c => c.name && c.name !== '0-0001-KT').length, switchLoaded, withComm, allOffline };
      });
    } catch {
      await new Promise(r => setTimeout(r, waitMs));
      continue;
    }
    if (!result.ok) {
      log(`      WAIT_CARDS ${i+1}/${maxRetries}: ${result.real||0}/${result.count||0} real, waiting ${waitMs}ms...`);
      await new Promise(r => setTimeout(r, waitMs));
      continue;
    }
    if (!result.switchLoaded && !result.allOffline) {
      log(`      WAIT_CARDS ${i+1}/${maxRetries}: switch images not loaded, waiting ${waitMs}ms...`);
      await new Promise(r => setTimeout(r, waitMs));
      continue;
    }
    if (result.withComm < result.count) {
      log(`      WAIT_CARDS ${i+1}/${maxRetries}: comm ${result.withComm||0}/${result.count||0}, waiting ${waitMs}ms...`);
      await new Promise(r => setTimeout(r, waitMs));
      continue;
    }
    return true;
  }
  return false;
}

function pageFromData(pageName, data, extra = {}) {
  const normalizedCards = normalizeKnownSourceDefects(data.cards || []);
  const normalizedData = { ...data, cards: normalizedCards };
  const duplicateNames = Array.isArray(data.duplicateNames) ? data.duplicateNames : [];
  const qc = checkCardQuality(normalizedCards, normalizedData);
  const knownMissingIndicator = classifyKnownMissingIndicatorPage(normalizedCards, normalizedData);
  const qualityReason = knownMissingIndicator.eligible
    ? 'known_source_indicator_missing'
    : (extra.qualityReason || data.qualityReason || (qc.ok ? 'quality_pass' : ''));
  return {
    page: pageName,
    count: data.count,
    rawCount: data.rawCount ?? data.count,
    uniqueCount: data.uniqueCount ?? data.count,
    duplicateNames,
    onHref: data.onHref,
    offHref: data.offHref,
    layout: data.layout,
    qualityReason,
    cards: normalizedCards,
    ...extra,
  };
}

function auditCollectedOutput(output) {
  const issues = [];
  for (const bldg of output.buildings || []) {
    for (const sa of bldg.subAreas || []) {
      for (const pageRow of sa.pages || []) {
        const cards = Array.isArray(pageRow.cards) ? pageRow.cards : [];
        const qc = checkCardQuality(cards, pageRow);
        const reason = pageRow.qualityReason || pageRow.quality_reason || '';
        const allowedNonPass = reason === 'device_anomalies_preserved'
          ? classifyPersistentDeviceAnomalyPage(cards, pageRow).eligible
          : reason === 'known_source_indicator_missing'
            ? classifyKnownMissingIndicatorPage(cards, pageRow).eligible
            : isAcceptedCaptureQualityReason(reason);
        if (!qc.ok && !allowedNonPass) {
          issues.push({
            building: bldg.building,
            floor: sa.floor,
            subArea: sa.text,
            page: pageRow.page,
            reason: reason || 'missing_quality_reason',
            details: qc.details,
          });
        }
      }
    }
  }
  return issues;
}

// Wait until WebSocket data is sufficiently loaded (>85% of points received)
async function waitForDataReady(page, opts = {}) {
  const maxRetries = opts.maxRetries || 10;
  const waitMs = opts.waitMs || 200;
  let prev = { count: -1, stable: 0 };
  for (let i = 0; i < maxRetries; i++) {
    const ok = await page.evaluate(() => {
      const w = document.querySelector('.pi-graphics-configuration-svg-new');
      if (!w || !w.__vue__) return { count: -1 };
      return { count: Object.keys(w.__vue__.$data.websocketDataProp || {}).length };
    }).catch(() => ({ count: -1 }));
    if (ok.count < 0) continue;
    if (ok.count === prev.count && ok.count > 0) {
      log(`      WS_READY (${ok.count} pts after ${(i+1)*waitMs}ms)`);
      return true;
    } else if (ok.count > 0) {
      prev = { count: ok.count, stable: 1 };
    }
    await new Promise(r => setTimeout(r, waitMs));
  }
  log(`      WS_TIMEOUT after ${maxRetries*waitMs}ms — proceeding best-effort (last count: ${prev.count})`);
  return false;
}

// Wait for page switch to complete — detect NEW data arrival (WS count change)
// Much faster: no "stable" wait, just detect count changed from before click
async function waitForPageSwitch(page, beforeCount, opts = {}) {
  const maxRetries = opts.maxRetries || 6;
  const waitMs = opts.waitMs || 200;
  let ok = { count: -1 };
  for (let i = 0; i < maxRetries; i++) {
    ok = await page.evaluate(() => {
      const w = document.querySelector('.pi-graphics-configuration-svg-new');
      if (!w || !w.__vue__) return { count: -1 };
      return { count: Object.keys(w.__vue__.$data.websocketDataProp || {}).length };
    }).catch(() => ({ count: -1 }));
    if (ok.count > 0 && ok.count !== beforeCount) {
      log(`      PAGE_SWITCH: WS count ${beforeCount} -> ${ok.count} (${(i+1)*waitMs}ms)`);
      return true;
    }
    await new Promise(r => setTimeout(r, waitMs));
  }
  log(`      PAGE_SWITCH_TIMEOUT after ${maxRetries*waitMs}ms (last count: ${ok.count})`);
  return false;
}

// Wait until indicator SVG images are stable (all hrefs resolved)
async function waitForSvgStable(page, opts = {}) {
  const maxRetries = opts.maxRetries || 25;
  const waitMs = opts.waitMs || 150;
  let prevSnapshot = '';
  let stableCount = 0;
  for (let i = 0; i < maxRetries; i++) {
    const indHrefs = await page.evaluate(() => window.__ems.checkSvgIndicators()).catch(() => []);
    const snapshot = [...new Set(indHrefs)].sort().map(h => h.slice(0,8)).join(',');
    if (snapshot && snapshot === prevSnapshot) {
      stableCount++;
      if (stableCount >= 2) {
        log(`      SVG_STABLE (${new Set(indHrefs).size} groups) after ${(i+1)*waitMs}ms`);
        return true;
      }
    } else {
      stableCount = 0;
    }
    prevSnapshot = snapshot;
    await pause(waitMs);
  }
  log(`      SVG_TIMEOUT (groups: ${prevSnapshot || 'none'}) — proceeding`);
  return false;
}

async function closeModals(page) {
  try {
    await page.evaluate(() => {
      const m = document.querySelector('.ivu-modal-mask');
      if (m && m.offsetParent !== null) { m.click(); return; }
      for (const v of document.querySelectorAll('.ivu-modal-wrap:not(.ivu-modal-hidden)')) {
        // Click "关闭/取消" first (验证框点"确定"会触发"密码不能为空")
        for (const b of v.querySelectorAll('.ivu-btn, button')) {
          if (/^(关闭|取消|Close|Cancel)$/i.test((b.textContent||'').trim())) { b.click(); return; }
        }
        for (const b of v.querySelectorAll('.ivu-btn, button')) {
          if (/^(确定|OK|Confirm)$/i.test((b.textContent||'').trim())) { b.click(); return; }
        }
      }
    });
    await pause(300);
  } catch { /* ok */ }
}

async function healthCheck(page) {
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      await Promise.race([
        page.evaluate(() => true),
        new Promise((_, reject) => setTimeout(() => reject(new Error('slow')), 8000))
      ]);
      return true;
    } catch { await pause(2000); }
  }
  return false; // page unresponsive after 2 attempts
}

async function recoverFromCrash(page, buildingName, partialResult) {
  log('!!! PAGE CRASHED — auto-reloading...');
  if (partialResult) {
    saveOutput(partialResult);
    log(`  Saved ${partialResult.subAreas.length} sub-areas`);
  }
  // Reload page programmatically
  try { await page.evaluate(() => location.reload()); } catch {}
  await pause(5000);
  await injectHelpers(page);
  if (await waitForReady(page)) {
    log('  Recovered successfully, resuming...');
    return true;
  }
  // Auto-reload failed — fallback to manual
  log('  Auto-reload failed. Please refresh Edge manually, then press Enter');
  await new Promise(resolve => { process.stdin.once('data', resolve); });
  await pause(2000);
  await injectHelpers(page);
  return await waitForReady(page);
}

async function clickMenu(page, menuMatch) {
  await closeModals(page);
  const menuRe = new RegExp('^' + menuMatch);
  // Try Playwright locator first
  const items = page.locator('.ivu-menu-item');
  const count = await items.count();
  for (let i = 0; i < count; i++) {
    const txt = (await items.nth(i).textContent()) || '';
    if (menuRe.test(txt.trim()) && /楼|空调|开闭所|服务/.test(txt)) {
      try {
        await items.nth(i).click({ force: true, timeout: 5000 });
        return txt.trim();
      } catch {
        // Fallback: native DOM click via evaluate
        const clicked = await page.evaluate((match) => {
          const r = new RegExp('^' + match);
          const all = document.querySelectorAll('.ivu-menu-item');
          for (const el of all) {
            const t = el.textContent.trim();
            if (r.test(t) && /楼|空调|开闭所|服务/.test(t)) {
              el.dispatchEvent(new MouseEvent('click', { bubbles: true }));
              return t;
            }
          }
          return null;
        }, menuMatch);
        if (clicked) return clicked;
      }
    }
  }
  return null;
}

async function clickMenuReady(page, menuMatch, opts = {}) {
  const maxRetries = opts.maxRetries || 20;
  const attempts = opts.attempts || 2;
  let clicked = null;
  for (let i = 0; i < attempts; i++) {
    await closeModals(page);
    clicked = await clickMenu(page, menuMatch);
    if (!clicked) return { clicked: null, ready: false };
    await injectHelpers(page).catch(() => {});
    if (await waitForReady(page, maxRetries)) return { clicked, ready: true };
    loggerLog(LEVELS.WARN, 'CRASH', `Menu ${menuMatch} not ready after click attempt ${i + 1}/${attempts}`);
    await pause(600);
  }
  return { clicked, ready: false };
}

// ===== Network Monitoring & Diagnostics =====
function setupNetworkMonitor(page) {
  const netLog = { requests: [], responses: [], errors: [], wsEvents: [], summary: { total: 0, failed: 0, wsDisconnects: 0 } };
  const captureReq = (req) => {
    if (netLog.requests.length >= NETWORK_LOG_MAX) netLog.requests.shift();
    netLog.requests.push({
      url: req.url().substring(0, 200),
      type: req.resourceType(),
      method: req.method(),
      ts: Date.now()
    });
    netLog.summary.total++;
  };
  const captureRes = (res) => {
    if (netLog.responses.length >= NETWORK_LOG_MAX) netLog.responses.shift();
    const ok = res.ok();
    netLog.responses.push({
      url: res.url().substring(0, 200),
      status: res.status(),
      ok,
      ts: Date.now()
    });
    if (!ok) {
      netLog.summary.failed++;
      netLog.errors.push({
        url: res.url().substring(0, 200),
        status: res.status(),
        ts: Date.now()
      });
    }
  };
  const captureWS = (ws) => {
    netLog.wsEvents.push({ type: 'open', url: ws.url().substring(0, 200), ts: Date.now() });
    ws.on('close', () => {
      netLog.wsEvents.push({ type: 'close', url: ws.url().substring(0, 200), ts: Date.now() });
      netLog.summary.wsDisconnects++;
    });
    ws.on('framesent', () => {});
    ws.on('framereceived', () => {});
  };
  page.on('request', captureReq);
  page.on('requestfailed', (req) => {
    const fail = req.failure ? req.failure() : null;
    netLog.errors.push({ url: req.url().substring(0, 200), error: fail ? fail.errorText : 'unknown', ts: Date.now() });
    netLog.summary.failed++;
  });
  page.on('response', captureRes);
  page.on('websocket', captureWS);
  return netLog;
}

function getNetworkSummary(netLog) {
  if (!netLog) {
    return {
      totalRequests: 0,
      failedRequests: 0,
      recentErrors: [],
      wsDisconnects: 0,
      wsEventCount: 0
    };
  }
  const errors = netLog.errors.filter(e => !e.url.includes('data:') && !e.url.includes('favicon'));
  const wsCloses = netLog.wsEvents.filter(e => e.type === 'close');
  return {
    totalRequests: netLog.summary.total,
    failedRequests: netLog.summary.failed,
    recentErrors: errors.slice(-10),
    wsDisconnects: wsCloses.length,
    wsEventCount: netLog.wsEvents.length
  };
}

// ===== Live Browser State Verification =====
async function verifyPageState(page) {
  try {
    return await page.evaluate(() => {
      const c = document.querySelector('.pi-svg-container');
      const shadowOk = c && c.shadowRoot;
      const svgCount = shadowOk ? c.shadowRoot.querySelectorAll('svg').length : 0;
      const textCount = shadowOk ? c.shadowRoot.querySelectorAll('text').length : 0;
      const wsContainer = document.querySelector('.pi-graphics-configuration-svg-new');
      const hasVue = wsContainer && wsContainer.__vue__;
      let wsDataCount = 0;
      if (hasVue) {
        try { wsDataCount = Object.keys(wsContainer.__vue__.$data.websocketDataProp || {}).length; } catch {}
      }
      const indicators = shadowOk
        ? Array.from(c.shadowRoot.querySelectorAll('image[href*=".png"]')).filter(img => {
            const w = img.getAttribute('width');
            const h = img.getAttribute('height');
            return w === '29' && h === '27';
          }).length
        : 0;
      return {
        ready: window.__ems && window.__ems.isReady ? window.__ems.isReady() : false,
        shadowDOM: !!shadowOk,
        svgCount,
        textCount,
        wsDataCount,
        indicatorCount: indicators,
        url: location.href,
        title: document.title
      };
    });
  } catch (e) {
    return { error: e.message, ready: false };
  }
}

async function verifyCardIntegrity(page) {
  try {
    return await page.evaluate(() => {
      if (!window.__ems || !window.__ems.extractCards) return { error: 'extractCards not injected' };
      const data = window.__ems.extractCards();
      const issues = [];
      if (!data.cards || data.cards.length === 0) {
        issues.push('no cards extracted');
      } else {
        const placeholders = data.cards.filter(c => c.name && c.name.startsWith('0-'));
        if (placeholders.length > 0) issues.push(`${placeholders.length} placeholder cards`);
        const missingIndicator = data.cards.filter(c => !c.indicator);
        if (missingIndicator.length > 0) issues.push(`${missingIndicator.length} cards missing indicator`);
        const names = data.cards.map(c => c.name);
        const dupes = names.filter((n, i) => n && names.indexOf(n) !== i);
        if (dupes.length > 0) issues.push(`${dupes.length} duplicate card names`);
      }
      return {
        count: data.cards ? data.cards.length : 0,
        onCount: data.onHref ? data.cards.filter(c => c.indicator === data.onHref).length : -1,
        offCount: data.offHref ? data.cards.filter(c => c.indicator === data.offHref).length : -1,
        issues,
        placeholderCards: data.cards ? data.cards.filter(c => c.name && c.name.startsWith('0-')).map(c => c.name) : []
      };
    });
  } catch (e) {
    return { error: e.message };
  }
}

// ===== Enhanced State Snapshots =====
async function captureEnhancedSnapshot(page, label) {
  const start = Date.now();
  const pageState = await verifyPageState(page);
  const cardIntegrity = await verifyCardIntegrity(page);
  let cards = [];
  let onHref = '';
  let offHref = '';
  let layout = '';

  try {
    const data = await page.evaluate(() => window.__ems.extractCards());
    cards = data.cards || [];
    onHref = data.onHref || '';
    offHref = data.offHref || '';
    layout = data.layout || '';
  } catch (e) {
    // fallback
  }

  // Compute indicator frequency
  const indicatorFreq = {};
  for (const c of cards) {
    const src = c.indicator || 'none';
    indicatorFreq[src] = (indicatorFreq[src] || 0) + 1;
  }

  return {
    label,
    ts: new Date().toISOString(),
    elapsed: Date.now() - start,
    pageState,
    cardIntegrity,
    cards,
    onHref,
    offHref,
    layout,
    indicatorFreq,
    cardCount: cards.length,
    // Determine ON count using onHref frequency
    onCount: onHref ? (indicatorFreq[onHref] || 0) : 0,
    offCount: offHref ? (indicatorFreq[offHref] || 0) : 0
  };
}

async function diagnosePage(page, netLog) {
  const state = await verifyPageState(page);
  const integrity = await verifyCardIntegrity(page);
  const netSummary = getNetworkSummary(netLog);
  const issues = [];
  if (!state.ready) issues.push('Page not ready');
  if (!state.shadowDOM) issues.push('Shadow DOM missing');
  if (state.indicatorCount === 0) issues.push('No indicators found');
  if (integrity.issues && integrity.issues.length > 0) issues.push(...integrity.issues);
  if (netSummary.failedRequests > 0) issues.push(`${netSummary.failedRequests} failed network requests`);
  if (netSummary.wsDisconnects > 0) issues.push(`${netSummary.wsDisconnects} WS disconnects`);
  return {
    healthy: issues.length === 0,
    issues,
    state,
    integrity,
    network: netSummary
  };
}

async function waitForVerifyHealthy(page, netLog, timeoutMs = 20000) {
  const start = Date.now();
  let last = null;
  while (Date.now() - start < timeoutMs) {
    await waitForReady(page, 5);
    await Promise.all([
      waitForLoadedCards(page, { maxRetries: 3, waitMs: 250 }),
      waitForDataReady(page, { maxRetries: 5, waitMs: 200 }),
      waitForSvgStable(page, { maxRetries: 6, waitMs: 200 }),
    ]);
    const diag = await diagnosePage(page, netLog);
    last = diag;
    const hasExtractedRealCards =
      diag.state &&
      diag.state.shadowDOM &&
      diag.integrity &&
      diag.integrity.count > 0 &&
      !(diag.integrity.issues || []).some(issue => /placeholder|no cards extracted/i.test(issue));
    if ((diag.healthy && diag.integrity && diag.integrity.count > 0) || hasExtractedRealCards) {
      return diag;
    }
    loggerLog(LEVELS.DEBUG, 'QUALITY', `VERIFY_WAIT ${Math.round(Date.now() - start)}ms: ${diag.issues.join(', ') || 'not healthy'}`);
    await pause(500);
  }
  return last || await diagnosePage(page, netLog);
}

// ===== Login helpers for auto-launch =====
async function isLoggedIn(page) {
  try {
    return await page.evaluate(() => {
      if (document.querySelector('input[type="password"]')) return false;
      const txt = document.body?.innerText || '';
      const hasBusinessText = /[1-6]号/.test(txt) && txt.includes('空调');
      const hasEmsShell = !!(
        document.querySelector('.pi-svg-container') ||
        document.querySelector('.ivu-menu') ||
        document.querySelector('.pi-menu') ||
        document.querySelector('.pi-app')
      );
      return hasBusinessText && hasEmsShell;
    });
  } catch { return false; }
}

async function helpPage() {
  const mode = USE_AUTO_LAUNCH ? '自动启动 Edge' : (USE_CDP ? 'CDP 连接已有 Edge' : '无头浏览器');
  const sep = '\x1b[90m' + '─'.repeat(48) + '\x1b[0m';
  function readAns() {
    return new Promise(resolve => {
      process.stdin.once('data', d => resolve(d.toString().trim().toUpperCase()));
    });
  }
  process.stdout.write('\n');
  console.log(sep);
  log('登录检测超时');
  console.log(sep);
  log('当前模式: ' + mode);
  log('');
  log('无法检测到 EMS 登录状态。可能原因:');
  log('• 浏览器被拦截或弹出窗口被阻止');
  log('• 尚未在浏览器中输入账号密码');
  log('• EMS 服务或网络连接问题');
  log('');
  log('请确认: 已打开 EMS 页面并完成登录');
  log('');
  log(`  [C] 切换模式 — 使用 CDP 连接已有 Edge（需先手动打开 ${CDP_URL}）`);
  log('  [R] 重试 — 再次等待登录');
  log('  [Q] 返回 — 回到主菜单');
  log('');
  while (true) {
    process.stdout.write('  请选择 [C/R/Q]: ');
    const ans = await readAns();
    if (ans === 'C') {
      process.stdout.write('[ACTION]switch_to_cdp\n');
      process.exit(0);
    }
    if (ans === 'R') return;
    if (ans === 'Q') {
      process.stdout.write('[ACTION]return\n');
      process.exit(0);
    }
    process.stdout.write('  无效输入\n');
  }
}

async function waitForLogin(page) {
  log('请在弹出的 Edge 窗口中登录 EMS。');
  log('60 秒倒计时，超时后自动显示帮助页。\n');
  const MAX_WAIT = 60000;
  const start = Date.now();
  let lastRemain = -1;
  let forceCheck = false;
  let forceHelp = false;
  const stdinHandler = (data) => {
    const ch = data.toString().trim().toLowerCase();
    if (ch === 'q') forceHelp = true;
    if (ch === '') forceCheck = true;
  };
  process.stdin.on('data', stdinHandler);
  try {
    while (true) {
      if (await isLoggedIn(page)) {
        log('检测到登录状态，继续采集。');
        return;
      }
      const remain = Math.max(0, Math.round((MAX_WAIT - (Date.now() - start)) / 1000));
      if (remain <= 0) break;
      if (remain !== lastRemain) {
        console.log(`\r  等待登录... 剩余 ${remain}s [Enter=立即检测 / q=帮助]`);
        lastRemain = remain;
      }
      if (forceHelp) { forceHelp = false; process.stdout.write('\n'); await helpPage(); continue; }
      if (forceCheck) { forceCheck = false; continue; }
      await pause(1000);
    }
  } finally {
    process.stdin.removeListener('data', stdinHandler);
    try { process.stdin.pause(); } catch {}
  }
  process.stdout.write('\n');
  await helpPage();
  await waitForLogin(page);
}

async function ensureLoggedInOrExit(page) {
  if (!CHECK_LOGIN || await isLoggedIn(page)) return;
  if (FAIL_IF_NOT_LOGGED_IN) {
    log('未检测到 EMS 登录状态，非交互模式已停止。');
    process.exit(3);
  }
  await waitForLogin(page);
}

// ===== Main enumeration =====
async function main() {
  let browser, context, page;

  if (USE_AUTO_LAUNCH) {
    log(`Mode: Auto-launch → Edge (EMS ${EMS_URL})`);
    const EDGE_PROFILE = path.join(OUT_DIR, '.edge_profile');
    try {
      fs.mkdirSync(EDGE_PROFILE, { recursive: true });
      context = await chromium.launchPersistentContext(EDGE_PROFILE, {
        channel: 'msedge',
        headless: false,
        viewport: { width: 1920, height: 1080 },
        ignoreDefaultArgs: ['--no-sandbox'],
      });
      browser = context.browser();
      const pages = context.pages();
      page = pages.length > 0 ? pages[0] : await context.newPage();
      await page.goto(EMS_URL, { waitUntil: 'domcontentloaded', timeout: 30000 });
      await page.waitForLoadState('networkidle').catch(() => pause(5000));
      await ensureLoggedInOrExit(page);
      log('页面已加载，开始采集。');
    } catch (e) {
      log('Auto-launch failed:', e.message);
      log('请确认 Microsoft Edge 已安装。');
      log(`降级提示: 可使用 --edge 模式（先手动打开 ${CDP_URL}）。`);
      process.exit(1);
    }
  } else if (USE_CDP) {
    log(`Mode: CDP → Edge (${CDP_URL}, EMS ${EMS_URL})`);
    try { browser = await chromium.connectOverCDP(CDP_URL); }
    catch (e) { log('Cannot connect to Edge CDP at', CDP_URL); process.exit(1); }
    context = browser.contexts()[0];
    const pages = context.pages();
    page = pages.find(p => isEmsPageUrl(p.url())) || pages[0];
    log('Page:', page.url());
    if (!isEmsPageUrl(page.url())) {
      await page.goto(EMS_URL, { waitUntil: 'domcontentloaded', timeout: 30000 });
    }
    await page.waitForLoadState('networkidle').catch(() => pause(3000));
    await ensureLoggedInOrExit(page);
  } else {
    log(`Mode: Headless Chromium (${EMS_URL}, --edge to use existing Edge CDP)`);
    browser = await chromium.launch({ headless: !process.argv.includes('--no-headless') });
    context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
    page = await context.newPage();
    log('Navigating to EMS...');
    await page.goto(EMS_URL, { waitUntil: 'networkidle', timeout: 30000 });
    log('If login required, complete login in the non-headless window.');
    log('Then press Enter in this terminal to continue...');
    // Wait for user to confirm login is done
    await new Promise(resolve => { process.stdin.once('data', resolve); });
  }

  // Inject once
  await injectHelpers(page);

  // Setup network monitoring
  let netLog = null;
  let diagInterval = null;
  if (ENABLE_NETWORK_MONITOR) {
    netLog = setupNetworkMonitor(page);
    log('Network monitor active');
  }

  // Self-diagnose mode: periodic health checks
  if (ENABLE_SELF_DIAGNOSE) {
    diagInterval = setInterval(async () => {
      const diag = await diagnosePage(page, netLog);
      if (diag.healthy) {
        loggerLog(LEVELS.DEBUG, 'CRASH', `DIAG Healthy: ${diag.state.indicatorCount} indicators, ${diag.integrity.count} cards, ${diag.network.totalRequests} req`);
      } else {
        loggerLog(LEVELS.WARN, 'CRASH', `DIAG ISSUES: ${diag.issues.join(', ')}`);
      }
    }, DIAGNOSE_INTERVAL);
    loggerLog(LEVELS.DEBUG, 'CRASH', 'Self-diagnose mode active');
  }

  // ===== Verify mode: compare live browser state against DB =====
  if (process.argv.includes('--verify')) {
    log('Verify mode: checking live browser state...');
    const verifyTarget = FILTER && FILTER.length ? FILTER[0] : BUILDINGS[0].building;
    const verifyBuilding = BUILDINGS.find(b => b.building === verifyTarget) || BUILDINGS[0];
    const readyBeforeVerify = await waitForReady(page, 3);
    if (!readyBeforeVerify || (FILTER && FILTER.length)) {
      const menuResult = await clickMenuReady(page, verifyBuilding.menuMatch, { maxRetries: 20, attempts: 2 });
      if (!menuResult.ready) {
        log(`Verify target not ready: ${verifyBuilding.building}`);
      }
    }
    const diag = await waitForVerifyHealthy(page, netLog, 20000);
    log(`Health: ${diag.healthy ? 'OK' : 'ISSUES'}`);
    if (!diag.healthy) diag.issues.forEach(i => log(`  Issue: ${i}`));
    log(`Shadow DOM: ${diag.state.shadowDOM}, Indicators: ${diag.state.indicatorCount}, WS data: ${diag.state.wsDataCount}`);

    // Extract cards and compare against DB if available
    const snapshot = await captureEnhancedSnapshot(page, 'verify');
    log(`Cards: ${snapshot.cardCount}, ON: ${snapshot.onCount}, OFF: ${snapshot.offCount}`);

    // Try loading DB for comparison
    const dbPath = path.join(OUT_DIR, 'ac.db');
    if (require('fs').existsSync(dbPath)) {
      try {
        const Database = require('better-sqlite3');
        const db = new Database(dbPath, { readonly: true });
        const dbOnCount = db.prepare("SELECT COUNT(*) as c FROM cards WHERE comm='开机'").get().c;
        const dbOffCount = db.prepare("SELECT COUNT(*) as c FROM cards WHERE comm='关机'").get().c;
        const dbOfflineCount = db.prepare("SELECT COUNT(*) as c FROM cards WHERE comm='离线'").get().c;
        db.close();
        log(`DB: ${dbOnCount + dbOffCount + dbOfflineCount} cards (开机=${dbOnCount}, 关机=${dbOffCount}, 离线=${dbOfflineCount})`);

        // Compare current view vs DB
        if (snapshot.onCount > 0 && snapshot.onCount !== dbOnCount) {
          loggerLog(LEVELS.WARN, 'QUALITY', `Live 开机=${snapshot.onCount} vs DB 开机=${dbOnCount}`);
        }
      } catch (e) {
        log(`DB compare skipped: ${e.message}`);
      }
    }

    // Output detailed breakdown
    log(`--- Card breakdown ---`);
    const byState = {};
    for (const c of snapshot.cards) {
      const state = c.indicator === snapshot.onHref ? '开机'
        : c.indicator === snapshot.offHref ? '关机' : '离线';
      if (!byState[state]) byState[state] = [];
      byState[state].push(c);
    }
    for (const [state, cards] of Object.entries(byState)) {
      log(`${state} (${cards.length}):`);
      for (const c of cards) {
        log(`  ${c.name}${c.area ? ' [' + c.area + ']' : ''}${c.publicArea ? ' (公区)' : ''}`);
      }
    }

    if (diagInterval) clearInterval(diagInterval);
    await browser.close();
    const hasRealVerifyCards = snapshot.cards.some(c => c.name && c.name !== '0-0001-KT');
    const fatalVerifyIssue = (diag.issues || []).some(issue => /Page not ready|Shadow DOM missing|no cards extracted|placeholder/i.test(issue));
    if (fatalVerifyIssue || snapshot.cardCount <= 0 || !hasRealVerifyCards) {
      log('Verify failed: live EMS page is not healthy enough for collection.');
      process.exit(4);
    }
    return;
  }

  // ===== Recapture mode =====
  if (RECAPTURE_MODE) {
    log(`Recapture mode: ${RECAPTURE_TARGETS.length} target(s)`);
    const recaptureResult = { targets: [], completedAt: null };
    for (const t of RECAPTURE_TARGETS) {
      log(`--- Recapture ${t.building} x=${t.x} y=${t.y} ---`);
      const bldg = BUILDINGS.find(b => b.building === t.building);
      if (!bldg) { log(`  Unknown building ${t.building}`); continue; }
      const clickedMenu = await clickMenu(page, bldg.menuMatch);
      if (!clickedMenu) { log(`  Menu not found`); recaptureResult.targets.push({ ...t, err: 'menu' }); continue; }
      log(`  Menu: ${clickedMenu}`);
      await pause(200);
      if (!(await waitForReady(page))) { log(`  No shadow after menu`); recaptureResult.targets.push({ ...t, err: 'no shadow' }); continue; }
      // Find target sub-area by position
      const subAreas = await page.evaluate(() => window.__ems.findAllSubAreaGroups());
      const target = subAreas.find(s => Math.abs(s.x - t.x) <= 5 && Math.abs(s.y - t.y) <= 5);
      if (!target) {
        log(`  Target x=${t.x} y=${t.y} not found. Available:`);
        for (const s of subAreas) log(`    F${s.floor} ${s.text} x=${s.x} y=${s.y}`);
        recaptureResult.targets.push({ ...t, err: 'not found' });
        continue;
      }
      log(`  Target: F${target.floor} ${target.text} (id=${target.id})`);
      const clicked = await page.evaluate((id) => window.__ems.clickById(id), target.id);
      if (!clicked) { log(`  Click failed`); recaptureResult.targets.push({ ...t, err: 'click' }); continue; }
      await pause(100);
      if (!(await waitForReady(page))) { log(`  No shadow after click`); recaptureResult.targets.push({ ...t, err: 'no shadow after click' }); continue; }
      // Wait for loaded cards (no 0-0001-KT placeholders)
      await waitForLoadedCards(page);
      // Reuse the collectPage flow by directly calling the page logic
      const saRes = { building: t.building, floor: target.floor, text: target.text, x: t.x, y: t.y, pages: [] };
      // Inline simplified collectPage
      const capturePages = async (prefix) => {
        const btns = await page.evaluate(() => window.__ems.findPageBtns());
        const hasNext = !!btns['下页'];
        const hasNumbered = Object.keys(btns).some(k => /^[一二三四五六七八九十]页$/.test(k));
        const pages = [];
        if (hasNext && !hasNumbered) {
          await waitForDataReady(page);
          await waitForSvgStable(page);
          let data0 = await page.evaluate(() => window.__ems.extractCards());
          pages.push(pageFromData(prefix + '一页', data0));
          let pageNum = 2; let prevCards = data0.cards;
          let curBtns = btns;
          while (curBtns['下页']) {
            const beforeCount = await page.evaluate(() => {
              const w = document.querySelector('.pi-graphics-configuration-svg-new');
              if (!w || !w.__vue__) return -1;
              return Object.keys(w.__vue__.$data.websocketDataProp || {}).length;
            }).catch(() => -1);
            await page.evaluate((id) => window.__ems.clickById(id), curBtns['下页']);
            await pause(W.PAGE_CLICK);
            if (!(await waitForReady(page))) break;
            await waitForPageSwitch(page, beforeCount);
            await waitForDataReady(page, { maxRetries: 5, waitMs: 200 });
            await waitForSvgStable(page, { maxRetries: 6, waitMs: 200 });
            await waitForLoadedCards(page, { maxRetries: 3, waitMs: 250 });
            let data = await page.evaluate(() => window.__ems.extractCards());
            const quality = assessDataQuality(data.cards);
            const qc = checkCardQuality(data.cards, data);
            if (!qc.ok || !quality.isGood) {
              log(`      RECAPTURE PAGE QUALITY: ${qc.details} ${quality.details} — progressive retry...`);
              const retryResult = await qualityCheckWithProgressiveRetry(
                page,
                () => page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 })),
                `RECAPTURE ${prefix + pageNum}页`,
                3
              );
              if (retryResult.data) {
                data = retryResult.data;
                log(`      RECAPTURE PROGRESSIVE RETRY OK: ${retryResult.qc.details}`);
              } else {
                log(`      RECAPTURE PROGRESSIVE RETRY FAILED — deeper WS wait fallback...`);
                for (let f = 0; f < 10; f++) {
                  await waitForDataReady(page, { maxRetries: 8, waitMs: 200 });
                  await waitForSvgStable(page, { maxRetries: 8, waitMs: 200 });
                  let dataN = await page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 }));
                  if (!dataN.cards || dataN.cards.length === 0) {
                    await injectHelpers(page);
                    dataN = await page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 }));
                    if (!dataN.cards || dataN.cards.length === 0) continue;
                  }
                  const qcN = checkCardQuality(dataN.cards, dataN);
                  const qualityN = assessDataQuality(dataN.cards);
                  if (isAcceptableCapture(dataN, qcN, qualityN)) {
                    data = dataN;
                    data.qualityReason = 'quality_pass';
                    log(`      RECAPTURE FALLBACK OK after round ${f+1}: ${qcN.details} ${qualityN.details}`);
                    break;
                  }
                  if (dataN.cards.length >= data.cards.length) data = dataN;
                }
              }
            }
            const cn = ['', '一','二','三','四','五','六','七','八','九','十'][pageNum] || pageNum.toString();
            const stale = prevCards.length > 0 && data.cards.length > 0 && data.cards.map(c => c.name).join(',') === prevCards.map(c => c.name).join(',');
            pages.push(pageFromData(prefix + cn + '页', data, { stale: stale || undefined }));
            prevCards = data.cards;
            pageNum++;
            curBtns = await page.evaluate(() => window.__ems.findPageBtns());
          }
        } else {
          const numbered = Object.keys(btns).filter(k => /^[一二三四五六七八九十]页$/.test(k));
          const order = { '一页': 1, '二页': 2, '三页': 3, '四页': 4, '五页': 5 };
          const sorted = numbered.sort((a, b) => (order[a]||99) - (order[b]||99));
          if (sorted.length === 0) {
            await waitForLoadedCards(page);
            await waitForDataReady(page);
            await waitForSvgStable(page);

            // Re-scan for page buttons after SVG is stable — fix catch-22
            const reBtns = await page.evaluate(() => window.__ems.findPageBtns());
            const rePages = Object.keys(reBtns).filter(k => /^[一二三四五六七八九十]页$/.test(k));
            if (rePages.length > 0) return capturePages(prefix);

            const data = await page.evaluate(() => window.__ems.extractCards());
            pages.push(pageFromData(prefix + 'default', data));
          } else {
            // Full waits for initial floor SVG load
            await waitForLoadedCards(page);
            await waitForDataReady(page);
            await waitForSvgStable(page);

            // Re-read page buttons AFTER waits — all buttons should now be rendered
            const finalBtns = await page.evaluate(() => window.__ems.findPageBtns());
            const finalPages = Object.keys(finalBtns).filter(k => /^[一二三四五六七八九十]页$/.test(k))
              .sort((a, b) => ({ '一页': 1, '二页': 2, '三页': 3, '四页': 4, '五页': 5 }[a] || 99) - ({ '一页': 1, '二页': 2, '三页': 3, '四页': 4, '五页': 5 }[b] || 99));

            // Collect current view as first page
            const data0 = await page.evaluate(() => window.__ems.extractCards());
            const firstLabel = finalPages[0] || '一页';
            pages.push(pageFromData(prefix + firstLabel, data0));
            let prevCards = data0.cards;

            // Click remaining pages (skip first since we already captured it)
            for (const plabel of finalPages.slice(1)) {
              const curBtns2 = await page.evaluate(() => window.__ems.findPageBtns());
              const bid = curBtns2[plabel];
              if (!bid) continue;
              const beforeCount = await page.evaluate(() => {
                const w = document.querySelector('.pi-graphics-configuration-svg-new');
                if (!w || !w.__vue__) return -1;
                return Object.keys(w.__vue__.$data.websocketDataProp || {}).length;
              }).catch(() => -1);
              await page.evaluate((id) => window.__ems.clickById(id), bid);
              await pause(W.PAGE_CLICK);
              if (!(await waitForReady(page))) continue;
              await waitForPageSwitch(page, beforeCount);
              await waitForDataReady(page, { maxRetries: 5, waitMs: 200 });
              await waitForSvgStable(page, { maxRetries: 6, waitMs: 200 });
              await waitForLoadedCards(page, { maxRetries: 3, waitMs: 250 });
              let data = await page.evaluate(() => window.__ems.extractCards());
              const quality = assessDataQuality(data.cards);
              const qc = checkCardQuality(data.cards, data);
              if (!qc.ok || !quality.isGood) {
                log(`      RECAPTURE PAGE QUALITY: ${qc.details} ${quality.details} — progressive retry...`);
                const retryResult = await qualityCheckWithProgressiveRetry(
                  page,
                  () => page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 })),
                  `RECAPTURE ${prefix + plabel}`,
                   3
                );
                 if (retryResult.data) {
                   data = retryResult.data;
                   log(`      RECAPTURE PROGRESSIVE RETRY OK: ${retryResult.qc.details}`);
                 } else {
                   log(`      RECAPTURE PROGRESSIVE RETRY FAILED — deeper WS wait fallback...`);
                   for (let f = 0; f < 10; f++) {
                     await waitForDataReady(page, { maxRetries: 8, waitMs: 200 });
                     await waitForSvgStable(page, { maxRetries: 8, waitMs: 200 });
                     const dataN = await page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 }));
                     if (!dataN.cards || dataN.cards.length === 0) continue;
                     const qcN = checkCardQuality(dataN.cards, dataN);
                     const qualityN = assessDataQuality(dataN.cards);
                     if (isAcceptableCapture(dataN, qcN, qualityN)) {
                       data = dataN;
                       data.qualityReason = 'quality_pass';
                       log(`      RECAPTURE FALLBACK OK after round ${f+1}: ${qcN.details} ${qualityN.details}`);
                       break;
                     }
                     if (dataN.cards.length >= data.cards.length) data = dataN;
                   }
                 }
               }
               const stale = prevCards.length > 0 && data.cards.length > 0 && data.cards.map(c => c.name).join(',') === prevCards.map(c => c.name).join(',');
               pages.push(pageFromData(prefix + plabel, data, { stale: stale || undefined }));
              prevCards = data.cards;
            }
          }
        }
        return pages;
      };

      // Collect default view
      saRes.pages.push(...(await capturePages('')));

      // Check for sub-tabs (裙楼/塔楼) — collect each tab's pages
      // Skip the active tab (default view) to avoid duplicates
      const subTabs = await page.evaluate(() => window.__ems.findSubTabs());
      if (subTabs.length > 0) {
        // findSubTabs now returns isActive flag
        subTabs.sort((a, b) => b.txt.localeCompare(a.txt)); // 裙楼 first
        const defaultCardNames = saRes.pages.flatMap(p => p.cards || []).map(c => c.name).sort().join(',');
        for (const tab of subTabs) {
          if (tab.isActive) {
            log(`    Sub-tab ${tab.txt} — already active, skipping`);
            continue;
          }
          if (tab.mainDom)
            await page.evaluate((t) => window.__ems.clickMainDomTab(t), tab);
          else
            await page.evaluate((t) => window.__ems.clickShadowTab(t.id), tab);
          await pause(W.PAGE_CLICK);
          if (!(await waitForReady(page))) continue;
          const tabPages = await capturePages(tab.txt + '/');
          // Skip if cards match default view
          const tabCardNames = tabPages.flatMap(p => p.cards || []).map(c => c.name).sort().join(',');
          if (tabCardNames === defaultCardNames) {
            log(`    Sub-tab ${tab.txt} — same as default, skipping`);
            continue;
          }
          log(`    Sub-tab ${tab.txt}: ${tabPages.reduce((s, p) => s + (p.cards ? p.cards.length : 0), 0)} cards`);
          saRes.pages.push(...tabPages);
        }
      }
      // Special: 6号 A座 1F BM page
      if (t.building === '6号' && t.x <= 650 && target.floor === 1) {
        if (await waitForReady(page)) {
          await closeModals(page);
          const sBtns = await page.evaluate(() => window.__ems.findSpecialPageBtns());
          log(`  6号A座1F specialBtns: ${Object.keys(sBtns).join(', ') || '(none)'}`);
          if (sBtns['BM']) {
            const ok = await page.evaluate((id) => window.__ems.clickById(id), sBtns['BM']);
            if (ok) {
              await pause(W.BM_CLICK);
              if (await waitForReady(page)) {
                 await waitForLoadedCards(page);
                 await waitForDataReady(page);
                 await waitForSvgStable(page);
                 let dataBM = await page.evaluate(() => window.__ems.extractCards());
                  const qualityBM = assessDataQuality(dataBM.cards);
                  const qcBM = checkCardQuality(dataBM.cards, dataBM);
                  if (!qcBM.ok || !qualityBM.isGood) {
                    log(`      BM PAGE QUALITY: ${qcBM.details} ${qualityBM.details} — progressive retry...`);
                    const retryResult = await qualityCheckWithProgressiveRetry(
                      page,
                      () => page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 })),
                      'BM PAGE',
                      3
                    );
                    if (retryResult.data) {
                      dataBM = retryResult.data;
                      log(`      BM PAGE PROGRESSIVE RETRY OK: ${retryResult.qc.details}`);
                    } else {
                      log(`      BM PAGE PROGRESSIVE RETRY FAILED — deeper WS wait fallback...`);
                      for (let f = 0; f < 10; f++) {
                        await waitForDataReady(page, { maxRetries: 8, waitMs: 200 });
                        await waitForSvgStable(page, { maxRetries: 8, waitMs: 200 });
                        let dataN = await page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 }));
                        if (!dataN.cards || dataN.cards.length === 0) {
                          await injectHelpers(page);
                          dataN = await page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 }));
                          if (!dataN.cards || dataN.cards.length === 0) continue;
                        }
                        const qcN = checkCardQuality(dataN.cards, dataN);
                        const qualityN = assessDataQuality(dataN.cards);
                        if (isAcceptableCapture(dataN, qcN, qualityN)) {
                          dataBM = dataN;
                          dataBM.qualityReason = 'quality_pass';
                          log(`      BM PAGE FALLBACK OK after round ${f+1}: ${qcN.details} ${qualityN.details}`);
                          break;
                        }
                        if (dataN.cards.length >= dataBM.cards.length) dataBM = dataN;
                      }
                    }
                  }
                  saRes.pages.push(pageFromData('BM', dataBM));
                log(`  BM page: ${dataBM.count} cards`);
              }
            }
          }
        }
      }
      const total = saRes.pages.reduce((s, p) => s + (p.cards ? p.cards.length : 0), 0);
      const placeholder = saRes.pages.reduce((s, p) => s + (p.cards ? p.cards.filter(c => c.name === '0-0001-KT').length : 0), 0);
      log(`  Total: ${total} cards (placeholder ${placeholder})`);
      recaptureResult.targets.push({ ...t, result: saRes });
    }
    recaptureResult.completedAt = new Date().toISOString();
    const recOut = path.join(OUT_DIR, 'recapture_result.json');
    fs.mkdirSync(OUT_DIR, { recursive: true });
    fs.writeFileSync(recOut, JSON.stringify(recaptureResult, null, 2), 'utf-8');
    log(`Saved recapture result: ${recOut}`);
    const targetErrors = recaptureResult.targets.filter(t => t.err);
    const qualityGateIssues = auditCollectedOutput({
      buildings: recaptureResult.targets
        .filter(t => t.result)
        .map(t => ({ building: t.building, subAreas: [t.result] })),
    });
    for (const issue of qualityGateIssues.slice(0, 20)) {
      loggerLog(LEVELS.ERROR, 'QUALITY', `RECAPTURE QUALITY GATE FAIL ${issue.building} F${issue.floor} ${issue.subArea} ${issue.page}: ${issue.details} reason=${issue.reason}`);
    }
    await browser.close();
    if (targetErrors.length || qualityGateIssues.length) {
      if (targetErrors.length) log(`Recapture target failures: ${targetErrors.length}`);
      if (qualityGateIssues.length) log(`Recapture quality failures: ${qualityGateIssues.length}`);
      process.exit(2);
    }
    return;
  }

  const allResults = [];
  const bldgs = FILTER ? BUILDINGS.filter(b => FILTER.includes(b.building)) : BUILDINGS;
  if (FILTER) log(`Building filter: ${FILTER.join(', ')}`);

  let firstBldg = true;
  for (const bldg of bldgs) {
    const bRes = { building: bldg.building, subAreas: [] };
    log(`--- ${bldg.building} ---`);

    if (DRY_RUN) {
      const clicked = await clickMenu(page, bldg.menuMatch);
      if (!clicked) { bRes.err = 'menu not found'; allResults.push(bRes); continue; }
      if (firstBldg && !(await waitForReady(page))) { bRes.err = 'no shadow'; allResults.push(bRes); continue; }
      firstBldg = false;
      const sas = await page.evaluate(() => window.__ems.findAllSubAreaGroups());
      log(`  Menu: ${clicked}, Sub-areas: ${sas.length}`);
      bRes.subAreaCount = sas.length;
      allResults.push(bRes);
      continue;
    }

    const menuResult = await clickMenuReady(page, bldg.menuMatch, { maxRetries: firstBldg ? 20 : 10, attempts: 3 });
    const clicked = menuResult.clicked;
    if (!clicked) { bRes.err = 'menu not found'; allResults.push(bRes); log('  MENU NOT FOUND'); continue; }
    bRes.menuClicked = clicked;
    log(`  Menu: ${clicked}`);

    if (!menuResult.ready) { bRes.err = 'no shadow after menu'; allResults.push(bRes); firstBldg = false; continue; }
    firstBldg = false;

    // Scan all sub-areas from overview, sort by floor ascending (B1F→1F→...→31F)
    const rawSubAreas = await page.evaluate(() => {
      if (!window.__ems || !window.__ems.findAllSubAreaGroups) return [];
      return window.__ems.findAllSubAreaGroups();
    });
    // 5号/6号: sort by 座 (x range) first, then floor. Others: floor first, then x.
    const is56 = bldg.building === '5号' || bldg.building === '6号';
    const subAreas = rawSubAreas.sort((a, b) => {
      if (is56) {
        const za = getZone(a.x, bldg.building);
        const zb = getZone(b.x, bldg.building);
        if (za !== zb) return za - zb;
        return a.floor - b.floor;
      }
      return a.floor - b.floor || a.x - b.x;
    });
    bRes.subAreaCount = subAreas.length;
    log(`  Sub-areas: ${subAreas.length}`);

    const visited = new Set();
    let bldgCardAcc = 0;

    for (let saIdx = 0; saIdx < subAreas.length; saIdx++) {
      const target = subAreas[saIdx];
      const visitKey = target.floor + '|' + target.x + '|' + target.y;
      if (visited.has(visitKey)) continue;
      visited.add(visitKey);

      // BM is handled as a special page within 6号 A座 1F, not as a standalone sub-area
      if (bldg.building === '6号' && target.floor === -2) { bRes.subAreas.push({ idx: saIdx, floor: -2, text: target.text, err: 'bm inline' }); continue; }

      // Page health check — skip first sub-area (page still loading after menu click)
      if (saIdx > 0 && !(await healthCheck(page))) {
        if (await recoverFromCrash(page, bldg.building, bRes)) {
          saIdx--; continue;
        }
        break;
      }

      // Reset to overview for sub-areas after the first (first is already on overview).
      // Re-scan each time because SVG element IDs change after each re-render.
      if (saIdx > 0) {
        await closeModals(page);
        const resetResult = await clickMenuReady(page, bldg.menuMatch, { maxRetries: 20, attempts: 3 });
        if (!(await waitForReady(page, 20))) {
          log(`  [${saIdx}] F${target.floor} SKIP: no shadow after reset`);
          bRes.subAreas.push({ idx: saIdx, floor: target.floor, text: target.text, err: 'no shadow after reset' });
          continue;
        }
        if (!resetResult.ready) {
          log(`  [${saIdx}] F${target.floor} WARN: reset menu not fully ready, continuing with current DOM`);
        }
      }

      // Re-scan sub-areas fresh each time
      let curSubAreas = await page.evaluate(() => {
        if (!window.__ems || !window.__ems.findAllSubAreaGroups) return [];
        return window.__ems.findAllSubAreaGroups();
      });
      let candidates = curSubAreas.filter(g => g.floor === target.floor);
      if (is56 && candidates.length > 1) {
        const targetZone = getZone(target.x, bldg.building);
        const zoneMatches = candidates.filter(g => getZone(g.x, bldg.building) === targetZone);
        if (zoneMatches.length > 0) candidates = zoneMatches;
      }
      let matchedFloor = candidates.length === 1 ? candidates[0]
        : (candidates.length > 1 ? candidates.reduce((a, b) => Math.abs(a.x - target.x) < Math.abs(b.x - target.x) ? a : b) : null);
      if (!matchedFloor) {
        log(`  [${saIdx}] F${target.floor} SKIP: not found`);
        bRes.subAreas.push({ idx: saIdx, floor: target.floor, text: target.text, err: 'not found' });
        continue;
      }
      let clicked = await page.evaluate((id) => window.__ems.clickById(id), matchedFloor.id);
      if (!clicked) { log(`  [${saIdx}] F${target.floor} SKIP: click failed`); bRes.subAreas.push({ idx: saIdx, floor: target.floor, text: target.text, err: 'click failed' }); continue; }

      // Quick pause for Vue to begin SVG re-render before polling
      await pause(200);

      const saRes = { idx: saIdx, floor: target.floor, text: target.text, x: target.x, y: target.y, pages: [] };

      // Collect default view FIRST, then click sub-tabs
      // Default view is often the first tab (e.g. 塔楼), sub-tab is the alternate (裙楼)
      const collectPage = async (prefix) => {
        const btns = await page.evaluate(() => window.__ems.findPageBtns());
        const results = [];

        // Dynamic pagination: "下页" button that changes to "上页" when clicked.
        // Used by 3号/4号. Keep clicking "下页" until it disappears.
        const hasNext = !!btns['下页'];
        const hasNumbered = Object.keys(btns).some(k => /^[一二三四五六七八九十]页$/.test(k));

        if (hasNext && !hasNumbered) {
          // Collect current view first (parallel waits after SVG ready)
          await waitForReady(page);
          await Promise.all([
            waitForLoadedCards(page),
            waitForDataReady(page),
            waitForSvgStable(page),
          ]);
          let data0 = await page.evaluate(() => window.__ems.extractCards());
          const qc0 = checkCardQuality(data0.cards, data0);
          const quality0 = assessDataQuality(data0.cards);
          if (!isAcceptableCapture(data0, qc0, quality0)) {
            log(`      FIRST PAGE QUALITY: ${qc0.details} ${quality0.details} — adaptive polling up to 45s for real data...`);
            const result = await adaptivePolling(
              page,
              () => page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 })),
              45000,
              'FIRST PAGE'
            );
            if (result.data) {
              data0 = result.data;
            } else {
              const DEADLINE = 45000;
              const start = Date.now();
              let bestData = data0;
              let gotQuality = false;
              for (let round = 1; Date.now() - start < DEADLINE; round++) {
                await waitForDataReady(page, { maxRetries: 15, waitMs: 200 });
                await waitForSvgStable(page, { maxRetries: 15, waitMs: 200 });
                const dataN = await page.evaluate(() => window.__ems.extractCards());
                const qcN = checkCardQuality(dataN.cards, dataN);
                const qualityN = assessDataQuality(dataN.cards);
                if (isAcceptableCapture(dataN, qcN, qualityN)) {
                  dataN.qualityReason = 'quality_pass';
                  log(`      FIRST PAGE OK after round ${round} (${Date.now()-start}ms): ${qcN.details} ${qualityN.details}`);
                  data0 = dataN;
                  gotQuality = true;
                  break;
                }
                if (dataN.cards.length >= bestData.cards.length) {
                  bestData = dataN;
                }
                log(`      FIRST PAGE round ${round} still template (${Date.now()-start}ms), continuing...`);
              }
              if (!gotQuality && bestData !== data0) {
                data0 = bestData;
                log(`      FIRST PAGE using best fallback after ${Date.now()-start}ms (${bestData.cards.length} cards)`);
              }
            }
          }
          results.push(pageFromData(prefix + '一页', data0));

          let pageNum = 2;
          let curBtns = btns;
          let prevCards = data0.cards;
          while (curBtns['下页']) {
            const bid = curBtns['下页'];
            // Get WS count BEFORE click to detect change
            const beforeCount = await page.evaluate(() => {
              const w = document.querySelector('.pi-graphics-configuration-svg-new');
              if (!w || !w.__vue__) return -1;
              return Object.keys(w.__vue__.$data.websocketDataProp || {}).length;
            }).catch(() => -1);
            if (!(await page.evaluate((id) => window.__ems.clickById(id), bid))) break;
            await pause(W.PAGE_CLICK);
            if (!(await waitForReady(page))) break;
            // Fast path: wait for WS count to change (no stable wait)
            await waitForPageSwitch(page, beforeCount);
            // Wait for WS data to stabilize on the new page
            await waitForDataReady(page, { maxRetries: 5, waitMs: 200 });
            // Optional: quick SVG check (no full stable wait)
            await waitForSvgStable(page, { maxRetries: 6, waitMs: 200 });
            await waitForLoadedCards(page, { maxRetries: 3, waitMs: 250 });
            let data = await page.evaluate(() => {
              if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
              return window.__ems.extractCards();
            }).catch(() => ({ cards: [], count: 0 }));
            if (!data.cards || data.cards.length === 0) {
              loggerLog(LEVELS.DEBUG, 'CRASH', `__ems lost — re-injecting helpers and retrying`);
              await injectHelpers(page);
              data = await page.evaluate(() => {
                if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                return window.__ems.extractCards();
              }).catch(() => ({ cards: [], count: 0 }));
            }
            // Enhanced quality check with progressive retry
            const quality = assessDataQuality(data.cards);
            const qc = checkCardQuality(data.cards, data);
            
            if (!qc.ok || !quality.isGood) {
              log(`      LOW QUALITY: ${qc.details} ${quality.details} — progressive retry...`);
              
              const retryResult = await qualityCheckWithProgressiveRetry(
                page,
                async () => {
                  let d = await page.evaluate(() => {
                    if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                    return window.__ems.extractCards();
                  }).catch(() => ({ cards: [], count: 0 }));
                  if (!d.cards || d.cards.length === 0) {
                    loggerLog(LEVELS.DEBUG, 'CRASH', `__ems lost in quality retry — re-injecting`);
                    await injectHelpers(page);
                    d = await page.evaluate(() => {
                      if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                      return window.__ems.extractCards();
                    }).catch(() => ({ cards: [], count: 0 }));
                  }
                  return d;
                },
                `PAGE ${pageNum}`,
                3 // Reduced attempts for subsequent pages
              );
              
              if (retryResult.data) {
                data = retryResult.data;
                log(`      PROGRESSIVE RETRY OK: ${retryResult.qc.details}`);
              } else {
                log(`      PROGRESSIVE RETRY FAILED — deeper WS wait fallback...`);
                for (let f = 0; f < 10; f++) {
                  await waitForDataReady(page, { maxRetries: 8, waitMs: 200 });
                  await waitForSvgStable(page, { maxRetries: 8, waitMs: 200 });
                  let dataN = await page.evaluate(() => {
                    if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                    return window.__ems.extractCards();
                  }).catch(() => ({ cards: [], count: 0 }));
                  if (!dataN.cards || dataN.cards.length === 0) {
                    await injectHelpers(page);
                    dataN = await page.evaluate(() => {
                      if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                      return window.__ems.extractCards();
                    }).catch(() => ({ cards: [], count: 0 }));
                    if (!dataN.cards || dataN.cards.length === 0) continue;
                  }
                  const qcN = checkCardQuality(dataN.cards, dataN);
                  const qualityN = assessDataQuality(dataN.cards);
                  if (isAcceptableCapture(dataN, qcN, qualityN)) {
                    data = dataN;
                    data.qualityReason = 'quality_pass';
                    log(`      FALLBACK OK after round ${f+1}: ${qcN.details} ${qualityN.details}`);
                    break;
                  }
                  if (dataN.cards.length >= data.cards.length) data = dataN;
                }
              }
            }
            const cn = ['', '一','二','三','四','五','六','七','八','九','十'][pageNum] || pageNum.toString();
            // Validate: page switch must produce different cards
            const stale = prevCards.length > 0 && data.cards.length > 0 &&
              data.cards.map(c => c.name).join(',') === prevCards.map(c => c.name).join(',');
            const pageEntry = pageFromData(prefix + cn + '页', data);
            if (stale) {
              pageEntry.stale = true;
              loggerLog(LEVELS.WARN, 'ENUM', `${prefix + cn}页 data unchanged — page switch may have failed, retrying...`);
              // Retry: click 下页 again up to 2 times with 1.5s wait
              for (let r = 0; r < 2; r++) {
                if (!(await page.evaluate((id) => window.__ems.clickById(id), bid))) break;
                await pause(1500);
                if (!(await waitForReady(page))) break;
                await waitForDataReady(page);
                await waitForSvgStable(page);
                let dataRetry = await page.evaluate(() => {
                  if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                  return window.__ems.extractCards();
                }).catch(() => ({ cards: [], count: 0 }));
                if (!dataRetry.cards || dataRetry.cards.length === 0) {
                  loggerLog(LEVELS.DEBUG, 'CRASH', `__ems lost in stale retry — re-injecting`);
                  await injectHelpers(page);
                  dataRetry = await page.evaluate(() => {
                    if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                    return window.__ems.extractCards();
                  }).catch(() => ({ cards: [], count: 0 }));
                }
                const stillStale = dataRetry.cards.length > 0 && dataRetry.cards.map(c => c.name).join(',') === prevCards.map(c => c.name).join(',');
                if (!stillStale && dataRetry.cards.length > 0) {
                  Object.assign(pageEntry, pageFromData(prefix + cn + '页', dataRetry));
                  pageEntry.stale = false;
                  const qcR = checkCardQuality(dataRetry.cards, dataRetry);
                  const qualityR = assessDataQuality(dataRetry.cards);
                  if (!isAcceptableCapture(dataRetry, qcR, qualityR)) log(`      RETRY refreshed but low quality: ${qcR.details} ${qualityR.details}`);
                  else log(`      RETRY OK: ${prefix + cn}页 refreshed`);
                  break;
                }
              }
            }
            results.push(pageEntry);
            prevCards = data.cards;
            pageNum++;
            curBtns = await page.evaluate(() => window.__ems.findPageBtns());
          }
          return results;
        }

        // Standard pagination: static "一页/二页/三页" buttons
        const pageOrder = { '一页': 1, '二页': 2, '三页': 3, '四页': 4, '五页': 5, '六页': 6, '七页': 7, '八页': 8, '九页': 9, '十页': 10, '首页': 0, '上页': 0, '下页': 99, '末页': 100 };
        const uniquePages = [...new Set(Object.keys(btns))].sort((a, b) => {
          return (pageOrder[a] || 99) - (pageOrder[b] || 99);
        }).filter(k => !['首页', '上页', '末页', '下页'].includes(k));

        if (uniquePages.length === 0) {
          loggerLog(LEVELS.DEBUG, 'ENUM', `PAGE_BTNS initial none (${Object.keys(btns).join(',') || 'none'})`);
          await waitForReady(page);
          await Promise.all([
            waitForLoadedCards(page),
            waitForDataReady(page),
            waitForSvgStable(page),
          ]);

          // Re-scan after SVG is stable. Buttons may render late; cover both
          // numbered pagination and dynamic "下页" pagination.
          const reBtns = await page.evaluate(() => window.__ems.findPageBtns());
          const rePages = [...new Set(Object.keys(reBtns))].sort((a, b) => {
            return (pageOrder[a] || 99) - (pageOrder[b] || 99);
          }).filter(k => !['首页', '上页', '末页', '下页'].includes(k));
          if (reBtns['下页'] || rePages.length > 0) {
            loggerLog(LEVELS.DEBUG, 'ENUM', `PAGE_BTNS rescan found ${Object.keys(reBtns).join(',')}`);
            return collectPage(prefix);
          }

          let data = await page.evaluate(() => window.__ems.extractCards());
          const qc = checkCardQuality(data.cards, data);
          const quality = assessDataQuality(data.cards);
          if (!isAcceptableCapture(data, qc, quality)) {
            log(`      FIRST PAGE QUALITY: ${qc.details} ${quality.details} — adaptive polling up to 45s for real data...`);
            const result = await adaptivePolling(
              page,
              () => page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 })),
              45000,
              'FIRST PAGE'
            );
            if (result.data) {
              data = result.data;
            } else {
              const DEADLINE = 45000;
              const start = Date.now();
              let bestData = data;
              let gotQuality = false;
              for (let round = 1; Date.now() - start < DEADLINE; round++) {
                await waitForDataReady(page, { maxRetries: 15, waitMs: 200 });
                await waitForSvgStable(page, { maxRetries: 15, waitMs: 200 });
                const dataN = await page.evaluate(() => window.__ems.extractCards());
                const qcN = checkCardQuality(dataN.cards, dataN);
                const qualityN = assessDataQuality(dataN.cards);
                if (isAcceptableCapture(dataN, qcN, qualityN)) {
                  dataN.qualityReason = 'quality_pass';
                  log(`      FIRST PAGE OK after round ${round} (${Date.now()-start}ms): ${qcN.details} ${qualityN.details}`);
                  data = dataN;
                  gotQuality = true;
                  break;
                }
                if (dataN.cards.length >= bestData.cards.length) {
                  bestData = dataN;
                }
                log(`      FIRST PAGE round ${round} still template (${Date.now()-start}ms), continuing...`);
              }
              if (!gotQuality && bestData !== data) {
                data = bestData;
                log(`      FIRST PAGE using best fallback after ${Date.now()-start}ms (${bestData.cards.length} cards)`);
              }
            }
          }
          results.push(pageFromData(prefix + 'default', data));
        } else {
          // Full waits for initial floor SVG load (parallel after SVG ready)
          await waitForReady(page);
          await Promise.all([
            waitForLoadedCards(page),
            waitForDataReady(page),
            waitForSvgStable(page),
          ]);

          // Re-read page buttons AFTER waits — all buttons should now be rendered.
          // The early btns (line 1533) may only show 一页 if SVG was still transitioning.
          const finalBtns = await page.evaluate(() => window.__ems.findPageBtns());
          const finalPages = [...new Set(Object.keys(finalBtns))].sort((a, b) => {
            return (pageOrder[a] || 99) - (pageOrder[b] || 99);
          }).filter(k => !['首页', '上页', '末页', '下页'].includes(k));

          // Collect current view as first page
          let data0 = await page.evaluate(() => window.__ems.extractCards());
          const qc0 = checkCardQuality(data0.cards, data0);
          const quality0 = assessDataQuality(data0.cards);
          if (!isAcceptableCapture(data0, qc0, quality0)) {
            log(`      FIRST PAGE QUALITY: ${qc0.details} ${quality0.details} — adaptive polling up to 45s for real data...`);
            const result = await adaptivePolling(
              page,
              () => page.evaluate(() => window.__ems.extractCards()).catch(() => ({ cards: [], count: 0 })),
              45000,
              'FIRST PAGE'
            );
            if (result.data) {
              data0 = result.data;
            } else {
              const DEADLINE = 45000;
              const start = Date.now();
              let bestData = data0;
              let gotQuality = false;
              for (let round = 1; Date.now() - start < DEADLINE; round++) {
                await waitForDataReady(page, { maxRetries: 15, waitMs: 200 });
                await waitForSvgStable(page, { maxRetries: 15, waitMs: 200 });
                const dataN = await page.evaluate(() => window.__ems.extractCards());
                const qcN = checkCardQuality(dataN.cards, dataN);
                const qualityN = assessDataQuality(dataN.cards);
                if (isAcceptableCapture(dataN, qcN, qualityN)) {
                  dataN.qualityReason = 'quality_pass';
                  log(`      FIRST PAGE OK after round ${round} (${Date.now()-start}ms): ${qcN.details} ${qualityN.details}`);
                  data0 = dataN;
                  gotQuality = true;
                  break;
                }
                if (dataN.cards.length >= bestData.cards.length) {
                  bestData = dataN;
                }
                log(`      FIRST PAGE round ${round} still template (${Date.now()-start}ms), continuing...`);
              }
              if (!gotQuality && bestData !== data0) {
                data0 = bestData;
                log(`      FIRST PAGE using best fallback after ${Date.now()-start}ms (${bestData.cards.length} cards)`);
              }
            }
          }
          const firstLabel = finalPages[0] || '一页';
          results.push(pageFromData(prefix + firstLabel, data0));
          let prevCards = data0.cards;

          // Click remaining pages (skip first since we already captured it)
          for (const plabel of finalPages.slice(1)) {
            const curBtns = await page.evaluate(() => window.__ems.findPageBtns());
            const btnId = curBtns[plabel];
            if (!btnId) { results.push({ page: prefix + plabel, err: 'btn not found' }); continue; }
            // Get WS count BEFORE click to detect change
            const beforeCount = await page.evaluate(() => {
              const w = document.querySelector('.pi-graphics-configuration-svg-new');
              if (!w || !w.__vue__) return -1;
              return Object.keys(w.__vue__.$data.websocketDataProp || {}).length;
            }).catch(() => -1);
            const clickedBtn = await page.evaluate((id) => window.__ems.clickById(id), btnId);
            if (!clickedBtn) { results.push({ page: prefix + plabel, err: 'click failed' }); continue; }
            await pause(W.PAGE_CLICK);
            if (!(await waitForReady(page))) { results.push({ page: prefix + plabel, err: 'no shadow after' }); continue; }
            // Fast path: wait for WS count to change
            await waitForPageSwitch(page, beforeCount);
            // Wait for WS data to stabilize on the new page
            await waitForDataReady(page, { maxRetries: 5, waitMs: 200 });
            // Optional: quick SVG check
            await waitForSvgStable(page, { maxRetries: 6, waitMs: 200 });
            await waitForLoadedCards(page, { maxRetries: 3, waitMs: 250 });
            let data = await page.evaluate(() => {
              if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
              return window.__ems.extractCards();
            }).catch(() => ({ cards: [], count: 0 }));
            if (!data.cards || data.cards.length === 0) {
              loggerLog(LEVELS.DEBUG, 'CRASH', `__ems lost — re-injecting helpers and retrying`);
              await injectHelpers(page);
              data = await page.evaluate(() => {
                if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                return window.__ems.extractCards();
              }).catch(() => ({ cards: [], count: 0 }));
            }
            // Enhanced quality check with progressive retry
            const quality = assessDataQuality(data.cards);
            const qc = checkCardQuality(data.cards, data);
            
            if (!qc.ok || !quality.isGood) {
              log(`      LOW QUALITY: ${qc.details} ${quality.details} — progressive retry...`);
              
              const retryResult = await qualityCheckWithProgressiveRetry(
                page,
                async () => {
                  let d = await page.evaluate(() => {
                    if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                    return window.__ems.extractCards();
                  }).catch(() => ({ cards: [], count: 0 }));
                  if (!d.cards || d.cards.length === 0) {
                    loggerLog(LEVELS.DEBUG, 'CRASH', `__ems lost in quality retry — re-injecting`);
                    await injectHelpers(page);
                    d = await page.evaluate(() => {
                      if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                      return window.__ems.extractCards();
                    }).catch(() => ({ cards: [], count: 0 }));
                  }
                  return d;
                },
                `PAGE ${plabel}`,
                3 // Reduced attempts for subsequent pages
              );
              
              if (retryResult.data) {
                data = retryResult.data;
                log(`      PROGRESSIVE RETRY OK: ${retryResult.qc.details}`);
              } else {
                log(`      PROGRESSIVE RETRY FAILED — deeper WS wait fallback...`);
                for (let f = 0; f < 10; f++) {
                  await waitForDataReady(page, { maxRetries: 8, waitMs: 200 });
                  await waitForSvgStable(page, { maxRetries: 8, waitMs: 200 });
                  let dataN = await page.evaluate(() => {
                    if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                    return window.__ems.extractCards();
                  }).catch(() => ({ cards: [], count: 0 }));
                  if (!dataN.cards || dataN.cards.length === 0) {
                    await injectHelpers(page);
                    dataN = await page.evaluate(() => {
                      if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                      return window.__ems.extractCards();
                    }).catch(() => ({ cards: [], count: 0 }));
                    if (!dataN.cards || dataN.cards.length === 0) continue;
                  }
                  const qcN = checkCardQuality(dataN.cards, dataN);
                  const qualityN = assessDataQuality(dataN.cards);
                  if (isAcceptableCapture(dataN, qcN, qualityN)) {
                    data = dataN;
                    data.qualityReason = 'quality_pass';
                    log(`      FALLBACK OK after round ${f+1}: ${qcN.details} ${qualityN.details}`);
                    break;
                  }
                  if (dataN.cards.length >= data.cards.length) data = dataN;
                }
              }
            }
            // Validate page switch
            const stale = prevCards.length > 0 && data.cards.length > 0 &&
              data.cards.map(c => c.name).join(',') === prevCards.map(c => c.name).join(',');
            const pEntry = pageFromData(prefix + plabel, data);
            if (stale) {
              pEntry.stale = true;
              loggerLog(LEVELS.WARN, 'ENUM', `${prefix + plabel} data unchanged — page switch may have failed, retrying...`);
              // Retry: click the same page button up to 2 times
              for (let r = 0; r < 2; r++) {
                const retryClicked = await page.evaluate((id) => {
                  if (!window.__ems || !window.__ems.clickById) return false;
                  return window.__ems.clickById(id);
                }, btnId).catch(() => false);
                if (!retryClicked) break;
                await pause(1500);
                if (!(await waitForReady(page))) break;
                await waitForDataReady(page);
                await waitForSvgStable(page);
                let dataRetry = await page.evaluate(() => {
                  if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                  return window.__ems.extractCards();
                }).catch(() => ({ cards: [], count: 0 }));
                if (!dataRetry.cards || dataRetry.cards.length === 0) {
                  loggerLog(LEVELS.DEBUG, 'CRASH', `__ems lost in stale retry — re-injecting`);
                  await injectHelpers(page);
                  dataRetry = await page.evaluate(() => {
                    if (!window.__ems || !window.__ems.extractCards) return { cards: [], count: 0 };
                    return window.__ems.extractCards();
                  }).catch(() => ({ cards: [], count: 0 }));
                }
                const stillStale = dataRetry.cards.length > 0 && dataRetry.cards.map(c => c.name).join(',') === prevCards.map(c => c.name).join(',');
                if (!stillStale && dataRetry.cards.length > 0) {
                  Object.assign(pEntry, pageFromData(prefix + plabel, dataRetry));
                  pEntry.stale = false;
                  const qcR = checkCardQuality(dataRetry.cards, dataRetry);
                  const qualityR = assessDataQuality(dataRetry.cards);
                  if (!isAcceptableCapture(dataRetry, qcR, qualityR)) log(`      RETRY refreshed but low quality: ${qcR.details} ${qualityR.details}`);
                  else log(`      RETRY OK: ${prefix + plabel} refreshed`);
                  break;
                }
              }
            }
            results.push(pEntry);
            prevCards = data.cards;
          }
        }
        return results;
      };

      // Collect default view (active tab)
      saRes.pages.push(...(await collectPage('')));

      // Default view = active tab (usually 塔楼), already collected above
      const subTabs = await page.evaluate(() => window.__ems.findSubTabs());
      if (subTabs.length > 0) {
        // Skip the currently active tab to avoid re-capturing
        // findSubTabs now returns isActive flag
        // 裙楼 first, then 塔楼
        subTabs.sort((a, b) => b.txt.localeCompare(a.txt));
        const defaultCardNames = saRes.pages.flatMap(p => p.cards || []).map(c => c.name).sort().join(',');
        for (const tab of subTabs) {
          if (tab.isActive) {
            log(`    [${saIdx}] Tab ${tab.txt} — already active, skipping`);
            continue;
          }
          const tabClicked = tab.mainDom
            ? await page.evaluate((t) => window.__ems.clickMainDomTab(t), tab)
            : await page.evaluate((t) => window.__ems.clickShadowTab(t.id), tab);
          await pause(W.PAGE_CLICK);
          await waitForReady(page);
          const tabPages = await collectPage(tab.txt + '/');
          // Skip if cards match default view
          const tabCardNames = tabPages.flatMap(p => p.cards || []).map(c => c.name).sort().join(',');
          if (tabCardNames === defaultCardNames) {
            log(`    [${saIdx}] Tab ${tab.txt} — same as default, skipping`);
            continue;
          }
          log(`    [${saIdx}] Sub-tab: ${tab.txt}: ${tabPages.reduce((s, p) => s + (p.cards ? p.cards.length : 0), 0)} cards`);
          saRes.pages.push(...tabPages);
        }
      }

      // 6号 A座 1F special BM page detection
      if (bldg.building === '6号' && target.x <= 650 && target.floor === 1) {
        if (!(await waitForReady(page))) { /* continue without BM */ }
        else {
          await closeModals(page);
          const sBtns = await page.evaluate(() => window.__ems.findSpecialPageBtns());
          if (sBtns['BM']) {
            const clickedBM = await page.evaluate((id) => window.__ems.clickById(id), sBtns['BM']);
            if (clickedBM) {
              await pause(W.BM_CLICK);
              if (await waitForReady(page)) {
                await waitForDataReady(page);
                await waitForSvgStable(page);
                const dataBM = await page.evaluate(() => window.__ems.extractCards());
                saRes.pages.push(pageFromData('BM', dataBM));
                // Return to 1F view — BM view breaks subsequent floor navigation
                // SVG re-renders after BM click, so element IDs change — re-scan fresh
                for (let r = 0; r < 3; r++) {
                  await closeModals(page);
                  const _1fId = await page.evaluate(() => {
                    const sr = document.querySelector('.pi-svg-container')?.shadowRoot;
                    if (!sr) return null;
                    for (const g of sr.querySelectorAll('g')) {
                      let txt = '';
                      for (const c2 of g.childNodes) { if (c2.nodeType === 3) txt += c2.textContent; }
                      txt = txt.trim();
                      if (!txt) { const t = g.querySelector(':scope > text, :scope > tspan'); if (t) txt = (t.textContent || '').trim(); }
                      if (txt === '1F') { const r = g.getBoundingClientRect(); if (r.left > 1500) return g.id; }
                    }
                    return null;
                  });
                  if (_1fId) {
                    const ok = await page.evaluate((id) => window.__ems.clickById(id), _1fId);
                    if (ok) { await pause(W.BM_CLICK); if (await waitForReady(page)) break; }
                  }
                  await pause(500);
                }
              }
            }
          }
        }
      }

      const totalCards = saRes.pages.reduce((sum, p) => sum + (p && p.cards ? p.cards.length : 0), 0);
      bldgCardAcc += totalCards;
      process.stdout.write('[PROGRESS]' + JSON.stringify({
        t: 'c', bldg: bldg.building,
        cards: totalCards, acc: bldgCardAcc,
        totalSa: subAreas.length, curSa: saIdx + 1
      }) + '\n');
      log(`    [${saIdx}/${subAreas.length}] F${target.floor}${subTabs.length > 0 ? ' +' + subTabs.length + ' tabs' : ''}: ${totalCards} cards`);
      bRes.subAreas.push(saRes);
    }

    allResults.push(bRes);
    const bTotal = bRes.subAreas.reduce((sum, sa) => sum + (sa.pages ? sa.pages.reduce((s, p) => s + (p && p.cards ? p.cards.length : 0), 0) : 0), 0);
    log(`  ${bldg.building} total: ${bTotal} cards`);
  }

  const output = { buildings: allResults, completedAt: new Date().toISOString() };
  const validation = validateEnumData(output, { buildings: bldgs.map(b => b.building) });
  for (const line of formatValidation(validation)) log(`  ${line}`);
  const qualityGateIssues = auditCollectedOutput(output);
  for (const issue of qualityGateIssues.slice(0, 20)) {
    loggerLog(LEVELS.ERROR, 'QUALITY', `QUALITY GATE FAIL ${issue.building} F${issue.floor} ${issue.subArea} ${issue.page}: ${issue.details} reason=${issue.reason}`);
  }
  if (!validation.ok || qualityGateIssues.length) {
    const stamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z');
    const rejectedFile = path.join(OUT_DIR, `enum_rejected_${stamp}.json`);
    fs.mkdirSync(OUT_DIR, { recursive: true });
    fs.writeFileSync(rejectedFile, JSON.stringify(output, null, 2), 'utf-8');
    log(`采集结果校验失败，未覆盖 ${OUT_FILE}`);
    if (qualityGateIssues.length) log(`质量门槛失败: ${qualityGateIssues.length} 页`);
    log(`异常采集已另存: ${rejectedFile}`);
    close();
    await browser.close();
    process.exit(2);
  }

  // Save each building (append mode)
  for (const bRes of allResults) saveOutput(bRes);
  log(`Saved: ${OUT_FILE}`);

  // Summary
  const totalCards = allResults.reduce((sum, b) =>
    sum + (b.subAreas ? b.subAreas.reduce((s, sa) =>
      s + (sa.pages ? sa.pages.reduce((s2, p) => s2 + (p && p.cards ? p.cards.length : 0), 0) : 0), 0) : 0), 0);
  const totalSubAreas = allResults.reduce((sum, b) => sum + ((b.subAreas||[]).filter(sa => !sa.err).length), 0);
  log(`Done. ${totalCards} cards, ${totalSubAreas} sub-areas across ${allResults.length} buildings`);

  close();
  await browser.close();
}

main().catch(e => { console.error(e); process.exit(1); });
