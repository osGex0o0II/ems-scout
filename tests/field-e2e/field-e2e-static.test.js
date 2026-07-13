'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const root = path.join(__dirname, '..', '..');
const script = fs.readFileSync(path.join(root, 'scripts', 'field-e2e.ps1'), 'utf8');

test('field E2E uses the CollectionSnapshot native pipeline in order', () => {
  const stages = [
    'sidecar\\collect.js',
    'Validate CollectionSnapshot v1',
    'Transactional import into fresh temp SQLite',
    'Native SQLite quality audit',
    'Excel export smoke',
  ];
  let previous = -1;
  for (const stage of stages) {
    const index = script.indexOf(stage);
    assert.ok(index > previous, `${stage} must appear after the previous pipeline stage`);
    previous = index;
  }

  assert.match(script, /collection_snapshot_v1\.json/);
  assert.match(script, /sidecar\\runner\.js/);
  assert.match(script, /collection-workflow-events\.ndjson/);
  assert.match(script, /Assert-WorkflowEventStream/);
  assert.match(script, /ems\.workflow-event\/v1/);
  assert.match(script, /EMS_WORKFLOW_ID\s*=\s*\$workflowId/);
  assert.match(script, /"--apply"/);
  assert.match(script, /"--source=latest"/);
  const collectorArguments = script.slice(script.indexOf('$enumArgs = @('), script.indexOf('$sidecarArgs ='));
  assert.doesNotMatch(collectorArguments, /src\\enumerate\.js/);
  assert.match(script, /Native SQLite quality audit[^\n]+-AllowFailure/);
  assert.doesNotMatch(script, /Invoke-Checked "Import into temp SQLite" "node"/);
  assert.doesNotMatch(script, /scripts\\quality-report\.js/);
  assert.doesNotMatch(script, /scripts\\import\.js/);
});

test('field E2E keeps browser isolation, live verify, and cleanup invariants', () => {
  assert.match(script, /Get-FreeLoopbackPort/);
  assert.match(script, /--remote-debugging-address=127\.0\.0\.1/);
  assert.match(script, /--user-data-dir=\$ProfileDirectory/);
  assert.match(script, /\.edge_profile/);
  assert.match(script, /"--verify"/);
  assert.match(script, /Stop-FieldEdge/);
  assert.match(script, /Remove-ProfileWithRetry/);
  assert.match(script, /KeepBrowser/);
  assert.match(script, /KeepProfile/);
  assert.match(script, /ConvertTo-WindowsCommandLine/);
  assert.match(script, /Test-ProfileCommandLine/);
  assert.doesNotMatch(script, /-like \"\*\$profile\*\"/);
  assert.doesNotMatch(script, /\$KeepBrowser\s*=\s*\$true/);
  assert.doesNotMatch(script, /\$KeepProfile\s*=\s*\$true/);
  assert.match(script, /PrepareLoginSession.*requires explicit -LaunchEdge -KeepBrowser and -KeepProfile/s);
});

test('field E2E guards production files by raw metadata and SHA-256 only', () => {
  assert.match(script, /field-e2e-\$stamp-\$runSuffix/);
  assert.match(script, /Get-FileHash -LiteralPath \$Path -Algorithm SHA256/);
  assert.match(script, /ac\.db-wal/);
  assert.match(script, /ac\.db-shm/);
  assert.match(script, /production_db_guard_passed/);
  assert.match(script, /additionally, \$guardMessage/);
  assert.match(script, /Assert-NotProductionPath \$dbPath \$productionDb/);
  assert.match(script, /LocalApplicationData/);
  assert.match(script, /settings\.json/);
  assert.match(script, /enum_full_v5\.json/);
  assert.doesNotMatch(script, /--db=\$productionDb/);
  assert.doesNotMatch(script, /EMS_DB_PATH\s*=\s*\$productionDb/);
});
