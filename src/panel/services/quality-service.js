'use strict';

const fs = require('fs');
const path = require('path');

function createQualityService(options) {
  const ROOT = options.root;
  const OUT_DIR = options.outDir;
  const DB_PATH = options.dbPath;
  const openReadonlyDb = options.openReadonlyDb;
  const resolveRunId = options.resolveRunId;

  function latestRealtimeQualityFile() {
    if (!fs.existsSync(OUT_DIR)) return '';
    return fs.readdirSync(OUT_DIR)
      .filter(name => /^realtime_quality_classified_\d{8}_\d{6}\.json$/i.test(name))
      .map(name => {
        const full = path.join(OUT_DIR, name);
        return { full, mtime: fs.statSync(full).mtimeMs };
      })
      .sort((a, b) => b.mtime - a.mtime)[0]?.full || '';
  }

  function loadRealtimeQualityReport() {
    const file = latestRealtimeQualityFile();
    if (!file) return null;
    try {
      const data = JSON.parse(fs.readFileSync(file, 'utf8'));
      const summary = {
        total_cards: data.totalRows || 0,
        collection_errors: data.collectionErrors && data.collectionErrors.count || 0,
        device_anomaly_rows: data.deviceAnomalies && data.deviceAnomalies.rowCount || 0,
        device_anomaly_events: data.deviceAnomalies && data.deviceAnomalies.eventCount || 0,
        invalid_realtime_tags: data.deviceAnomalies && data.deviceAnomalies.byCategory
          ? Number(data.deviceAnomalies.byCategory.invalidRealtimeTags || 0)
          : 0,
        invalid_enum: data.deviceAnomalies && data.deviceAnomalies.byCategory
          ? Number(data.deviceAnomalies.byCategory.invalidEnum || 0)
          : 0,
        out_of_range: data.deviceAnomalies && data.deviceAnomalies.byCategory
          ? Number(data.deviceAnomalies.byCategory.outOfRange || 0)
          : 0,
        invalid_lock: data.deviceAnomalies && data.deviceAnomalies.byCategory
          ? Number(data.deviceAnomalies.byCategory.invalidLock || 0)
          : 0,
      };
      summary.issue_count = Number(summary.collection_errors || 0) + Number(summary.device_anomaly_rows || 0);
      const issues = [];
      const collectionCategories = data.collectionErrors && data.collectionErrors.byCategory || {};
      for (const [category, count] of Object.entries(collectionCategories)) {
        issues.push({
          code: `realtime_collection_${category}`,
          severity: 'P1',
          count,
          message: `实时详情采集质量问题：${category}`,
        });
      }
      const anomalyCategories = data.deviceAnomalies && data.deviceAnomalies.byCategory || {};
      for (const [category, count] of Object.entries(anomalyCategories)) {
        issues.push({
          code: `realtime_device_${category}`,
          severity: category === 'invalidLock' ? 'INFO' : 'P2',
          count,
          message: `实时详情字段需复核：${category}`,
        });
      }
      const samples = {
        realtime_collection_errors: (data.collectionErrors && data.collectionErrors.rows || []).slice(0, 80),
        realtime_device_anomalies: (data.deviceAnomalies && data.deviceAnomalies.rows || []).slice(0, 120),
      };
      return {
        ...data,
        kind: 'realtime',
        source_file: path.relative(ROOT, file),
        generated_at: data.createdAt || fs.statSync(file).mtime.toISOString(),
        summary,
        issues,
        samples,
      };
    } catch {
      return null;
    }
  }

  function loadQualityReport(query = {}) {
    if (!query.run_id && !query.runId) {
      const realtime = loadRealtimeQualityReport();
      if (realtime) return realtime;
    }

    const db = fs.existsSync(DB_PATH) ? openReadonlyDb() : null;
    let runId = null;
    try {
      if (db) {
        runId = resolveRunId(db, query.run_id || query.runId);
        if (!runId && query.current !== '1') {
          const row = db.prepare(`
            SELECT id
            FROM collection_runs
            WHERE IFNULL(is_anomaly, 0) = 0
            ORDER BY datetime(completed_at) DESC, id DESC
            LIMIT 1
          `).get();
          runId = row ? row.id : null;
        }
      }
    } catch {
      runId = null;
    } finally {
      if (db) db.close();
    }

    const p = runId ? path.join(OUT_DIR, `quality_report_run${runId}.json`) : path.join(OUT_DIR, 'quality_report.json');
    if (fs.existsSync(p)) {
      try { return JSON.parse(fs.readFileSync(p, 'utf8')); }
      catch {}
    }

    if (runId && fs.existsSync(DB_PATH)) {
      const db2 = openReadonlyDb();
      try {
        const row = db2.prepare('SELECT id, run_key, completed_at, quality_summary FROM collection_runs WHERE id = ?').get(runId);
        if (row && row.quality_summary && row.quality_summary !== '{}') {
          const parsed = JSON.parse(row.quality_summary);
          return {
            ...parsed,
            run_id: runId,
            run: { id: row.id, run_key: row.run_key, completed_at: row.completed_at },
            partial: true,
          };
        }
      } catch {
        return null;
      } finally {
        db2.close();
      }
    }
    return null;
  }

  return {
    latestRealtimeQualityFile,
    loadRealtimeQualityReport,
    loadQualityReport,
  };
}

module.exports = { createQualityService };
