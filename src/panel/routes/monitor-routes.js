'use strict';

function createMonitorRoutes(options) {
  const {
    json,
    readBody,
    openDb,
    ensureMonitorSchema,
    loadMonitors,
    saveMonitor,
    deleteMonitor,
    computeMonitorStatuses,
    compareMonitorStatuses,
    refreshMonitorSnapshots,
    loadMonitorEvents,
  } = options;

  return async function monitorRoutes(ctx) {
    const { req, res, url, method, pathName } = ctx;

    if (method === 'GET' && pathName === '/api/monitor/floors') {
      const db = openDb();
      try {
        ensureMonitorSchema(db);
        json(res, 200, {
          ok: true,
          data: {
            monitors: loadMonitors(db, { includeDisabled: true }),
            statuses: computeMonitorStatuses(db, { includeDisabled: true, run_id: url.searchParams.get('run_id') || '' }),
            events: loadMonitorEvents(db, 50, { run_id: url.searchParams.get('run_id') || '' }),
            changes: compareMonitorStatuses(db, { run_id: url.searchParams.get('run_id') || '' }),
          },
        });
      } finally {
        db.close();
      }
      return true;
    }

    if (method === 'POST' && pathName === '/api/monitor/floors') {
      const body = await readBody(req);
      const db = openDb();
      try {
        json(res, 200, { ok: true, data: saveMonitor(db, body) });
      } finally {
        db.close();
      }
      return true;
    }

    const monitorDelete = pathName.match(/^\/api\/monitor\/floors\/(\d+)$/);
    if (method === 'DELETE' && monitorDelete) {
      const db = openDb();
      try {
        json(res, 200, { ok: true, data: { deleted: deleteMonitor(db, Number(monitorDelete[1])) } });
      } finally {
        db.close();
      }
      return true;
    }

    if (method === 'POST' && pathName === '/api/monitor/refresh') {
      const body = await readBody(req);
      const db = openDb();
      try {
        json(res, 200, { ok: true, data: refreshMonitorSnapshots(db, { run_id: body.run_id || body.runId || '' }) });
      } finally {
        db.close();
      }
      return true;
    }

    if (method === 'GET' && pathName === '/api/monitor/events') {
      const db = openDb();
      try {
        json(res, 200, { ok: true, data: loadMonitorEvents(db, Number(url.searchParams.get('limit')) || 100, { run_id: url.searchParams.get('run_id') || '' }) });
      } finally {
        db.close();
      }
      return true;
    }

    if (method === 'GET' && pathName === '/api/monitor/changes') {
      const db = openDb();
      try {
        json(res, 200, {
          ok: true,
          data: compareMonitorStatuses(db, {
            run_id: url.searchParams.get('run_id') || '',
            base_run_id: url.searchParams.get('base_run_id') || '',
          }),
        });
      } finally {
        db.close();
      }
      return true;
    }

    return false;
  };
}

module.exports = { createMonitorRoutes };
