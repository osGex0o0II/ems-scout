'use strict';

function detailRow(row, metadata = {}) {
  return {
    ...row,
    issue_code: metadata.code || row.issue_code || '',
    severity: metadata.severity || row.severity || 'P2',
    message: metadata.message || row.message || '',
    collector_decision: row.collector_decision || row.collectorDecision || '需复核',
    attribution: row.attribution || '采集判断',
    resolution_state: row.resolution_state || row.resolutionState || '需复核',
  };
}

function buildQualityDetails(buckets = [], realtime = {}) {
  const details = {};
  for (const bucket of buckets) {
    const rows = Array.isArray(bucket.rows) ? bucket.rows : [];
    details[bucket.code] = rows.map(row => detailRow(row, bucket));
  }

  const collectionErrors = Array.isArray(realtime.collectionErrors) ? realtime.collectionErrors : [];
  const deviceAnomalies = Array.isArray(realtime.deviceAnomalies) ? realtime.deviceAnomalies : [];
  details.realtime_collection_errors = collectionErrors.map(row => detailRow(row, {
    code: `realtime_${row.category || 'collection_error'}`,
    severity: 'P2',
    message: row.detail?.message || row.error || row.category || '实时采集问题',
  }));
  details.realtime_device_anomalies = deviceAnomalies.map(row => detailRow({
    ...row,
    device_name: row.device_name || row.name || '',
    issues: Array.isArray(row.issues) ? row.issues : [],
  }, {
    code: 'realtime_device_anomaly',
    severity: 'P2',
    message: Array.isArray(row.issues) ? row.issues.join('；') : '实时设备异常',
  }));
  return details;
}

module.exports = { buildQualityDetails };
