'use strict';

function createTasksRoutes(options) {
  const {
    json,
    readBody,
    getCurrentTask,
    getTaskHistory,
    publicTask,
    startTask,
    stopTask,
    buildPreflight,
  } = options;

  return async function tasksRoutes(ctx) {
    const { req, res, method, pathName } = ctx;

    if (method === 'GET' && pathName === '/api/tasks/current') {
      json(res, 200, {
        ok: true,
        data: {
          current: publicTask(getCurrentTask()),
          history: getTaskHistory().map(publicTask),
        },
      });
      return true;
    }

    if (method === 'GET' && pathName === '/api/tasks/preflight') {
      json(res, 200, { ok: true, data: buildPreflight() });
      return true;
    }

    if (method === 'POST' && pathName === '/api/tasks') {
      json(res, 200, { ok: true, data: startTask(await readBody(req)) });
      return true;
    }

    if (method === 'POST' && pathName === '/api/tasks/stop') {
      json(res, 200, { ok: true, data: stopTask() });
      return true;
    }

    return false;
  };
}

module.exports = { createTasksRoutes };
