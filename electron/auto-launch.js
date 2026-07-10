'use strict';

function isAutoLaunchEnabled(app) {
  return !!(app.getLoginItemSettings && app.getLoginItemSettings().openAtLogin);
}

function setAutoLaunch(app, enabled) {
  if (!app.setLoginItemSettings) return false;
  app.setLoginItemSettings({
    openAtLogin: !!enabled,
    path: process.execPath,
  });
  return true;
}

module.exports = {
  isAutoLaunchEnabled,
  setAutoLaunch,
};
