'use strict';

const path = require('path');
const { Menu, Tray, nativeImage, dialog, Notification } = require('electron');
const { requestJson } = require('./server-manager');
const { showMainWindow } = require('./window');
const { isAutoLaunchEnabled, setAutoLaunch } = require('./auto-launch');

function trayIcon() {
  const iconPath = path.join(__dirname, 'assets', 'tray.svg');
  const icon = nativeImage.createFromPath(iconPath);
  return icon.isEmpty() ? nativeImage.createEmpty() : icon.resize({ width: 16, height: 16 });
}

function notify(title, body) {
  if (Notification.isSupported()) {
    new Notification({ title, body }).show();
  }
}

async function runAction(label, logger, action) {
  try {
    const result = await action();
    logger.info(`tray action completed: ${label}`, result || {});
    return result;
  } catch (err) {
    logger.error(`tray action failed: ${label}`, err);
    dialog.showErrorBox('EMS Scout Legacy', `${label}失败：${err.message || err}`);
    return null;
  }
}

function createAppTray(options) {
  const {
    app,
    mainWindow,
    logger,
    port,
    setQuitRequested,
    stopServer,
  } = options;

  const tray = new Tray(trayIcon());
  tray.setToolTip('EMS System');

  const openPanel = () => showMainWindow(mainWindow());
  const startCollect = () => runAction('开始采集', logger, async () => {
    const task = await requestJson('POST', '/api/tasks', {
      kind: 'realtimeDetails',
      captureMode: 'autoLaunch',
      logFile: true,
      label: '桌面一键采集',
    }, { port });
    notify('EMS Scout Legacy', '采集任务已启动');
    openPanel();
    return task;
  });
  const stopCollect = () => runAction('停止采集', logger, async () => {
    const task = await requestJson('POST', '/api/tasks/stop', {}, { port });
    notify('EMS Scout Legacy', '已发送停止采集命令');
    return task;
  });
  const runReconcile = () => runAction('运行对账', logger, async () => {
    const diff = await requestJson('GET', '/api/reconcile/diff', undefined, { port });
    const summary = diff && diff.summary || {};
    const total = Number(summary.total_diff || summary.diff_count || summary.total || 0);
    notify('EMS Scout Legacy', `对账完成，差异 ${total} 项`);
    openPanel();
    return diff;
  });
  function rebuildMenu() {
    const autoLaunchEnabled = isAutoLaunchEnabled(app);
    const menu = Menu.buildFromTemplate([
    { label: 'EMS System', enabled: false },
    { type: 'separator' },
    { label: '打开面板', click: openPanel },
    { label: '开始采集', click: startCollect },
    { label: '停止采集', click: stopCollect },
    { label: '运行对账', click: runReconcile },
    {
      label: '开机自启',
      type: 'checkbox',
      checked: autoLaunchEnabled,
      click: item => {
        const enabled = !!item.checked;
        const ok = setAutoLaunch(app, enabled);
        logger.info('set auto launch', { enabled, ok });
        rebuildMenu();
      },
    },
    { type: 'separator' },
    {
      label: '退出',
      click: () => {
        setQuitRequested(true);
        stopServer();
        app.quit();
      },
    },
    ]);
    tray.setContextMenu(menu);
  }

  rebuildMenu();
  tray.on('click', openPanel);

  return tray;
}

module.exports = {
  createAppTray,
};
