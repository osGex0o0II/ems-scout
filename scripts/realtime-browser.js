'use strict';

const fs = require('fs');
const http = require('http');
const path = require('path');
const { spawn } = require('child_process');
const { chromium } = require('playwright');
const { isAllowedEmsUrl, isAllowedCdpUrl, sanitizeUrlForDisplay } = require('../src/connection-policy');

const ROOT = path.resolve(__dirname, '..');
const OUT_DIR = path.resolve(process.env.EMS_OUT_DIR || path.join(ROOT, 'out'));
const DEFAULT_EMS_URL = process.env.EMS_URL || 'http://172.29.248.4:8000/ui';
const DEFAULT_CDP_URL = process.env.CDP_URL || 'http://127.0.0.1:9222';
const DEFAULT_REALTIME_CDP_PORT = Number(process.env.REALTIME_CDP_PORT || 9333);
const EDGE_PROFILE = process.env.EDGE_PROFILE || path.join(OUT_DIR, '.edge_profile');
const REALTIME_EDGE_PROFILE = process.env.REALTIME_EDGE_PROFILE || path.join(OUT_DIR, '.edge_profile_realtime');

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function logLine(log, text) {
  if (typeof log === 'function') log(text);
}

function cdpPort(cdpUrl) {
  try {
    return Number(new URL(cdpUrl).port) || 9222;
  } catch {
    return 9222;
  }
}

function cdpVersionUrl(cdpUrl) {
  const url = new URL(cdpUrl);
  url.pathname = '/json/version';
  url.search = '';
  url.hash = '';
  return url.toString();
}

function localCdpUrl(port) {
  return `http://127.0.0.1:${Number(port) || 9222}`;
}

function httpGetJson(url, timeoutMs = 1500) {
  return new Promise((resolve, reject) => {
    const req = http.get(url, { timeout: timeoutMs }, res => {
      let body = '';
      res.setEncoding('utf8');
      res.on('data', chunk => { body += chunk; });
      res.on('end', () => {
        if (res.statusCode < 200 || res.statusCode >= 300) {
          reject(new Error(`HTTP ${res.statusCode}`));
          return;
        }
        try {
          resolve(JSON.parse(body));
        } catch (err) {
          reject(err);
        }
      });
    });
    req.on('timeout', () => {
      req.destroy(new Error('CDP endpoint timeout'));
    });
    req.on('error', reject);
  });
}

async function waitForCdpEndpoint(cdpUrl, timeoutMs = 12000) {
  const started = Date.now();
  const versionUrl = cdpVersionUrl(cdpUrl);
  let lastError = null;
  while (Date.now() - started < timeoutMs) {
    try {
      await httpGetJson(versionUrl);
      return true;
    } catch (err) {
      lastError = err;
      await sleep(300);
    }
  }
  throw new Error(`CDP 端口未就绪: ${sanitizeUrlForDisplay(cdpUrl)} (${lastError ? lastError.message : 'timeout'})`);
}

function edgeCandidates() {
  const env = process.env;
  return [
    env.EDGE_PATH,
    path.join(env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)', 'Microsoft\\Edge\\Application\\msedge.exe'),
    path.join(env.ProgramFiles || 'C:\\Program Files', 'Microsoft\\Edge\\Application\\msedge.exe'),
    path.join(env.LocalAppData || '', 'Microsoft\\Edge\\Application\\msedge.exe'),
  ].filter(Boolean);
}

function findEdgeExecutable() {
  const override = process.env.EDGE_PATH;
  if (override) {
    const resolved = path.resolve(override);
    if (!path.isAbsolute(override) || path.basename(resolved).toLowerCase() !== 'msedge.exe' || !fs.existsSync(resolved)) {
      throw new Error('EDGE_PATH 必须是存在的 msedge.exe 绝对路径。');
    }
    return resolved;
  }
  const hit = edgeCandidates().slice(1).find(file => fs.existsSync(file));
  if (!hit) {
    throw new Error('未找到 Microsoft Edge。请安装 Edge，或设置 EDGE_PATH 指向 msedge.exe。');
  }
  return hit;
}

function launchEdgeCdp(cdpUrl, emsUrl, log) {
  fs.mkdirSync(EDGE_PROFILE, { recursive: true });
  const edge = findEdgeExecutable();
  const port = cdpPort(cdpUrl);
  const args = [
    `--remote-debugging-port=${port}`,
    '--remote-debugging-address=127.0.0.1',
    `--user-data-dir=${EDGE_PROFILE}`,
    '--no-first-run',
    '--disable-default-apps',
    emsUrl,
  ];
  logLine(log, `[CDP] 未发现 ${sanitizeUrlForDisplay(cdpUrl)}，正在启动 Edge 调试端口 ${port}`);
  const proc = spawn(edge, args, {
    cwd: ROOT,
    detached: true,
    stdio: 'ignore',
    windowsHide: false,
  });
  proc.unref();
  return proc.pid;
}

