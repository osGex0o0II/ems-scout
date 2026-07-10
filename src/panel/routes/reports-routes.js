'use strict';

function createReportsRoutes(options) {
  const {
    json,
    readLatestLog,
  } = options;

  return async function reportsRoutes(ctx) {
    const { res, method, pathName } = ctx;

    if (method === 'GET' && pathName === '/api/logs') {
      json(res, 200, { ok: true, data: readLatestLog() });
      return true;
    }

    return false;
  };
}

module.exports = { createReportsRoutes };
