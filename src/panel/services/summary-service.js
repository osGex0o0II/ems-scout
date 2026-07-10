'use strict';

function createSummaryService(options) {
  const {
    BLDG_ORDER,
    BLDG_META,
    openReadonlyDb,
    resolveRunId,
    loadCards,
    loadQualityReport,
    realtimeCoverage,
    realtimeFieldValue,
    commStateValue,
    isManagedRealtimeRow,
    isRealtimeDbUnmatchedRow,
  } = options;

  function loadSummary(query = {}) {
    const db = openReadonlyDb();
    try {
      const runId = resolveRunId(db, query.run_id || query.runId);
      const rows = loadCards({
        building: query.building || '',
        run_id: runId || '',
        include_realtime: '1',
        limit: 20000,
        _maxLimit: 20000,
      }).rows || [];
      const updated = {};
      try {
        const rowsUpdated = runId
          ? db.prepare('SELECT building, updated_at FROM run_buildings WHERE run_id = ?').all(runId)
          : db.prepare('SELECT building, updated_at FROM buildings').all();
        for (const r of rowsUpdated) updated[r.building] = r.updated_at;
      } catch {}

      const quality = loadQualityReport({ run_id: runId || '' });
      const summary = {
        run_id: runId,
        total: rows.length,
        on: 0,
        off: 0,
        offline: 0,
        comm_unknown: 0,
        unknown: 0,
        public: 0,
        public_on: 0,
        non_public: 0,
        non_public_on: 0,
        buildings: [],
        quality,
      };

      const dbCoverage = realtimeCoverage({ run_id: runId || '' });
      summary.db_total = dbCoverage.db_rows;
      summary.detail_total = rows.length;
      summary.matched = rows.filter(isManagedRealtimeRow).length;
      summary.db_missing_realtime = 0;
      summary.realtime_unmatched = rows.filter(isRealtimeDbUnmatchedRow).length;
      summary.delta = rows.length - Number(dbCoverage.db_rows || 0);

      const byBuilding = Object.fromEntries(BLDG_ORDER.map(b => [b, {
        building: b,
        name: BLDG_META[b] ? BLDG_META[b].name : b,
        total: 0,
        on: 0,
        off: 0,
        offline: 0,
        comm_unknown: 0,
        unknown: 0,
        baseline: BLDG_META[b] ? BLDG_META[b].baselineCards : 0,
        updated_at: updated[b] || null,
      }]));

      for (const r of rows) {
        const power = realtimeFieldValue(r, '当前开关机状态') || '';
        const communication = commStateValue(r);
        const isOn = power === '开机' || r.switch === 'ON';
        const isOff = power === '关机' || r.switch === 'OFF';
        const isOffline = communication === '离线';
        const isCommUnknown = communication === '未知';
        const pub = r.area_type === '公区';
        if (isOn) summary.on++;
        else if (isOff) summary.off++;
        else summary.unknown++;
        if (isOffline) summary.offline++;
        if (isCommUnknown) summary.comm_unknown++;
        if (pub) {
          summary.public++;
          if (isOn) summary.public_on++;
        } else {
          summary.non_public++;
          if (isOn) summary.non_public_on++;
        }
        const b = byBuilding[r.building] || (byBuilding[r.building] = {
          building: r.building,
          name: r.building,
          total: 0,
          on: 0,
          off: 0,
          offline: 0,
          comm_unknown: 0,
          unknown: 0,
          baseline: 0,
          updated_at: updated[r.building] || null,
        });
        b.total++;
        if (isOn) b.on++;
        else if (isOff) b.off++;
        else b.unknown++;
        if (isOffline) b.offline++;
        if (isCommUnknown) b.comm_unknown++;
      }

      const coverageByBuilding = new Map((dbCoverage.by_building || []).map(b => [b.building, b]));
      summary.buildings = Object.values(byBuilding).map(b => {
        const c = coverageByBuilding.get(b.building) || {};
        return {
          ...b,
          db_total: Number(c.db_rows || 0),
          matched: b.total,
          detail_delta: Number(c.delta || 0),
          delta: b.total - Number(b.baseline || 0),
        };
      });
      if (summary.quality && summary.quality.summary) {
        const qualityTotal = Number(summary.quality.summary.total_cards || summary.quality.totalRows || 0);
        summary.quality.stale = qualityTotal !== Number(summary.total || 0);
        summary.quality.expected_total = summary.total;
        summary.quality.detail_total = summary.total;
        summary.quality.report_total = qualityTotal;
      }
      return summary;
    } finally {
      db.close();
    }
  }

  return { loadSummary };
}

module.exports = { createSummaryService };
