#!/usr/bin/env node
'use strict';

const {
  openDb,
  ensureMonitorSchema,
  loadMonitors,
  saveMonitor,
  deleteMonitor,
  computeMonitorStatuses,
  refreshMonitorSnapshots,
  loadMonitorEvents,
} = require('../src/panel/monitor');

function usage() {
  console.log(`
Usage:
  node scripts/monitor-floors.js --list
  node scripts/monitor-floors.js --add --building=3号 --floor=18F [--priority=重点] [--note=...]
  node scripts/monitor-floors.js --delete=<id>
  node scripts/monitor-floors.js --status
  node scripts/monitor-floors.js --refresh
  node scripts/monitor-floors.js --events
`.trim());
}

function argValue(name) {
  const prefix = `--${name}=`;
  const found = process.argv.find(a => a.startsWith(prefix));
  return found ? found.slice(prefix.length) : '';
}

function hasArg(name) {
  return process.argv.includes(`--${name}`);
}

function printRows(rows) {
  console.log(JSON.stringify(rows, null, 2));
}

function main() {
  const db = openDb();
  ensureMonitorSchema(db);

  try {
    if (hasArg('list')) {
      printRows(loadMonitors(db, { includeDisabled: true }));
      return;
    }
    if (hasArg('add')) {
      const row = saveMonitor(db, {
        building: argValue('building'),
        floor_label: argValue('floor'),
        sub_area_text: argValue('sub-area'),
        priority: argValue('priority') || '重点',
        note: argValue('note') || '',
      });
      printRows(row);
      return;
    }
    const deleteArg = argValue('delete');
    if (deleteArg) {
      const changes = deleteMonitor(db, Number(deleteArg));
      console.log(`deleted=${changes}`);
      return;
    }
    if (hasArg('status')) {
      printRows(computeMonitorStatuses(db, { includeDisabled: true, run_id: argValue('run-id') }));
      return;
    }
    if (hasArg('refresh')) {
      printRows(refreshMonitorSnapshots(db, { run_id: argValue('run-id') }));
      return;
    }
    if (hasArg('events')) {
      printRows(loadMonitorEvents(db, Number(argValue('limit')) || 100, { run_id: argValue('run-id') }));
      return;
    }
    usage();
  } finally {
    db.close();
  }
}

if (require.main === module) main();
