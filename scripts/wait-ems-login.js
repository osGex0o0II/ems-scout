#!/usr/bin/env node
'use strict';

const { chromium } = require('playwright');
const { sanitizeErrorForDisplay, sanitizeUrlForDisplay } = require('../src/url-sanitizer');

function argValue(name, fallback = '') {
  const hit = process.argv.find(a => a.startsWith(name + '='));
  return hit ? hit.slice(name.length + 1) : fallback;
}

const cdpUrl = argValue('--cdp-url', process.env.CDP_URL || 'http://127.0.0.1:9222');
const emsUrl = process.env.EMS_URL || 'http://172.29.248.4:8000/ui';
const timeoutSeconds = Math.max(1, Number(argValue('--timeout-seconds', '120')) || 120);

function isEmsPageUrl(url) {
  if (!url || url === 'about:blank') return false;
  try {
    const expected = new URL(emsUrl);
    const current = new URL(url);
    return current.host === expected.host &&
      (current.pathname.includes('/ui') || expected.pathname.includes(current.pathname));
  } catch {
    return url.includes('/ui');
  }
}

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
  } catch {
    return false;
  }
}

async function main() {
  const browser = await chromium.connectOverCDP(cdpUrl);
  try {
    const context = browser.contexts()[0];
    if (!context) throw new Error('No CDP browser context');
    let page = context.pages().find(p => isEmsPageUrl(p.url())) || context.pages()[0];
    if (!page) page = await context.newPage();
    await page.bringToFront().catch(() => {});
    if (!isEmsPageUrl(page.url())) {
      await page.goto(emsUrl, { waitUntil: 'domcontentloaded', timeout: 30000 });
    }

    const deadline = Date.now() + timeoutSeconds * 1000;
    let lastRemain = -1;
    while (Date.now() < deadline) {
      if (await isLoggedIn(page)) {
        console.log('EMS_LOGIN_OK url=' + sanitizeUrlForDisplay(page.url()));
        return;
      }
      const remain = Math.max(0, Math.ceil((deadline - Date.now()) / 1000));
      if (remain !== lastRemain && (remain % 5 === 0 || remain <= 10)) {
        console.log(`EMS_LOGIN_WAIT remaining=${remain}s url=${sanitizeUrlForDisplay(page.url())}`);
        lastRemain = remain;
      }
      await page.waitForTimeout(1000);
    }

    console.error('EMS_LOGIN_TIMEOUT url=' + sanitizeUrlForDisplay(page.url()));
    process.exit(3);
  } finally {
    await browser.close().catch(() => {});
  }
}

main().catch(err => {
  console.error('EMS_LOGIN_ERROR ' + sanitizeErrorForDisplay(err, [emsUrl]));
  process.exit(1);
});
