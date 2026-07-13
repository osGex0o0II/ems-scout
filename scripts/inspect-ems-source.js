#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');
const { sanitizeErrorForDisplay, sanitizeUrlForDisplay } = require('../src/url-sanitizer');

function argValue(name, fallback = '') {
  const hit = process.argv.find(a => a.startsWith(name + '='));
  return hit ? hit.slice(name.length + 1) : fallback;
}

function pause(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

const CDP_URL = argValue('--cdp-url', process.env.CDP_URL || 'http://127.0.0.1:9222');
const EMS_URL = process.env.EMS_URL || 'http://172.29.248.4:8000/ui';
const BUILDING = argValue('--building', '1号');
const FLOOR_TEXT = argValue('--floor-text', '17F');
const PAGE_TEXT = argValue('--page', '二页');
const DEVICE = argValue('--device', '1717-KT-2');
const OUT_DIR = path.resolve(argValue('--out-dir', path.join(__dirname, '..', 'out', 'source-inspect')));

function isEmsPage(url) {
  try {
    const expected = new URL(EMS_URL);
    const current = new URL(url);
    return current.host === expected.host && current.pathname.includes('/ui');
  } catch {
    return url.includes('/ui');
  }
}

async function waitForReady(page, timeoutMs = 10000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const ready = await page.evaluate(() => {
      const c = document.querySelector('.pi-svg-container');
      const svg = c && c.shadowRoot ? c.shadowRoot.querySelector('svg') : null;
      return !!(svg && svg.querySelectorAll('text').length > 5);
    }).catch(() => false);
    if (ready) return true;
    await pause(200);
  }
  return false;
}

async function waitForSvgSettled(page, rounds = 3) {
  let last = '';
  let stable = 0;
  for (let i = 0; i < 40; i++) {
    const sig = await page.evaluate(() => {
      const c = document.querySelector('.pi-svg-container');
      const svg = c && c.shadowRoot ? c.shadowRoot.querySelector('svg') : null;
      if (!svg) return '';
      return Array.from(svg.querySelectorAll('text')).map(t => t.textContent).join('|') + '::' +
        Array.from(svg.querySelectorAll('image')).map(img => img.getAttribute('href') || img.getAttribute('xlink:href') || '').join('|');
    }).catch(() => '');
    if (sig && sig === last) {
      stable++;
      if (stable >= rounds) return true;
    } else {
      stable = 0;
      last = sig;
    }
    await pause(200);
  }
  return false;
}

async function clickMenu(page, building) {
  const pattern = building.replace('号', '号');
  return page.evaluate((needle) => {
    const candidates = Array.from(document.querySelectorAll('*'))
      .filter(el => (el.textContent || '').includes(needle) && el.getBoundingClientRect().width > 0 && el.getBoundingClientRect().height > 0)
      .sort((a, b) => a.getBoundingClientRect().width - b.getBoundingClientRect().width);
    const el = candidates[0];
    if (!el) return false;
    el.click();
    return true;
  }, pattern);
}

async function clickSvgText(page, text) {
  return page.evaluate((targetText) => {
    const c = document.querySelector('.pi-svg-container');
    const sr = c && c.shadowRoot;
    if (!sr) return { ok: false, reason: 'no shadow' };
    const groups = Array.from(sr.querySelectorAll('g'));
    for (const g of groups) {
      let txt = '';
      for (const n of g.childNodes) {
        if (n.nodeType === 3) txt += n.textContent;
      }
      txt = txt.trim();
      if (!txt) {
        const t = g.querySelector(':scope > text, :scope > tspan');
        if (t) txt = (t.textContent || '').trim();
      }
      if (txt !== targetText) continue;
      const r = g.getBoundingClientRect();
      const ev = new MouseEvent('click', {
        bubbles: true,
        cancelable: true,
        clientX: r.left + r.width / 2,
        clientY: r.top + r.height / 2,
      });
      g.dispatchEvent(ev);
      return { ok: true, id: g.id, x: Math.round(r.left), y: Math.round(r.top) };
    }
    return { ok: false, reason: 'not found' };
  }, text);
}

