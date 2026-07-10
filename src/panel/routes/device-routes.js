'use strict';

function createDeviceRoutes(options) {
  const { json, readBody, saveNote, saveTag, deleteTag } = options;

  return async function deviceRoutes(ctx) {
    const { req, res, method, pathName } = ctx;

    if (method === 'POST' && pathName === '/api/device/note') {
      json(res, 200, saveNote(await readBody(req)));
      return true;
    }

    if (method === 'POST' && pathName === '/api/device/tag') {
      json(res, 200, saveTag(await readBody(req)));
      return true;
    }

    if (method === 'DELETE' && pathName === '/api/device/tag') {
      json(res, 200, deleteTag(await readBody(req)));
      return true;
    }

    return false;
  };
}

module.exports = { createDeviceRoutes };
