'use strict';

function createAreasRoutes(options) {
  const {
    json,
    fail,
    readBody,
    openDb,
    loadAreaOptions,
    loadMonitorGroups,
    saveMonitorGroup,
    deleteMonitorGroup,
    loadMonitorGroupItems,
    saveMonitorGroupItem,
    deleteMonitorGroupItem,
    computeMonitorGroupStats,
  } = options;

  return async function areasRoutes(ctx) {
    const { req, res, url, method, pathName } = ctx;

    if (method === 'GET' && pathName === '/api/area-options') {
      json(res, 200, { ok: true, data: loadAreaOptions(Object.fromEntries(url.searchParams.entries())) });
      return true;
    }

    if (method === 'GET' && (pathName === '/api/areas' || pathName === '/api/monitor/groups')) {
      const db = openDb();
      try {
        json(res, 200, {
          ok: true,
          data: {
            groups: loadMonitorGroups(db, { includeDisabled: true }),
            items: loadMonitorGroupItems(db, url.searchParams.get('group_id') || null),
            stats: computeMonitorGroupStats(db, { run_id: url.searchParams.get('run_id') || '' }),
          },
        });
      } finally {
        db.close();
      }
      return true;
    }

    if (method === 'POST' && (pathName === '/api/areas' || pathName === '/api/monitor/groups')) {
      const db = openDb();
      try {
        json(res, 200, { ok: true, data: saveMonitorGroup(db, await readBody(req)) });
      } finally {
        db.close();
      }
      return true;
    }

    const groupDelete = pathName.match(/^\/api\/(?:areas|monitor\/groups)\/(\d+)$/);
    if (method === 'DELETE' && groupDelete) {
      const db = openDb();
      try {
        json(res, 200, { ok: true, data: { deleted: deleteMonitorGroup(db, Number(groupDelete[1])) } });
      } catch (e) {
        fail(res, 400, e);
      } finally {
        db.close();
      }
      return true;
    }

    if (method === 'POST' && (pathName === '/api/area-items' || pathName === '/api/monitor/group-items')) {
      const db = openDb();
      try {
        json(res, 200, { ok: true, data: saveMonitorGroupItem(db, await readBody(req)) });
      } catch (e) {
        fail(res, 400, e);
      } finally {
        db.close();
      }
      return true;
    }

    const groupItemDelete = pathName.match(/^\/api\/(?:area-items|monitor\/group-items)\/(\d+)$/);
    if (method === 'DELETE' && groupItemDelete) {
      const db = openDb();
      try {
        json(res, 200, { ok: true, data: { deleted: deleteMonitorGroupItem(db, Number(groupItemDelete[1])) } });
      } catch (e) {
        fail(res, 400, e);
      } finally {
        db.close();
      }
      return true;
    }

    return false;
  };
}

module.exports = { createAreasRoutes };