async function collectEvidence(page, device) {
  return page.evaluate((deviceName) => {
    const c = document.querySelector('.pi-svg-container');
    const sr = c && c.shadowRoot;
    const svg = sr ? sr.querySelector('svg') : null;
    const textItems = svg ? Array.from(svg.querySelectorAll('text')).map(t => {
      const r = t.getBoundingClientRect();
      return {
        id: t.id || (t.parentElement && t.parentElement.id) || '',
        x: Math.round(r.left + r.width / 2),
        y: Math.round(r.top + r.height / 2),
        left: Math.round(r.left),
        top: Math.round(r.top),
        text: (t.textContent || '').trim(),
      };
    }).filter(t => t.text) : [];
    const imageItems = svg ? Array.from(svg.querySelectorAll('image')).map(img => {
      const r = img.getBoundingClientRect();
      return {
        id: img.id || (img.parentElement && img.parentElement.id) || '',
        x: Math.round(r.left + r.width / 2),
        y: Math.round(r.top + r.height / 2),
        w: Math.round(r.width),
        h: Math.round(r.height),
        href: (img.getAttribute('href') || img.getAttribute('xlink:href') || '').split('/').pop(),
      };
    }) : [];
    const nameItem = textItems.find(t => t.text === deviceName);
    const aroundDevice = nameItem
      ? {
          nameItem,
          texts: textItems
            .filter(t => Math.abs(t.x - nameItem.x) <= 180 && t.y >= nameItem.y - 120 && t.y <= nameItem.y + 330)
            .sort((a, b) => a.y - b.y || a.x - b.x),
          images: imageItems
            .filter(img => Math.abs(img.x - nameItem.x) <= 180 && img.y >= nameItem.y - 140 && img.y <= nameItem.y + 330)
            .sort((a, b) => a.y - b.y || a.x - b.x),
        }
      : null;

    const vueRoot = document.querySelector('.pi-graphics-configuration-svg-new');
    const vueData = vueRoot && vueRoot.__vue__ ? vueRoot.__vue__.$data : null;
    const ws = vueData && vueData.websocketDataProp ? vueData.websocketDataProp : {};
    const rc = vueData && Array.isArray(vueData.runConfDataProp) ? vueData.runConfDataProp : [];
    const sl = vueData && Array.isArray(vueData.svgListDraw) ? vueData.svgListDraw : [];

    const ptIdToName = {};
    for (const e of sl) {
      if (!e || !e.dyn || !Array.isArray(e.dyn.listDyn)) continue;
      for (const ld of e.dyn.listDyn) {
        if (ld.DynType !== 23 || ld.PropertyName !== 'CabinetId') continue;
        let pid = null;
        try { pid = JSON.parse(ld.PtPath).ptId; } catch {}
        if (!pid) continue;
        const el = sr ? (sr.getElementById(e.id) || sr.querySelector('#' + CSS.escape(e.id))) : null;
        const name = el ? (el.textContent || '').trim() : '';
        if (name) ptIdToName[String(pid)] = name;
      }
    }

    const wsEntries = Object.entries(ws).map(([key, item]) => ({
      key,
      tag: item && item.tag ? item.tag : null,
      value: item && item.tag ? item.tag.value : null,
    }));
    const configRows = rc.map((row, index) => {
      const conf = row && row.ptPathConf ? row.ptPathConf : {};
      const value = ws[String(index)] && ws[String(index)].tag ? ws[String(index)].tag.value : null;
      return {
        index,
        devId: conf.devId,
        ptId: conf.ptId,
        name: conf.name,
        unit: conf.unit,
        value,
        deviceName: ptIdToName[String(conf.devId)] || ptIdToName[String(conf.ptId)] || '',
      };
    });
    const deviceRows = configRows.filter(row => row.deviceName === deviceName);
    const suspiciousRows = configRows.filter(row => {
      const value = String(row.value ?? '');
      return value.includes('1615') || value.includes('3301') || row.deviceName === deviceName;
    });
    const configWindowRows = configRows.filter(row => row.index >= 40 && row.index <= 60);
    const enumLike = (() => {
      function nearest(arr, x, y, xMax, yMax) {
        let best = null;
        let bestDist = 999999;
        for (const it of arr) {
          const dx = Math.abs(it.x - x);
          const dy = Math.abs(it.y - y);
          if (dx > xMax || dy > yMax) continue;
          const d = dx * dx + dy * dy;
          if (d < bestDist) {
            bestDist = d;
            best = it;
          }
        }
        return best;
      }
      const byY = {};
      for (const it of textItems) {
        const key = it.y;
        if (!byY[key]) byY[key] = [];
        byY[key].push({ x: it.x, y: it.y, txt: it.text });
      }
      const switchImgs = imageItems.filter(i => i.w >= 38 && i.w <= 50 && i.h >= 17 && i.h <= 30);
      const indicatorImgs = imageItems.filter(i => i.w >= 25 && i.w <= 33 && i.h >= 23 && i.h <= 31);
      const switchByHref = {};
      for (const si of switchImgs) {
        if (!si.href) continue;
        switchByHref[si.href] = (switchByHref[si.href] || 0) + 1;
      }
      const hrefs = Object.keys(switchByHref).sort((a, b) => switchByHref[b] - switchByHref[a]);
      const offHref = hrefs[0] || null;
      const onHref = hrefs[1] || null;
      const nameRows = [];
      for (const yStr in byY) {
        const arr = byY[yStr];
        if (arr.length >= 2 && arr.every(it => /^[A-Z0-9\-]+$/i.test(it.txt) && it.txt.length >= 5 && it.txt.length < 20)) {
          if (arr.every(it => /^[A-Z]?\d+F$/i.test(it.txt))) continue;
          nameRows.push({ y: parseInt(yStr, 10), items: arr });
        }
      }
      nameRows.sort((a, b) => a.y - b.y);
      const modeTexts = textItems.filter(i => /^(制冷|通风|制热|送暖|地暖|制热\+地暖)$/.test(i.text)).map(i => ({ x: i.x, y: i.y, txt: i.text }));
      const fanTexts = textItems.filter(i => /^(自动|高|中|低|0|1|2|3)$/.test(i.text)).map(i => ({ x: i.x, y: i.y, txt: i.text }));
      const tempTexts = textItems.filter(i => /\d+(\.\d+)?\s*℃/.test(i.text)).map(i => ({ x: i.x, y: i.y, txt: i.text }));
      const rawCards = [];
      for (let rowIndex = 0; rowIndex < nameRows.length; rowIndex++) {
        const row = nameRows[rowIndex];
        const nextRowY = nameRows[rowIndex + 1] ? nameRows[rowIndex + 1].y : Infinity;
        const rowBottom = Math.min(row.y + 285, nextRowY - 20);
        for (const nameIt of row.items) {
          const x = nameIt.x;
          const ry = row.y;
          const sw = nearest(switchImgs, x, ry + 100, 80, 50);
          let swState = '-';
          if (sw) {
            if (sw.href === onHref) swState = 'ON';
            else if (sw.href === offHref) swState = 'OFF';
          }
          const mode = nearest(modeTexts, x, ry + 140, 100, 60);
          const nearTemps = tempTexts.filter(i =>
            Math.abs(i.x - x) <= 100 && i.y > ry + 100 && i.y < rowBottom
          ).sort((a, b) => a.y - b.y);
          const fan = nearest(fanTexts, x, ry + 235, 100, 60);
          const indic = nearest(indicatorImgs, x, ry - 30, 80, 50);
          rawCards.push({
            name: nameIt.txt,
            rowY: row.y,
            rowBottom,
            switch: swState,
            mode: mode ? mode.txt : '-',
            indoor: nearTemps[0] ? nearTemps[0].txt.replace(/\s*℃/, '') : '-',
            setTemp: nearTemps[1] ? nearTemps[1].txt.replace(/\s*℃/, '') : '-',
            fan: fan ? fan.txt : '-',
            indicator: indic ? indic.href : '',
          });
        }
      }
      const FIELD_DEF = {
        '当前开关机模式': { field: 'switch', enum: { '0': 'OFF', '1': 'ON' }, valid: v => v === 'OFF' || v === 'ON' },
        '开关机': { field: 'switch', skip: true },
        '系统模式设置': { field: 'mode', enum: { '0': '通风', '1': '制冷', '2': '制热', '3': '地暖', '4': '制热+地暖' }, valid: v => /^(通风|制冷|制热|地暖|制热\+地暖)$/.test(v) },
        '室内温度': { field: 'indoor', valid: v => /^\d+(\.\d+)?$/.test(v) },
        '当前设置温度': { field: 'setTemp', valid: v => /^\d+(\.\d+)?$/.test(v) },
        '设定风速': { field: 'fan', enum: { '1': '低', '2': '中', '3': '高', '4': '自动' }, valid: v => /^(低|中|高|自动)$/.test(v) },
      };
      const devFields = {};
      for (let i = 0; i < rc.length; i++) {
        const conf = rc[i] && rc[i].ptPathConf ? rc[i].ptPathConf : {};
        if (!conf.devId || !conf.name) continue;
        const def = FIELD_DEF[conf.name];
        if (!def || def.skip) continue;
        const raw = ws[String(i)] && ws[String(i)].tag ? ws[String(i)].tag.value : null;
        if (raw === null) continue;
        let val = raw;
        if (def.enum) val = def.enum[raw] || raw;
        if (def.valid && !def.valid(val)) continue;
        if (!devFields[conf.devId]) devFields[conf.devId] = {};
        devFields[conf.devId][def.field] = val;
      }
      const nameToDev = {};
      for (const [ptIdStr, name] of Object.entries(ptIdToName)) {
        const pid = parseInt(ptIdStr, 10);
        if (devFields[pid]) nameToDev[name] = pid;
      }
      const interesting = rawCards
        .filter(c => c.name === deviceName || c.name === '1716B-KT' || c.name === '1717-KT-1')
        .map(card => ({
          raw: card,
          devId: nameToDev[card.name] || null,
          vueFields: nameToDev[card.name] ? devFields[nameToDev[card.name]] || null : null,
        }));
      return { rawCards: rawCards.length, interesting, nameToDev };
    })();

    return {
      url: location.href,
      title: document.title,
      textCount: textItems.length,
      imageCount: imageItems.length,
      wsCount: Object.keys(ws).length,
      runConfCount: rc.length,
      svgListDrawCount: sl.length,
      wsEntries,
      configWindowRows,
      enumLike,
      aroundDevice,
      deviceRows,
      suspiciousRows,
    };
  }, device);
}

