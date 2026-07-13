// verify-live.js — 实时浏览器状态验证
// Connects to live Edge (CDP), reads current EMS page state,
// extracts cards via full enumerate.js logic, compares against DB.
// Usage: node src/verify-live.js [--building=1号] [--floor=1] [--json]
const path = require('path');
const { chromium } = require('playwright');
const Database = require('better-sqlite3');
const { filterFloorGroups } = require('./page-navigation');
const { sanitizeUrlForDisplay } = require('./url-sanitizer');
const { log: loggerLog, setLevel, setCategories, enableFileLog, close, LEVELS, CATEGORIES } = require('./logger');
function log(...args) { loggerLog(LEVELS.INFO, 'ENUM', ...args); }

function formatDuplicateNames(list = []) {
  if (!Array.isArray(list) || list.length === 0) return '';
  return list
    .filter(d => d && d.name)
    .map(d => `${d.name}x${Number(d.copies) || 2}`)
    .join(', ');
}

const CDP_URL = 'http://127.0.0.1:9222';
const EMS_URL = 'http://172.29.248.4:8000/ui';
const ROOT = path.resolve(__dirname, '..');
const DB_PATH = path.join(ROOT, 'out', 'ac.db');

// Logger configuration
const LOG_LEVEL_ARG = process.argv.find(a => a.startsWith('--log-level='));
if (LOG_LEVEL_ARG) setLevel(LOG_LEVEL_ARG.split('=')[1]);
const LOG_CAT_ARG = process.argv.find(a => a.startsWith('--log-category='));
if (LOG_CAT_ARG) setCategories(LOG_CAT_ARG.split('=')[1]);
if (process.argv.includes('--log-file')) enableFileLog(path.join(ROOT, 'out'));

const BUILDING_FILTER = process.argv.find(a => a.startsWith('--building='))?.split('=')[1];
const FLOOR_FILTER = process.argv.find(a => a.startsWith('--floor='))?.split('=')[1];
const X_FILTER = process.argv.find(a => a.startsWith('--x='))?.split('=')[1];
const Y_FILTER = process.argv.find(a => a.startsWith('--y='))?.split('=')[1];
const PAGE_FILTER = process.argv.find(a => a.startsWith('--page='))?.split('=')[1];
const OUTPUT_JSON = process.argv.includes('--json');
let cdpBrowser = null;

async function disconnectBrowser() {
  if (!cdpBrowser) return;
  await cdpBrowser.close().catch(() => {});
  cdpBrowser = null;
}

async function connectBrowser() {
  const browser = await chromium.connectOverCDP(CDP_URL);
  cdpBrowser = browser;
  const context = browser.contexts()[0];
  const pages = context.pages();
  let page = null;
  if (BUILDING_FILTER) {
    for (const p of pages) {
      if (!p.url().includes('172.29.248.4') || p.url().includes('#/login')) continue;
      const active = await p.evaluate(() => {
        const el = document.querySelector('.ivu-menu-item-active, .ivu-menu-item-selected');
        return (el && el.textContent || '').trim();
      }).catch(() => '');
      if (active.startsWith(BUILDING_FILTER)) { page = p; break; }
    }
  }
  page = page ||
    pages.find(p => p.url().includes('172.29.248.4') && !p.url().includes('#/login')) ||
    pages.find(p => p.url().includes('172.29.248.4')) ||
    pages[0];
  if (!page.url().includes('172.29.248.4')) {
    await page.goto(EMS_URL, { waitUntil: 'domcontentloaded', timeout: 30000 });
  }
  await page.waitForTimeout(1000);
  return { browser, page };
}

async function waitForReady(page, maxRetries = 20) {
  for (let i = 0; i < maxRetries; i++) {
    const ok = await page.evaluate(() => window.__ems && window.__ems.isReady()).catch(() => false);
    if (ok) return true;
    await page.waitForTimeout(250);
  }
  return false;
}

async function clickMenu(page, building) {
  if (!building) return null;
  const clicked = await page.evaluate((building) => {
    const re = new RegExp('^' + building);
    for (const el of document.querySelectorAll('.ivu-menu-item')) {
      const txt = (el.textContent || '').trim();
      if (re.test(txt) && /楼|空调|服务/.test(txt)) {
        el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
        return txt;
      }
    }
    return null;
  }, building);
  if (!clicked) return null;
  await page.waitForTimeout(800);
  await injectHelpers(page);
  await waitForReady(page, 30);
  return clicked;
}

