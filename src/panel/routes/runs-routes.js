'use strict';

function createRunsRoutes(options) {
  const {
    json,
    fail,
    readBody,
    openDb,
    decorateRuns,
    listRuns,
    latestCollectionMeta,
    realtimeSummary,
    setRunAnomaly,
    restoreCurrentFromRun,
    deleteRun,
  } = options;

  return async function runsRoutes(ctx) {
    const { req, res, url, method, pathName } = ctx;

    if (method === 'GET' && pathName === '/api/runs') {
      const db = openDb();
      try {
        const runs = decorateRuns(listRuns(db, { limit: Number(url.searchParams.get('limit')) || 100 }));
        json(res, 200, {
          ok: true,
          data: {
            runs,
            latest_collection: latestCollectionMeta(),
            realtime_summary: realtimeSummary(),
          },
        });
      } finally {
        db.close();
      }
      return true;
    }

    if (method === 'POST' && pathName.match(/^\/api\/runs\/\d+\/anomaly$/)) {
      const id = Number(pathName.split('/')[3]);
      const body = await readBody(req);
      const db = openDb();
      try {
        const row = setRunAnomaly(db, id, body.is_anomaly !== false && body.is_anomaly !== 0, body.note || '');
        json(res, 200, { ok: true, data: row });
      } catch (e) {
        fail(res, 400, e);
      } finally {
        db.close();
      }
      return true;
    }

    if (method === 'POST' && pathName.match(/^\/api\/runs\/\d+\/restore$/)) {
      const id = Number(pathName.split('/')[3]);
      const db = openDb();
      try {
        const row = restoreCurrentFromRun(db, id);
        json(res, 200, { ok: true, data: row });
      } catch (e) {
        fail(res, 400, e);
      } finally {
        db.close();
      }
      return true;
    }

    if (method === 'DELETE' && pathName.match(/^\/api\/runs\/\d+$/)) {
      const id = Number(pathName.split('/')[3]);
      const body = await readBody(req);
      if (String(body.confirm || '').trim() !== '确认删除') {
        fail(res, 400, '请输入“确认删除”后再删除批次');
        return true;
      }
      const db = openDb();
      try {
        const row = deleteRun(db, id);
        json(res, 200, { ok: true, data: { deleted: row } });
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

module.exports = { createRunsRoutes };
