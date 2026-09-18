'use strict';

function readBatchIdentity(env = process.env) {
  const rawRunId = String(env.EMS_RUN_ID || '').trim();
  const runId = Number(rawRunId);
  return {
    runId: Number.isInteger(runId) && runId > 0 ? runId : null,
    batchUid: String(env.EMS_BATCH_UID || '').trim() || null,
    runKey: String(env.EMS_RUN_KEY || '').trim() || null,
  };
}

function withBatchIdentity(value, identity = readBatchIdentity()) {
  return {
    ...value,
    runId: identity.runId,
    batchUid: identity.batchUid,
    runKey: identity.runKey,
  };
}

module.exports = { readBatchIdentity, withBatchIdentity };