function normalizeBrowserMode(mode) {
  const raw = String(mode || process.env.REALTIME_BROWSER_MODE || 'persistent').trim().toLowerCase();
  if (raw === 'cdp' || raw === 'edge-cdp' || raw === 'connect-cdp') return 'cdp';
  return 'persistent';
}

function isEmsUrl(url, emsUrl) {
  return isAllowedEmsUrl(url, emsUrl);
}

async function isEmsReady(page) {
  try {
    return await page.evaluate(() => {
      if (document.querySelector('input[type="password"]')) return false;
      if (document.querySelector('.pi-svg-container')) return true;
      if (document.querySelector('.ivu-menu')) return true;
      if (document.querySelector('.pi-menu')) return true;
      if (document.querySelector('.pi-app')) return true;
      const text = document.body?.innerText || '';
      return /[1-6]号/.test(text) && text.includes('空调');
    });
  } catch {
    return false;
  }
}

async function waitForEmsReady(page, options = {}) {
  const timeoutMs = options.timeoutMs || 60000;
  const log = options.log;
  const prefix = options.prefix || '[BROWSER]';
  const started = Date.now();
  let notified = false;
  while (Date.now() - started < timeoutMs) {
    if (await isEmsReady(page)) return true;
    if (!notified) {
      notified = true;
      logLine(log, `${prefix} Edge 已打开，请确认 EMS 页面已登录，最多等待 60 秒`);
    }
    await sleep(1000);
  }
  throw new Error('EMS 页面未登录或未就绪；请在自动打开的 Edge 中完成登录后重新开始实时详情采集。');
}

async function getOrOpenEmsPageFromContext(context, emsUrl, log, prefix = '[BROWSER]', focusPage = false) {
  let pages = context.pages();
  let page = pages.find(p => isEmsUrl(p.url(), emsUrl));
  if (!page) page = pages[0] || await context.newPage();
  if (focusPage) {
    logLine(log, `${prefix} FOCUS 页面置前: ${sanitizeUrlForDisplay(page.url())}`);
    await page.bringToFront().catch(() => {});
  } else {
    logLine(log, `${prefix} REUSE_PAGE 后台复用页面（不抢占焦点）: ${sanitizeUrlForDisplay(page.url())}`);
  }
  if (!isEmsUrl(page.url(), emsUrl)) {
    logLine(log, `${prefix} GOTO 打开 EMS: ${sanitizeUrlForDisplay(emsUrl)}`);
    await page.goto(emsUrl, { waitUntil: 'domcontentloaded', timeout: 30000 });
  }
  await page.waitForLoadState('domcontentloaded', { timeout: 10000 }).catch(() => {});
  await waitForEmsReady(page, { log, prefix });
  return page;
}

async function getOrOpenEmsPage(browser, emsUrl, log, focusPage = false) {
  let context = browser.contexts()[0];
  if (!context) throw new Error('CDP 浏览器没有可用 context');
  return getOrOpenEmsPageFromContext(context, emsUrl, log, '[CDP]', focusPage);
}

async function connectCdp(cdpUrl) {
  await waitForCdpEndpoint(cdpUrl, 1500);
  return await chromium.connectOverCDP(cdpUrl, { timeout: 5000 });
}

