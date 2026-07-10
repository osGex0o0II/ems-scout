'use strict';

function createRealtimeRoutes(options) {
  const { json, realtimeSummary, realtimeCoverage } = options;

  return async function realtimeRoutes(ctx) {
    const { res, url, method, pathName } = ctx;

    if (method === 'GET' && pathName === '/api/realtime/summary') {
      json(res, 200, { ok: true, data: realtimeSummary() });
      return true;
    }

    if (method === 'GET' && pathName === '/api/realtime/coverage') {
      json(res, 200, { ok: true, data: realtimeCoverage(Object.fromEntries(url.searchParams.entries())) });
      return true;
    }

    return false;
  };
}

module.exports = { createRealtimeRoutes };
