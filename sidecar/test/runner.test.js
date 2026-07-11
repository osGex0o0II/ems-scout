'use strict';

const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const {
  mapTerminalOutcome,
  normalizedExitCode,
  parseArguments,
  parseWorkflowControl,
} = require('../runner');
const { validateSchema } = require('../../scripts/audit-contracts');

const runnerPath = path.join(__dirname, '..', 'runner.js');
const workflowEventSchema = JSON.parse(fs.readFileSync(
  path.join(__dirname, '..', '..', 'contracts', 'workflow-event-v1.schema.json'),
  'utf8'));

test('runner keeps stdout as WorkflowEvent NDJSON and sends human logs to stderr', async () => {
  const childSource = [
    "console.log('collector human log')",
    "console.log('[PROGRESS]' + JSON.stringify({t:'c',bldg:'1号',curSa:2,totalSa:4,cards:10,acc:20}))",
    "console.error('collector warning')",
  ].join(';');
  const result = await invokeRunner([], childSource);

  assert.equal(result.code, 0);
  const events = parseEvents(result.stdout);
  assert.deepEqual(events.map(event => event.type), ['started', 'progress', 'terminal']);
  assert.deepEqual(events.map(event => event.seq), [1, 2, 3]);
  assert.equal(events[1].progress.percent, 50);
  assert.equal(events[2].outcome, 'succeeded');
  assert.match(result.stderr, /collector human log/);
  assert.match(result.stderr, /collector warning/);
  assert.doesNotMatch(result.stdout, /collector human log/);
});

test('legacy actions produce an action and one explicit terminal outcome', async () => {
  const returned = await invokeRunner([], "console.log('[ACTION]return')");
  const returnEvents = parseEvents(returned.stdout);
  assert.equal(returned.code, 130);
  assert.deepEqual(returnEvents.map(event => event.type), ['started', 'action', 'terminal']);
  assert.equal(returnEvents[1].action, 'return');
  assert.equal(returnEvents[2].outcome, 'cancelled');
  assert.equal(returnEvents.filter(event => event.type === 'terminal').length, 1);

  const switchMode = await invokeRunner([], "console.log('[ACTION]switch_to_cdp')");
  const switchEvents = parseEvents(switchMode.stdout);
  assert.equal(switchMode.code, 3);
  assert.equal(switchEvents[2].outcome, 'auth_required');
});

test('exit code 2 is rejected by default and configurable for report findings', async () => {
  const rejected = await invokeRunner([], 'process.exitCode=2');
  assert.equal(rejected.code, 2);
  assert.equal(parseEvents(rejected.stdout).at(-1).outcome, 'rejected');

  const findings = await invokeRunner(['--exit-2-outcome=succeeded_with_findings'], 'process.exitCode=2');
  assert.equal(findings.code, 2);
  assert.equal(parseEvents(findings.stdout).at(-1).outcome, 'succeeded_with_findings');
});

test('child launch failure still produces exactly one internal-error terminal event', async () => {
  const result = await invokeRawRunner([], path.join(__dirname, 'missing-command-for-test'), []);
  const events = parseEvents(result.stdout);

  assert.equal(result.code, 1);
  assert.deepEqual(events.map(event => event.type), ['started', 'terminal']);
  assert.equal(events[1].outcome, 'internal_error');
  assert.equal(events.filter(event => event.type === 'terminal').length, 1);
  assert.match(result.stderr, /child process error/i);
});

test('legacy exit codes map to auth, rejection, and internal error', () => {
  const common = { signal: null, action: null, cancellationRequested: false, exit2Outcome: 'rejected' };
  assert.equal(mapTerminalOutcome({ ...common, code: 3 }), 'auth_required');
  assert.equal(mapTerminalOutcome({ ...common, code: 4 }), 'rejected');
  assert.equal(mapTerminalOutcome({ ...common, code: 1 }), 'internal_error');
  assert.equal(normalizedExitCode(-1073741510, null, null), 1);
  assert.equal(normalizedExitCode(3221225786, null, null), 255);
  assert.equal(normalizedExitCode(0, null, null, true), 130);
});

test('stdin cancel control produces an action and a cancelled terminal event', async () => {
  const result = await invokeCancelableRunner();
  const events = parseEvents(result.stdout);

  assert.equal(result.code, 130);
  assert.deepEqual(events.map(event => event.type), ['started', 'action', 'terminal']);
  assert.equal(events[1].action, 'cancel_requested');
  assert.equal(events[2].outcome, 'cancelled');
  assert.equal(events[2].exitCode, 130);
  assert.equal(events.filter(event => event.type === 'terminal').length, 1);
});

test('workflow control parser rejects mismatched and unversioned commands', () => {
  const valid = JSON.stringify({
    contractVersion: 'ems.workflow-control/v1',
    workflowId: 'test-workflow',
    timestamp: '2026-07-11T08:00:00.000Z',
    type: 'cancel',
    reason: 'user_requested',
  });
  assert.deepEqual(parseWorkflowControl(valid, 'test-workflow'), {
    type: 'cancel',
    reason: 'user_requested',
  });
  assert.throws(
    () => parseWorkflowControl(valid, 'different-workflow'),
    /does not match/);
  assert.throws(
    () => parseWorkflowControl(JSON.stringify({ workflowId: 'test-workflow', type: 'cancel' }), 'test-workflow'),
    /contractVersion/);
});

test('runner options require a command separator and validate findings policy', () => {
  assert.throws(() => parseArguments([]), /child command/);
  assert.throws(
    () => parseArguments(['--exit-2-outcome=unknown', '--', process.execPath]),
    /succeeded_with_findings/);
});

function invokeRunner(runnerArguments, childSource) {
  return invokeRawRunner(runnerArguments, process.execPath, ['-e', childSource]);
}

function invokeRawRunner(runnerArguments, command, commandArguments) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [
      runnerPath,
      '--workflow-id=test-workflow',
      '--stage=enumeration',
      ...runnerArguments,
      '--',
      command,
      ...commandArguments,
    ], { stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.once('error', reject);
    child.once('close', (code, signal) => resolve({ code, signal, stdout, stderr }));
  });
}

function invokeCancelableRunner() {
  return new Promise((resolve, reject) => {
    const childSource = "process.on('SIGINT',()=>process.exit(0));setInterval(()=>{},1000)";
    const child = spawn(process.execPath, [
      runnerPath,
      '--workflow-id=test-workflow',
      '--stage=enumeration',
      '--',
      process.execPath,
      '-e',
      childSource,
    ], { stdio: ['pipe', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    let controlSent = false;
    child.stdout.on('data', chunk => {
      stdout += chunk;
      if (!controlSent && stdout.includes('"type":"started"')) {
        controlSent = true;
        child.stdin.end(JSON.stringify({
          contractVersion: 'ems.workflow-control/v1',
          workflowId: 'test-workflow',
          timestamp: new Date().toISOString(),
          type: 'cancel',
          reason: 'test_requested',
        }) + '\n');
      }
    });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.once('error', reject);
    child.once('close', (code, signal) => resolve({ code, signal, stdout, stderr }));
  });
}

function parseEvents(stdout) {
  const lines = stdout.trim().split('\n').filter(Boolean);
  return lines.map(line => {
    const event = JSON.parse(line);
    for (const name of ['contractVersion', 'workflowId', 'seq', 'timestamp', 'type', 'stage']) {
      assert.ok(Object.hasOwn(event, name), `missing ${name}`);
    }
    assert.deepEqual(validateSchema(event, workflowEventSchema), []);
    return event;
  });
}
