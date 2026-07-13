'use strict';

const { BrowserWindow, shell } = require('electron');
const path = require('path');
const { isExternalBrowserUrl, isSamePanelOrigin } = require('./navigation-policy');

function createMainWindow(options) {
  const url = options.url;
  const logger = options.logger || console;
  const win = new BrowserWindow({
    width: 1360,
    height: 860,
    minWidth: 1120,
    minHeight: 720,
    title: 'EMS Scout Legacy',
    autoHideMenuBar: true,
    show: false,
    backgroundColor: '#f6f7f9',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  win.removeMenu();

  win.once('ready-to-show', () => {
    win.show();
  });

  win.on('close', event => {
    if (options.shouldQuit && options.shouldQuit()) return;
    event.preventDefault();
    win.hide();
  });

  win.webContents.setWindowOpenHandler(({ url: nextUrl }) => {
    if (isExternalBrowserUrl(nextUrl)) shell.openExternal(nextUrl);
    return { action: 'deny' };
  });

  win.webContents.on('will-navigate', (event, nextUrl) => {
    if (isSamePanelOrigin(nextUrl, url)) return;
    event.preventDefault();
    if (isExternalBrowserUrl(nextUrl)) shell.openExternal(nextUrl);
  });

  win.webContents.on('did-fail-load', (_event, code, desc) => {
    logger.error('window failed to load', { code, desc, url });
  });

  win.loadURL(url).catch(err => {
    logger.error('loadURL failed', err);
  });

  return win;
}

function showMainWindow(win) {
  if (!win) return;
  if (win.isMinimized()) win.restore();
  win.show();
  win.focus();
}

module.exports = {
  createMainWindow,
  showMainWindow,
};
