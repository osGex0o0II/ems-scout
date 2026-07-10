'use strict';

function createSummaryRoutes(deps) {
  const json = deps.json;
  const loadSummary = deps.loadSummary;

  return async function handleSummaryRoutes(ctx) {
    if (ctx.method === 'GET' && ctx.pathName === '/api/summary') {
      json(ctx.res, 200, { ok: true, data: loadSummary(Object.fromEntries(ctx.url.searchParams.entries())) });
      return true;
    }
    return false;
  };
}

module.exports = { createSummaryRoutes };
