'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { createCapturePolling } = require('../../src/capture-polling');

function card(overrides = {}) {
  return {
    name: '1-0101-KT',
    switch: 'OFF',
    indoor: '26',
    setTemp: '24',
    mode: '制冷',
    fan: '中',
    indicator: 'green.png',
    comm: '关机',
    ...overrides,
  };
}

function fakeClock() {
  let elapsed = 0;
  return {
    now: () => elapsed,
    sleep: async milliseconds => { elapsed += milliseconds; },
    elapsed: () => elapsed,
  };
}

test('progressive retry accepts a valid capture without waiting', async () => {
  const clock = fakeClock();
  const polling = createCapturePolling(clock);
  const data = { cards: [card(), card({ name: '1-0102-KT', indoor: '27' })] };

  const result = await polling.qualityCheckWithProgressiveRetry(null, async () => data, 'page');

  assert.equal(result.reason, 'quality_pass');
  assert.equal(result.attempt, 1);
  assert.equal(result.data.qualityReason, 'quality_pass');
  assert.equal(clock.elapsed(), 0);
});

test('progressive retry requires the offline template stability window', async () => {
  const clock = fakeClock();
  const polling = createCapturePolling(clock);
  const data = {
    cards: [
      card({ name: 'A', switch: '-', indoor: '0', setTemp: '0', indicator: 'gray.png', comm: '离线' }),
      card({ name: 'B', switch: '-', indoor: '0', setTemp: '0', indicator: 'gray.png', comm: '离线' }),
    ],
  };

  const result = await polling.qualityCheckWithProgressiveRetry(null, async () => data, 'offline');

  assert.equal(result.reason, 'offline_template_stable');
  assert.equal(result.attempt, 3);
  assert.equal(result.qc.offlineTemplateStable, true);
  assert.equal(clock.elapsed(), 700);
});

test('adaptive polling returns a deterministic timeout', async () => {
  const clock = fakeClock();
  const messages = [];
  const polling = createCapturePolling({
    ...clock,
    log: message => messages.push(message),
  });
  const data = { cards: [card({ name: '0-0001-KT', switch: '-', indicator: '', comm: '' })] };

  const result = await polling.adaptivePolling(null, async () => data, 600, 'placeholder');

  assert.equal(result.reason, 'timeout');
  assert.equal(clock.elapsed(), 600);
  assert.match(messages[0], /placeholder timeout after 600ms/);
});
