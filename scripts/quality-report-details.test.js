const test = require('node:test');
const assert = require('node:assert/strict');
const { buildQualityDetails } = require('../src/quality-details');

test('buildQualityDetails preserves every quality record beyond the legacy sample limit', () => {
  const rows = Array.from({ length: 75 }, (_, index) => ({
    building: '1号',
    floor: '1F',
    page_name: '一页',
    name: `1-${index + 1}-KT`,
    reason: '字段不完整',
  }));

  const details = buildQualityDetails([
    { code: 'invalid_card_fields', severity: 'P1', message: '字段不完整', rows },
  ]);

  assert.equal(details.invalid_card_fields.length, 75);
  assert.equal(details.invalid_card_fields[74].name, '1-75-KT');
  assert.equal(details.invalid_card_fields[0].issue_code, 'invalid_card_fields');
  assert.equal(details.invalid_card_fields[0].severity, 'P1');
});

test('buildQualityDetails preserves complete realtime collection and device records', () => {
  const collectionErrors = Array.from({ length: 3 }, (_, index) => ({
    category: 'timeout',
    building: '2号',
    detail: { page: `${index + 1}F` },
  }));
  const deviceAnomalies = Array.from({ length: 4 }, (_, index) => ({
    building: '2号',
    name: `2-${index + 1}-KT`,
    issues: ['范围异常 室内温度=99℃'],
  }));

  const details = buildQualityDetails([], { collectionErrors, deviceAnomalies });

  assert.equal(details.realtime_collection_errors.length, 3);
  assert.equal(details.realtime_device_anomalies.length, 4);
  assert.equal(details.realtime_device_anomalies[3].device_name, '2-4-KT');
});
