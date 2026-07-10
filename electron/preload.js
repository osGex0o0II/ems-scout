'use strict';

const { contextBridge } = require('electron');

contextBridge.exposeInMainWorld('emsDesktop', {
  platform: process.platform,
  product: 'EMS Legacy Web Panel',
});
