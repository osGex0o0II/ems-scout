'use strict';

function createRealtimeMatchRoutes(options) {
  const { json, readBody, saveRealtimeMatchOverride } = options;

  return async function realtimeMatchRoutes(ctx) {
    const { req, res, method, pathName } = ctx;
    if (method === 'POST' && pathName === '/api/realtime-match/override') {
      json(res, 200, saveRealtimeMatchOverride(await readBody(req)));
      return true;
    }
    return false;
  };
}

module.exports = { createRealtimeMatchRoutes };
