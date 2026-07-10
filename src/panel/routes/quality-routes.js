'use strict';

function createQualityRoutes(deps) {
  const json = deps.json;
  const loadQualityReport = deps.loadQualityReport;

  return async function handleQualityRoutes(ctx) {
    if (ctx.method === 'GET' && ctx.pathName === '/api/quality') {
      json(ctx.res, 200, { ok: true, data: loadQualityReport(Object.fromEntries(ctx.url.searchParams.entries())) });
      return true;
    }
    return false;
  };
}

module.exports = { createQualityRoutes };