async function clickFloor(page, floorValue) {
  if (floorValue === undefined) return null;
  const floorNum = Number(floorValue);
  if (!Number.isFinite(floorNum)) throw new Error(`Invalid --floor value: ${floorValue}`);
  let subAreas = filterFloorGroups(await page.evaluate(() => window.__ems.findAllSubAreaGroups()).catch(() => []));
  const targetX = X_FILTER === undefined ? null : Number(X_FILTER);
  const targetY = Y_FILTER === undefined ? null : Number(Y_FILTER);
  let candidates = subAreas.filter(s => Number(s.floor) === floorNum);
  if (Number.isFinite(targetX) || Number.isFinite(targetY)) {
    candidates = candidates.sort((a, b) => {
      const da = Math.abs((Number.isFinite(targetX) ? a.x - targetX : 0)) + Math.abs((Number.isFinite(targetY) ? a.y - targetY : 0));
      const db = Math.abs((Number.isFinite(targetX) ? b.x - targetX : 0)) + Math.abs((Number.isFinite(targetY) ? b.y - targetY : 0));
      return da - db;
    });
  }
  let target = candidates[0];
  if (!target) {
    await waitForReady(page, 20);
    subAreas = filterFloorGroups(await page.evaluate(() => window.__ems.findAllSubAreaGroups()).catch(() => []));
    candidates = subAreas.filter(s => Number(s.floor) === floorNum);
    if (Number.isFinite(targetX) || Number.isFinite(targetY)) {
      candidates = candidates.sort((a, b) => {
        const da = Math.abs((Number.isFinite(targetX) ? a.x - targetX : 0)) + Math.abs((Number.isFinite(targetY) ? a.y - targetY : 0));
        const db = Math.abs((Number.isFinite(targetX) ? b.x - targetX : 0)) + Math.abs((Number.isFinite(targetY) ? b.y - targetY : 0));
        return da - db;
      });
    }
    target = candidates[0];
  }
  if (!target) {
    const available = subAreas.map(s => s.text).join(', ') || 'none';
    throw new Error(`Floor ${floorValue} not found; available: ${available}`);
  }
  const ok = await page.evaluate(id => window.__ems.clickById(id), target.id);
  if (!ok) throw new Error(`Click floor failed: ${target.text}`);
  await page.waitForTimeout(1500);
  await injectHelpers(page);
  await waitForReady(page, 30);
  return target;
}

async function clickPageButton(page, pageName) {
  if (!pageName) return null;
  const btns = await page.evaluate(() => window.__ems.findPageBtns()).catch(() => ({}));
  const id = btns[pageName];
  if (!id) {
    const available = Object.keys(btns).join(', ') || 'none';
    throw new Error(`Page button ${pageName} not found; available: ${available}`);
  }
  const ok = await page.evaluate(id => window.__ems.clickById(id), id);
  if (!ok) throw new Error(`Click page failed: ${pageName}`);
  await page.waitForTimeout(1500);
  await injectHelpers(page);
  await waitForReady(page, 30);
  return pageName;
}

