'use strict';

function createHealthRoutes(deps) {
  const json = deps.json;
  const root = deps.root;
  const dbPath = deps.dbPath;
  const databaseExists = deps.databaseExists;

  return async function handleHealthRoutes(ctx) {
    if (ctx.method === 'GET' && ctx.pathName === '/api/health') {
      json(ctx.res, 200, { ok: true, root, db_path: dbPath, db_exists: databaseExists() });
      return true;
    }
    return false;
  };
}

module.exports = { createHealthRoutes };
