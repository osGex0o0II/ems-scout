'use strict';

function createAboutRoutes(options) {
  const { json, loadAboutInfo } = options;

  return async function aboutRoutes(ctx) {
    const { res, method, pathName } = ctx;
    if (method === 'GET' && pathName === '/api/about') {
      json(res, 200, { ok: true, data: loadAboutInfo() });
      return true;
    }
    return false;
  };
}

module.exports = { createAboutRoutes };
