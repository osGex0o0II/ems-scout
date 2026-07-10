'use strict';

function createReconcileRoutes(options) {
  const {
    json,
    fail,
    reconcileService,
  } = options;

  return async function reconcileRoutes(ctx) {
    const { res, url, method, pathName } = ctx;

    if (method === 'GET' && pathName === '/api/reconcile/diff') {
      try {
        json(res, 200, {
          ok: true,
          data: reconcileService.diff(Object.fromEntries(url.searchParams.entries())),
        });
      } catch (e) {
        fail(res, 500, e);
      }
      return true;
    }

    if (method === 'GET' && pathName === '/api/reconcile/audit') {
      try {
        json(res, 200, {
          ok: true,
          data: reconcileService.audit(Object.fromEntries(url.searchParams.entries())),
        });
      } catch (e) {
        fail(res, 500, e);
      }
      return true;
    }

    if (method === 'GET' && pathName === '/api/reconcile/replay') {
      try {
        const query = Object.fromEntries(url.searchParams.entries());
        json(res, 200, {
          ok: true,
          data: reconcileService.replay(query.run_id || query.runId || '', query.ruleVersion || query.rule_version, query),
        });
      } catch (e) {
        fail(res, 500, e);
      }
      return true;
    }

    return false;
  };
}

module.exports = { createReconcileRoutes };