function injectHelpers(page) {
  return page.evaluate(() => {
    const VERIFY_HELPER_VERSION = 3;
    if (window.__ems_verify_version === VERIFY_HELPER_VERSION) return;
    window.__ems_verify = true;
    window.__ems_verify_version = VERIFY_HELPER_VERSION;

    const NOISE = /^(BM|1F|2F|3F|B1F|B2F|B3F|F|N|S|E|W|NE|NW|SE|SW|公区|电梯|楼梯|屋顶|机房)$/i;
    const log = () => {};
    const LEVELS = { DEBUG: 10 };

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
            groups.push({ id: g.id, floor, text: txt, x: Math.round(r.left), y: Math.round(r.top) });
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
      findSubTabs() {
        const sr = getShadow();
        if (!sr) return [];
        const tabs = [];
        for (const g of sr.querySelectorAll('g')) {
          let txt = '';
          for (const c2 of g.childNodes) {
            if (c2.nodeType === 3) txt += c2.textContent;
          }
          txt = (txt || g.textContent || '').trim();
          if (!txt) continue;
          const cls = g.getAttribute('class') || '';
          if (/^(裙楼|塔楼|主楼|辅楼|A座|B座|C座|D座|E座|F座|G座|H座)$/.test(txt)) {
            tabs.push({ id: g.id, txt, isActive: cls.includes('item-active') });
          }
        }
        return tabs;
      },
      clickById(id) {
        const sr = getShadow();
        if (!sr) return false;
        const el = sr.getElementById(id);
        if (!el) return false;
        el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
        return true;
      },
      // extractCards: full logic from enumerate.js
      extractCards() {
        const sr = getShadow();
        if (!sr) return { count: 0, rawCount: 0, uniqueCount: 0, duplicateNames: [], cards: [], layout: 'unknown' };
        const svg = sr.querySelector('svg');
        if (!svg) return { count: 0, rawCount: 0, uniqueCount: 0, duplicateNames: [], cards: [], layout: 'unknown' };

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
        let offHref = null, onHref = null;

        const indicatorImgs = imgList.filter(i => i.w >= 25 && i.w <= 33 && i.h >= 23 && i.h <= 31);

        // Detect layout
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

          const modeTexts = items.filter(i => /^(制冷|通风|制热|送暖|地暖|制热\+地暖)$/.test(i.txt));
          const fanTexts = items.filter(i => /^(自动|高|中|低|0|1|2|3)$/.test(i.txt));
          const tempTexts = items.filter(i => /\d+(\.\d+)?\s*℃/.test(i.txt));

          for (const row of nameRows) {
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
              const nearTemps = tempTexts.filter(i =>
                Math.abs(i.x - x) <= 100 && i.y > ry + 100 && i.y < ry + 300
              ).sort((a, b) => a.y - b.y);
              const indoor = nearTemps[0] ? nearTemps[0].txt.replace(/\s*℃/, '') : '-';
              const setT = nearTemps[1] ? nearTemps[1].txt.replace(/\s*℃/, '') : '-';
              const fan = nearest(fanTexts, x, ry + 235, 100, 60);
              const indic = nearest(indicatorImgs, x, ry - 30, 80, 50);
              cards.push({
                name: nameIt.txt, switch: swState,
                mode: mode ? mode.txt : '-', indoor, setTemp: setT,
                fan: fan ? fan.txt : '-', indicator: indic ? indic.href : ''
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
            const mode = nearest(modeTextsG, dn.x, dn.y + 110, 80, 60);
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

        // Vue enrichment
        try {
          const w = document.querySelector('.pi-graphics-configuration-svg-new');
          if (w && w.__vue__) {
            const d = w.__vue__.$data;
            const rc = d.runConfDataProp || [];
            const ws = d.websocketDataProp || {};
            const sl = d.svgListDraw || [];
            const sr2 = (document.querySelector('.pi-svg-container') || {}).shadowRoot;

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

            const nameToDev = {};
            for (const [ptIdStr, name] of Object.entries(ptIdToName)) {
              const pid = parseInt(ptIdStr);
              if (devFields[pid]) nameToDev[name] = pid;
            }

            for (const card of cards) {
              const devId = nameToDev[card.name];
              if (!devId || !devFields[devId]) continue;
              const f = devFields[devId];
              if (f.switch) card.switch = f.switch;
              if (f.mode && card.mode === '-') { card.mode = f.mode; log(LEVELS.DEBUG, 'VUE', `mode -→${f.mode}`, { card: card.name }); }
              if (f.indoor) {
                const svgVal = parseFloat(card.indoor);
                const vueVal = parseFloat(f.indoor);
                if (card.indoor === '-' || (isNaN(svgVal) && !isNaN(vueVal)) ||
                    (!isNaN(vueVal) && vueVal >= 0 && vueVal <= 60 && (isNaN(svgVal) || svgVal <= 0 || svgVal > 60))) {
                  if (card.indoor !== f.indoor) log(LEVELS.DEBUG, 'VUE', `indoor ${card.indoor}→${f.indoor}`, { card: card.name });
                  card.indoor = f.indoor;
                }
              }
              if (f.setTemp) {
                const svgVal = parseFloat(card.setTemp);
                const vueVal = parseFloat(f.setTemp);
                if (card.setTemp === '-' || (isNaN(svgVal) && !isNaN(vueVal)) ||
                    (!isNaN(vueVal) && vueVal >= 5 && vueVal <= 40 && (isNaN(svgVal) || svgVal < 5 || svgVal > 40))) {
                  if (card.setTemp !== f.setTemp) log(LEVELS.DEBUG, 'VUE', `setTemp ${card.setTemp}→${f.setTemp}`, { card: card.name });
                  card.setTemp = f.setTemp;
                }
              }
              if (f.fan) {
                if (card.fan === '-' || /^\d$/.test(card.fan)) {
                  if (card.fan !== f.fan) log(LEVELS.DEBUG, 'VUE', `fan ${card.fan}→${f.fan}`, { card: card.name });
                  card.fan = f.fan;
                }
              }
            }
          }
        } catch (_) {}

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

async function verifyPageState(page) {
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
    return {
      ready: window.__ems.isReady(),
      shadowDOM: !!shadowOk, svgCount, textCount, wsDataCount,
      url: location.href, title: document.title
    };
  });
}

async function main() {
  log('Connecting to live Edge browser...');
  const { browser, page } = await connectBrowser();
  await injectHelpers(page);
  let selectedSubArea = null;
  if (BUILDING_FILTER) {
    const clicked = await clickMenu(page, BUILDING_FILTER);
    log(`Selected building: ${clicked || 'not found'}`);
  }
  if (FLOOR_FILTER !== undefined) {
    const target = await clickFloor(page, FLOOR_FILTER);
    selectedSubArea = target;
    log(`Selected floor: ${target.text} (x=${target.x}, y=${target.y})`);
  }
  if (PAGE_FILTER) {
    await clickPageButton(page, PAGE_FILTER);
    log(`Selected page: ${PAGE_FILTER}`);
  }

  // Step 1: Show page state
  const state = await verifyPageState(page);
  log(`=== Page State ===`);
  log(`URL: ${sanitizeUrlForDisplay(state.url)}  |  Title: ${state.title}`);
  log(`Ready: ${state.ready}  |  Shadow: ${state.shadowDOM}  |  SVG texts: ${state.textCount}  |  WS data: ${state.wsDataCount}`);

  // Step 2: Read current view
  const data = await page.evaluate(() => window.__ems.extractCards());

  // Step 3: Read sub-areas and page buttons from current page
  const pageInfo = await page.evaluate(() => {
    const sas = window.__ems.findAllSubAreaGroups().map(s => ({ floor: s.floor, text: s.text, x: s.x, y: s.y }));
    const btns = Object.keys(window.__ems.findPageBtns());
    const tabs = window.__ems.findSubTabs().map(t => ({ txt: t.txt, active: t.isActive }));
    return { sas, btns, tabs };
  });

  // Identify current building and floor
  const bldgMatch = state.title.match(/(\d+号)/);
  const buildingFromCard = data.cards.length > 0
    ? (() => { const m = data.cards[0].name.match(/^([1-6])-/); return m ? `${m[1]}号` : null; })()
    : null;
  const curBldg = BUILDING_FILTER || (bldgMatch ? bldgMatch[1] : null) || buildingFromCard || '??';
  // Detect floor from card name prefixes (e.g. "3F-WSJ-KT-1" → floor 3)
  const floorFromCard = data.cards.length > 0
    ? (() => { const m = data.cards[0].name.match(/^(\d+)F-/); return m ? parseInt(m[1]) : null; })()
    : null;
  const floorInfo = pageInfo.sas.find(sa => sa.text !== 'BM') || pageInfo.sas[0] || {};
  const viewFloor = floorFromCard || selectedSubArea?.floor || floorInfo.floor || 0;

  log(`\n=== Current View ===`);
  const floorLabel = floorFromCard ? `F${viewFloor} (卡名前缀)` : `F${viewFloor || '?'} (子区)`;
  log(`Building: ${curBldg}  |  Floor: ${floorLabel}  |  Layout: ${data.layout}`);
  log(`Sub-areas: ${pageInfo.sas.length}  |  Page btns: ${pageInfo.btns.join(', ') || 'none'}  |  Tabs: ${pageInfo.tabs.map(t => t.txt).join(', ') || 'none'}`);

  if (!data.cards || data.cards.length === 0) {
    log('\nNo cards extracted. Try clicking a sub-area first.');
    await disconnectBrowser();
    return;
  }

  // Step 4: Summarize extracted cards
  const onCards = data.cards.filter(c => c.comm === '开机');
  const offCards = data.cards.filter(c => c.comm === '关机');
  const offlineCards = data.cards.filter(c => c.comm === '离线');
  const unknownCards = data.cards.filter(c => !c.comm);

  log(`\n=== Extracted Cards (${data.count}) ===`);
  log(`开机: ${onCards.length}  |  关机: ${offCards.length}  |  离线: ${offlineCards.length}  |  未知: ${unknownCards.length}`);
  if ((data.rawCount || data.count) > (data.uniqueCount || data.count)) {
    log(`重复渲染: raw=${data.rawCount} unique=${data.uniqueCount}  ${formatDuplicateNames(data.duplicateNames)}`);
  }

  // Step 5: Compare against DB
  const db = new Database(DB_PATH, { readonly: true });
  const floorNum = FLOOR_FILTER === undefined ? null : Number(FLOOR_FILTER);
  if (FLOOR_FILTER !== undefined && !Number.isFinite(floorNum)) {
    throw new Error(`Invalid --floor value: ${FLOOR_FILTER}`);
  }

  const liveNames = new Set(data.cards.map(c => c.name));

  // Resolve the DB sub-area first. Live SVG coordinates can drift with window
  // size/zoom, so use coordinates for ordering rather than a hard filter.
  const xNum = X_FILTER === undefined ? selectedSubArea?.x ?? null : Number(X_FILTER);
  const yNum = Y_FILTER === undefined ? selectedSubArea?.y ?? null : Number(Y_FILTER);
  const selectedText = selectedSubArea?.text || null;
  const subAreaSql = `
    SELECT id, building, text, floor, x, y,
           (CASE WHEN ? IS NULL THEN 0 ELSE ABS(x - ?) END) +
           (CASE WHEN ? IS NULL THEN 0 ELSE ABS(y - ?) END) AS dist
    FROM sub_areas
    WHERE (? = '??' OR building = ?)
      AND (? IS NULL OR floor = ?)
      AND (? IS NULL OR text = ?)
    ORDER BY dist ASC, id ASC
  `;
  let dbSubAreas = db.prepare(subAreaSql).all(
    Number.isFinite(xNum) ? xNum : null, Number.isFinite(xNum) ? xNum : null,
    Number.isFinite(yNum) ? yNum : null, Number.isFinite(yNum) ? yNum : null,
    curBldg, curBldg,
    floorNum, floorNum,
    selectedText, selectedText
  );
  if (dbSubAreas.length === 0 && selectedText) {
    dbSubAreas = db.prepare(subAreaSql).all(
      Number.isFinite(xNum) ? xNum : null, Number.isFinite(xNum) ? xNum : null,
      Number.isFinite(yNum) ? yNum : null, Number.isFinite(yNum) ? yNum : null,
      curBldg, curBldg,
      floorNum, floorNum,
      null, null
    );
  }
  const dbSubArea = dbSubAreas[0] || null;
  if (dbSubArea) {
    log(`DB sub-area: ${dbSubArea.text} floor=${dbSubArea.floor} x=${dbSubArea.x} y=${dbSubArea.y}${dbSubArea.dist ? ' dist=' + dbSubArea.dist : ''}`);
  }

  const dbSql = `
    SELECT c.name, c.switch, c.mode, c.indoor, c.set_temp, c.fan,
           COALESCE(c.indicator, '') AS indicator,
           c.comm, sa.text, sa.floor, sa.x, sa.y, p.page_name,
           p.raw_count, p.unique_count, p.duplicate_names
    FROM cards c
    JOIN pages p ON c.page_id = p.id
    JOIN sub_areas sa ON p.sub_area_id = sa.id
    WHERE (? = '??' OR sa.building = ?)
      AND (? IS NULL OR sa.floor = ?)
      AND (? IS NULL OR sa.id = ?)
      AND (? IS NULL OR p.page_name = ?)
    ORDER BY c.name
  `;
  // Fetch DB cards for the same building/sub-area when known; otherwise fall back to broader matching.
  const dbCards = db.prepare(dbSql).all(
    curBldg, curBldg,
    floorNum, floorNum,
    dbSubArea ? dbSubArea.id : null, dbSubArea ? dbSubArea.id : null,
    PAGE_FILTER || null, PAGE_FILTER || null
  );
  const pageMeta = dbCards[0] ? {
    page: dbCards[0].page_name,
    rawCount: dbCards[0].raw_count,
    uniqueCount: dbCards[0].unique_count,
    duplicateNames: dbCards[0].duplicate_names,
  } : null;
  if (pageMeta && (pageMeta.rawCount || pageMeta.uniqueCount)) {
    log(`DB page: ${pageMeta.page} raw=${pageMeta.rawCount ?? '-'} unique=${pageMeta.uniqueCount ?? '-'}${pageMeta.duplicateNames ? ' duplicates=' + pageMeta.duplicateNames : ''}`);
  }

  // Deduplicate DB cards by name (take first occurrence per sub-area context)
  const dbMap = {};
  for (const r of dbCards) {
    if (!dbMap[r.name]) dbMap[r.name] = [];
    dbMap[r.name].push(r);
  }

  // Find cards in live but not in DB for this building/floor
  const inLiveNotDb = [];
  for (const c of data.cards) {
    if (!dbMap[c.name]) inLiveNotDb.push(c);
  }
  if (inLiveNotDb.length > 0) {
    log(`\n--- Live but NOT in DB (${inLiveNotDb.length}) ---`);
    for (const c of inLiveNotDb) log(`  ${c.name}  ${c.comm || '-'}`);
  }

  // Cards in DB but not in live for this floor
  const dbNamesForFloor = new Set(dbCards.map(r => r.name));
  const inDbNotLive = [...dbNamesForFloor].filter(n => !liveNames.has(n));
  if (inDbNotLive.length > 0 && inDbNotLive.length < dbNamesForFloor.size) {
    log(`\n--- DB but NOT in live view (${inDbNotLive.length}) ---`);
    for (const n of inDbNotLive.slice(0, 20)) {
      const r = dbMap[n][0];
      log(`  ${n}  (${r.comm})`);
    }
    if (inDbNotLive.length > 20) log(`  ... and ${inDbNotLive.length - 20} more`);
  }

  // Compare values for cards present in both
  log(`\n--- Value Comparison (live vs DB) ---`);
  let matchAll = true;
  let diffCount = 0;
  const FIELDS = ['switch', 'mode', 'indoor', 'setTemp', 'fan', 'comm', 'indicator'];
  const DB_FIELDS = { switch: 'switch', mode: 'mode', indoor: 'indoor', setTemp: 'set_temp', fan: 'fan', comm: 'comm', indicator: 'indicator' };
  const FIELD_LABELS = { switch: '开关', mode: '模式', indoor: '室内温', setTemp: '设定温', fan: '风速', comm: '通讯', indicator: 'indicator' };

  for (const c of data.cards) {
    const dbRow = dbMap[c.name] ? dbMap[c.name][0] : null;
    if (!dbRow) continue;
    for (const f of FIELDS) {
      const lv = String(c[f] ?? '');
      const dv = String(dbRow[DB_FIELDS[f]] ?? '');
      if (lv !== dv) {
        matchAll = false;
        diffCount++;
        if (diffCount <= 30) {
          log(`  ${c.name}  ${FIELD_LABELS[f]}:  live=${lv}  vs  DB=${dv}`);
        }
      }
    }
  }
  if (diffCount > 30) log(`  ... and ${diffCount - 30} more differences`);
  if (matchAll) log(`All values match DB ✓`);

  const result = {
    url: state.url,
    building: curBldg,
    floor: viewFloor,
    page: PAGE_FILTER || null,
    extracted: data.count,
    rawExtracted: data.rawCount ?? data.count,
    uniqueExtracted: data.uniqueCount ?? data.count,
    duplicateNames: data.duplicateNames || [],
    liveNotDb: inLiveNotDb.length,
    dbNotLive: inDbNotLive.length,
    diffs: diffCount,
    pageMeta,
  };
  if (OUTPUT_JSON) console.log(JSON.stringify(result, null, 2));
  log(`\nDone. (${data.count} cards, ${diffCount} diffs)`);
  db.close();
  await disconnectBrowser();
}

main().catch(async e => {
  console.error(e);
  await disconnectBrowser();
  process.exit(1);
});
