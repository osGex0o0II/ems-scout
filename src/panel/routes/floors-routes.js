'use strict';

function createFloorsRoutes(options) {
  const {
    json,
    readBody,
    openDb,
    loadFloorCatalog,
    loadAvailableFloors,
    saveFloorCatalog,
  } = options;

  return async function floorsRoutes(ctx) {
    const { req, res, url, method, pathName } = ctx;

    if (method === 'GET' && pathName === '/api/floors') {
      const db = openDb();
      try {
        json(res, 200, {
          ok: true,
          data: {
            catalog: loadFloorCatalog(db, {
              building: url.searchParams.get('building') || '',
              includeDisabled: url.searchParams.get('includeDisabled') === '1',
            }),
            discovered: loadAvailableFloors(db, url.searchParams.get('building') || '', {
              run_id: url.searchParams.get('run_id') || '',
            }),
          },
        });
      } finally {
        db.close();
      }
      return true;
    }

    if (method === 'POST' && pathName === '/api/floors') {
      const body = await readBody(req);
      const db = openDb();
      try {
        json(res, 200, { ok: true, data: saveFloorCatalog(db, body) });
      } finally {
        db.close();
      }
      return true;
    }

    return false;
  };
}

module.exports = { createFloorsRoutes };