async function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const browser = await chromium.connectOverCDP(CDP_URL);
  const context = browser.contexts()[0] || await browser.newContext();
  let page = context.pages().find(p => isEmsPage(p.url())) || context.pages()[0] || await context.newPage();
  if (!isEmsPage(page.url())) await page.goto(EMS_URL, { waitUntil: 'domcontentloaded' });

  if (!(await waitForReady(page, 3000))) {
    await clickMenu(page, BUILDING);
    await waitForReady(page, 10000);
  } else {
    await clickMenu(page, BUILDING);
    await waitForReady(page, 10000);
  }
  await pause(500);
  if (!(await waitForReady(page, 10000))) throw new Error('EMS SVG not ready after building click');
  const floorClick = await clickSvgText(page, FLOOR_TEXT);
  if (!floorClick.ok) throw new Error(`Floor ${FLOOR_TEXT} not found: ${floorClick.reason}`);
  await pause(700);
  await waitForReady(page, 10000);
  await waitForSvgSettled(page);
  const pageClick = await clickSvgText(page, PAGE_TEXT);
  if (!pageClick.ok) throw new Error(`Page ${PAGE_TEXT} not found: ${pageClick.reason}`);
  await pause(1000);
  await waitForReady(page, 10000);
  await waitForSvgSettled(page);

  const evidence = await collectEvidence(page, DEVICE);
  evidence.request = { cdpUrl: CDP_URL, emsUrl: sanitizeUrlForDisplay(EMS_URL), building: BUILDING, floorText: FLOOR_TEXT, pageText: PAGE_TEXT, device: DEVICE };
  evidence.capturedAt = new Date().toISOString();
  const jsonPath = path.join(OUT_DIR, `${DEVICE.replace(/[^\w.-]+/g, '_')}_source_evidence.json`);
  fs.writeFileSync(jsonPath, JSON.stringify(evidence, null, 2), 'utf8');
  const screenshotPath = path.join(OUT_DIR, `${DEVICE.replace(/[^\w.-]+/g, '_')}_page.png`);
  await page.screenshot({ path: screenshotPath, fullPage: true });
  await browser.close();
  console.log('Saved:', jsonPath);
  console.log('Saved:', screenshotPath);
  console.log(JSON.stringify({
    textCount: evidence.textCount,
    imageCount: evidence.imageCount,
    wsCount: evidence.wsCount,
    runConfCount: evidence.runConfCount,
    nameFound: !!evidence.aroundDevice,
    deviceRows: evidence.deviceRows.length,
    suspiciousRows: evidence.suspiciousRows.length,
  }, null, 2));
}

main().catch(err => {
  console.error(sanitizeErrorForDisplay(err, [EMS_URL]));
  process.exit(1);
});
