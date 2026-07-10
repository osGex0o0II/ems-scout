#!/usr/bin/env node
'use strict';

const path = require('path');
const Database = require('better-sqlite3');
const { BLDG_ORDER } = require('../src/rules');
const { createRealtimeService } = require('../src/panel/services/realtime-service');
const { createReconcileService } = require('../src/panel/services/reconcile-service');
const { RULE_VERSION } = require('../src/panel/services/reconcile-explain.service');

const ROOT = path.join(__dirname, '..');
const OUT_DIR = path.join(ROOT, 'out');
const DB_PATH = process.env.EMS_DB_PATH || path.join(OUT_DIR, 'ac.db');

function argValue(name) {
  const prefix = `--${name}=`;
  const hit = process.argv.find(a => a.startsWith(prefix));
  return hit ? hit.slice(prefix.length) : '';
}

function openReadonlyDb() {
  return new Database(DB_PATH, { readonly: true });
}

function service() {
  return createReconcileService({
    root: ROOT,
    outDir: OUT_DIR,
    openReadonlyDb,
    realtimeService: createRealtimeService({ root: ROOT, outDir: OUT_DIR, buildings: BLDG_ORDER }),
  });
}

function main() {
  const mode = argValue('mode') || (process.argv.includes('--audit') ? 'audit' : process.argv.includes('--replay') ? 'replay' : 'diff');
  if (mode === 'excel' || process.argv.includes('--excel')) {
    throw new Error('Reconciliation Excel export is no longer supported. Use Data Management filtered XLSX export instead.');
  }
  const runId = argValue('run-id') || argValue('run_id') || '';
  const ruleVersion = argValue('rule-version') || argValue('ruleVersion') || RULE_VERSION;
  const building = argValue('building') || '';
  const svc = service();
  const query = { run_id: runId, building };
  let output;
  if (mode === 'replay') output = svc.replay(runId, ruleVersion, query);
  else if (mode === 'audit') output = svc.audit(query);
  else output = svc.diff(query);
  console.log(JSON.stringify(output, null, 2));
}

if (require.main === module) main();
