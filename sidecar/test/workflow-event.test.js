'use strict';

const assert = require('node:assert/strict');
const { PassThrough } = require('node:stream');
const test = require('node:test');
const { adaptLegacyLine } = require('../legacy-line-adapter');
const { WorkflowEventWriter } = require('../workflow-event');

test('legacy enumeration progress becomes canonical progress', () => {
  const adapted = adaptLegacyLine(
    '[PROGRESS]{"t":"c","bldg":"1号","cards":20,"acc":40,"totalSa":10,"curSa":4}',
    'enumeration');

  assert.equal(adapted.kind, 'progress');
  assert.equal(adapted.stage, 'enumeration');
  assert.equal(adapted.progress.percent, 40);
  assert.equal(adapted.progress.current, 4);
  assert.equal(adapted.progress.total, 10);
  assert.equal(adapted.progress.unit, 'sub_area');
  assert.equal(adapted.progress.data.bldg, '1号');
});

test('legacy realtime progress maps phase and device counters', () => {
  const adapted = adaptLegacyLine(
    '[PROGRESS] {"phase":"realtime_batch","percent":25,"deviceDone":5,"deviceTotal":20,"message":"working"}',
    'realtime');

  assert.equal(adapted.kind, 'progress');
  assert.equal(adapted.stage, 'realtime_batch');
  assert.deepEqual(
    {
      percent: adapted.progress.percent,
      current: adapted.progress.current,
      total: adapted.progress.total,
      unit: adapted.progress.unit,
      message: adapted.progress.message,
    },
    { percent: 25, current: 5, total: 20, unit: 'device', message: 'working' });
});

test('malformed protocol markers are not treated as progress or actions', () => {
  assert.equal(adaptLegacyLine('[PROGRESS]{bad json', 'enumeration').kind, 'malformed');
  assert.equal(adaptLegacyLine('[ACTION]', 'enumeration').kind, 'malformed');
  assert.equal(adaptLegacyLine('human log', 'enumeration').kind, 'log');
});

test('writer emits ordered common fields and prevents events after terminal', () => {
  const output = new PassThrough();
  let text = '';
  output.on('data', chunk => { text += chunk; });
  const writer = new WorkflowEventWriter({
    workflowId: 'workflow-1',
    stage: 'enumeration',
    output,
    now: () => new Date('2026-07-11T00:00:00.000Z'),
  });

  writer.started();
  writer.progress({ percent: 50, data: {} });
  writer.terminal('succeeded', 0);

  const events = text.trim().split('\n').map(JSON.parse);
  assert.deepEqual(events.map(event => event.seq), [1, 2, 3]);
  assert.ok(events.every(event => event.contractVersion === 'ems.workflow-event/v1'));
  assert.deepEqual(events.map(event => event.type), ['started', 'progress', 'terminal']);
  assert.throws(() => writer.action('return'), /after terminal/);
});
