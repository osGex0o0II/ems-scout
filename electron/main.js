'use strict';

const { app, dialog } = require('electron');
const logger = require('./logger');
const { DEFAULT_PORT, ensureRunning, stopOwnedServer } = require('./server-manager');
const { createMainWindow } = require('./window');
const { createAppTray } = require('./tray');

const PORT = Number(process.env.EMS_PANEL_PORT || DEFAULT_PORT);

if (process.env.EMS_ENABLE_LEGACY_PANEL !== '1') {
  console.error('legacy-electron-desktop is disabled by default. Set EMS_ENABLE_LEGACY_PANEL=1 to run intentionally.');
  app.whenReady().then(() => app.exit(2));
} else {
let mainWindow = null;
let appTray = null;
let quitRequested = false;

function setQuitRequested(value) {
  quitRequested = !!value;
}

function stopServer() {
  stopOwnedServer(logger);
}

const singleInstance = app.requestSingleInstanceLock();
if (!singleInstance) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.show();
      mainWindow.focus();
    }
  });

  app.on('before-quit', () => {
    quitRequested = true;
  });

  app.on('window-all-closed', () => {});

  async function bootstrap() {
    logger.info('app starting', { version: app.getVersion(), port: PORT });
    const service = await ensureRunning({ port: PORT, logger, timeoutMs: 45000 });
    logger.info('panel service ready', service);

    mainWindow = createMainWindow({
      url: service.url,
      logger,
      shouldQuit: () => quitRequested,
    });

    appTray = createAppTray({
      app,
      mainWindow: () => mainWindow,
      logger,
      port: PORT,
      setQuitRequested,
      stopServer,
    });
  }

  app.whenReady().then(bootstrap).catch(err => {
    logger.error('bootstrap failed', err);
    dialog.showErrorBox('EMS Scout Legacy 启动失败', err.message || String(err));
    quitRequested = true;
    app.quit();
  });

  app.on('activate', () => {
    if (mainWindow) {
      mainWindow.show();
      mainWindow.focus();
    }
  });

  app.on('will-quit', () => {
    quitRequested = true;
    stopServer();
    appTray = null;
  });
}
}