async function launchPersistentEdge(emsUrl, log, options = {}) {
  fs.mkdirSync(REALTIME_EDGE_PROFILE, { recursive: true });
  const edge = findEdgeExecutable();
  const debugPort = Number(options.cdpPort || 0);
  const cdpUrl = debugPort > 0 ? localCdpUrl(debugPort) : '';
  // 首次启动（或显式请求聚焦）才把窗口置前；后台任务复用窗口时不抢占焦点
  const focusPage = !!options.focusPage;
  logLine(log, '[BROWSER] LAUNCH 自动启动 Edge（persistent context，推荐）');
  logLine(log, `[BROWSER] 用户数据目录: ${REALTIME_EDGE_PROFILE}`);
  if (cdpUrl) logLine(log, `[BROWSER] 会话内复用端口: ${cdpUrl}`);
  logLine(log, focusPage ? '[BROWSER] 窗口将置前等待登录' : '[BROWSER] 窗口后台运行，不抢占当前窗口');
  logLine(log, '[BROWSER] 首次使用如未登录 EMS，请在打开的窗口完成登录');
  let context = null;
  try {
    const args = [
      '--no-first-run',
      '--disable-default-apps',
    ];
    if (focusPage) args.push('--start-maximized');
    else args.push('--start-minimized');
    if (debugPort > 0) args.unshift(`--remote-debugging-port=${debugPort}`);
    context = await chromium.launchPersistentContext(REALTIME_EDGE_PROFILE, {
      executablePath: edge,
      headless: false,
      viewport: { width: 1920, height: 1080 },
      ignoreDefaultArgs: ['--no-sandbox'],
      args,
    });
    if (cdpUrl) await waitForCdpEndpoint(cdpUrl, 10000);
    const page = await getOrOpenEmsPageFromContext(context, emsUrl, log, '[BROWSER]', focusPage);
    return {
      browser: context.browser() || null,
      context,
      page,
      cdpUrl,
      cdpPort: debugPort || null,
      launched: true,
      mode: 'persistent',
    };
  } catch (err) {
    if (context) {
      await context.close().catch(() => {});
    }
    const msg = err && err.message ? err.message : String(err);
    if (/user data dir|profile|already in use|正在使用|占用|lock/i.test(msg)) {
      throw new Error(`自动启动 Edge 失败：实时采集用户数据目录正在被占用。请关闭上一次自动启动的 Edge 窗口后重试，或切换为“连接已有 Edge CDP（专家）”。原始错误：${msg}`);
    }
    throw err;
  }
}

async function ensureCdpBrowser(options = {}) {
  const cdpUrl = options.cdpUrl || DEFAULT_CDP_URL;
  const emsUrl = options.emsUrl || DEFAULT_EMS_URL;
  const log = options.log;
  const strict = !!options.strictCdp || process.env.REALTIME_CDP_STRICT === '1';
  // 首次连接/用户主动打开时才聚焦；后台采集中复用窗口不抢焦点
  const focusPage = !!options.focusPage;
  const allowRemoteCdp = !!options.allowRemoteCdp || process.env.REALTIME_ALLOW_REMOTE_CDP === '1';
  if (!isAllowedCdpUrl(cdpUrl, allowRemoteCdp)) {
    throw new Error(`拒绝不安全的 CDP 地址: ${sanitizeUrlForDisplay(cdpUrl)}；默认只允许本机回环地址。`);
  }
  let launched = false;
  let browser = null;

  try {
    browser = await connectCdp(cdpUrl);
    logLine(log, `[CDP] ATTACH 已连接已有 Edge CDP: ${sanitizeUrlForDisplay(cdpUrl)}`);
  } catch (firstErr) {
    if (strict) {
      throw new Error(`无法连接实时采集浏览器: ${sanitizeUrlForDisplay(cdpUrl)}。请确认采集任务打开的 Edge 窗口仍在运行。原始错误：${firstErr.message}`);
    }
    logLine(log, `[CDP] LAUNCH 未发现 ${sanitizeUrlForDisplay(cdpUrl)}，启动 Edge 调试端口`);
    launchEdgeCdp(cdpUrl, emsUrl, log);
    launched = true;
    await waitForCdpEndpoint(cdpUrl, 15000);
    try {
      browser = await chromium.connectOverCDP(cdpUrl, { timeout: 10000 });
    } catch (secondErr) {
      throw new Error(`无法连接 Edge CDP: ${sanitizeUrlForDisplay(cdpUrl)}; ${secondErr.message || firstErr.message}`);
    }
  }

  const page = await getOrOpenEmsPage(browser, emsUrl, log, focusPage);
  if (launched) logLine(log, '[CDP] Edge CDP 已就绪，继续实时详情采集');
  else logLine(log, `[CDP] 已连接已有 Edge CDP: ${sanitizeUrlForDisplay(cdpUrl)}`);
  return { browser, context: browser.contexts()[0] || null, page, launched, mode: 'cdp' };
}

async function ensureRealtimeBrowser(options = {}) {
  const emsUrl = options.emsUrl || DEFAULT_EMS_URL;
  const log = options.log;
  const mode = normalizeBrowserMode(options.mode || options.browserMode);
  if (mode === 'cdp') return ensureCdpBrowser(options);
  return launchPersistentEdge(emsUrl, log, options);
}

module.exports = {
  DEFAULT_CDP_URL,
  DEFAULT_EMS_URL,
  DEFAULT_REALTIME_CDP_PORT,
  REALTIME_EDGE_PROFILE,
  ensureRealtimeBrowser,
  waitForCdpEndpoint,
};
