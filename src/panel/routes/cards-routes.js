'use strict';

function createCardsRoutes(options) {
  const { json, cardsService } = options;

  return async function cardsRoutes(ctx) {
    const { res, url, method, pathName } = ctx;
    if (method === 'GET' && pathName === '/api/cards') {
      json(res, 200, { ok: true, data: cardsService.loadCards(Object.fromEntries(url.searchParams.entries())) });
      return true;
    }
    if (method === 'GET' && pathName === '/api/filter-options') {
      json(res, 200, { ok: true, data: cardsService.loadFilterOptions(Object.fromEntries(url.searchParams.entries())) });
      return true;
    }
    if (method === 'POST' && pathName === '/api/cards/export') {
      json(res, 410, {
        ok: false,
        error: '旧 Web 面板 Excel 导出已移除。当前唯一面向用户的导出入口是原生应用“数据管理 -> 导出当前筛选 Excel”。',
      });
      return true;
    }
    return false;
  };
}

module.exports = { createCardsRoutes };
