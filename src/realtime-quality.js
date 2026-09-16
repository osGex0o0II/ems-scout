'use strict';

const ENUM_FIELDS = {
  '\u5f53\u524d\u5f00\u5173\u673a\u72b6\u6001': ['\u5f00\u673a', '\u5173\u673a'],
  '\u9ad8\u98ce\u901f\u9600\u95e8\u5f00\u5173\u72b6\u6001': ['\u5f00', '\u5173'],
  '\u4e2d\u98ce\u901f\u9600\u95e8\u5f00\u5173\u72b6\u6001': ['\u5f00', '\u5173'],
  '\u4f4e\u98ce\u901f\u9600\u95e8\u5f00\u5173\u72b6\u6001': ['\u5f00', '\u5173'],
  '\u8bbe\u5b9a\u98ce\u901f': ['\u81ea\u52a8', '\u9ad8', '\u4e2d', '\u4f4e'],
  '\u7cfb\u7edf\u6a21\u5f0f\u8bbe\u7f6e': ['\u5236\u51b7', '\u901a\u98ce', '\u5236\u70ed', '\u9001\u6696', '\u5730\u6696', '\u5236\u70ed+\u5730\u6696'],
  '\u8fbe\u6e29\u98ce\u673a\u72b6\u6001': ['\u5f00\u542f', '\u5173\u95ed'],
  '\u96c6\u63a7\u9501\u5b9a': ['\u5f00\u542f', '\u5173\u95ed'],
  '\u7cfb\u7edf\u7c7b\u578b': ['\u4e24\u7ba1\u51b7\u6696', '\u4e24\u7ba1\u5236\u51b7', '\u4e24\u7ba1\u5236\u70ed', '\u56db\u7ba1\u5236', '\u4e24\u7ba1\u51b7\u6696+\u5730\u6696'],
  '\u5f85\u673a\u663e\u793a\u6e29\u5ea6': ['\u5b9e\u9645\u6e29\u5ea6', '\u8bbe\u5b9a\u6e29\u5ea6'],
  '\u9632\u51bb\u4fdd\u62a4\u662f\u5426\u5f00\u542f': ['\u5f00\u542f', '\u5173\u95ed'],
  '\u5ba4\u6e29\u663e\u793a\u7cbe\u5ea6': ['0.1\u2103', '0.5\u2103', '1\u2103'],
  '\u6e29\u5ea6\u5355\u4f4d\u9009\u62e9': ['\u6444\u6c0f\u5ea6', '\u534e\u6c0f\u5ea6'],
  '\u6389\u7535\u8bb0\u5fc6': ['\u8bb0\u5fc6', '\u4e0d\u8bb0\u5fc6'],
  '\u6062\u590d\u51fa\u5382\u8bbe\u7f6e': ['\u5e38\u89c4'],
};

const RANGE_FIELDS = {
  '\u5ba4\u5185\u6e29\u5ea6': { min: -10, max: 60 },
  '\u8bbe\u5b9a\u6e29\u5ea6': { min: 5, max: 40 },
  '\u8bbe\u5b9a\u6e29\u5ea6\u4e0a\u9650': { min: 5, max: 45 },
  '\u8bbe\u5b9a\u6e29\u5ea6\u4e0b\u9650': { min: 0, max: 40 },
  '\u901a\u8baf\u5730\u5740 (Modbus)': { min: 0, max: 255 },
};

function normalizeEnum(value) {
  return String(value ?? '').replace(/\s+/g, '');
}

function parseNumber(value) {
  const match = String(value ?? '').match(/-?\d+(?:\.\d+)?/);
  if (!match) return null;
  const number = Number(match[0]);
  return Number.isFinite(number) ? number : null;
}

function inspectRealtimeRow(row = {}) {
  const fields = row.fields || {};
  const fieldCount = Object.keys(fields).length;
  const realtimeTagCount = Number(row.realtimeTagCount);
  const realtimeValidTagCount = Number(row.realtimeValidTagCount);
  const rawFieldCount = Object.keys(row.rawFields || {}).length;
  const issues = [];
  const categories = new Set();

  if (realtimeTagCount === 46) {
    if (realtimeValidTagCount === 0) {
      issues.push('\u5b9e\u65f6\u70b9\u4f4d valid=0');
      categories.add('invalidRealtimeTags');
    } else if (Number.isFinite(realtimeValidTagCount) && realtimeValidTagCount !== 46) {
      issues.push(`\u5b9e\u65f6\u70b9\u4f4d valid=${row.realtimeValidTagCount}/46`);
      categories.add('partialRealtimeTags');
    }

    for (const [name, allowedValues] of Object.entries(ENUM_FIELDS)) {
      if (!(name in fields)) continue;
      const value = fields[name];
      if (!allowedValues.map(normalizeEnum).includes(normalizeEnum(value))) {
        const raw = row.rawFields && row.rawFields[name] !== undefined ? ` raw=${row.rawFields[name]}` : '';
        issues.push(`\u679a\u4e3e\u5f02\u5e38 ${name}=${value}${raw}`);
        categories.add(name === '\u96c6\u63a7\u9501\u5b9a' ? 'invalidLock' : 'invalidEnum');
      }
    }

    for (const [name, range] of Object.entries(RANGE_FIELDS)) {
      if (!(name in fields)) continue;
      const number = parseNumber(fields[name]);
      if (number === null || number < range.min || number > range.max) {
        issues.push(`\u8303\u56f4\u5f02\u5e38 ${name}=${fields[name]} \u671f\u671b ${range.min}-${range.max}`);
        categories.add('outOfRange');
      }
    }
  }

  const collectionComplete = !row.error && !row.defaultLike &&
    fieldCount === 26 && rawFieldCount === 26 && realtimeTagCount === 46;
  return {
    fieldCount,
    realtimeTagCount,
    realtimeValidTagCount,
    rawFieldCount,
    issues,
    categories: [...categories],
    collectionComplete,
    preserveRawValues: collectionComplete,
    rawValueStatus: collectionComplete
      ? (issues.length ? 'ems_raw_anomaly' : 'ems_raw_ok')
      : 'collection_incomplete',
  };
}

module.exports = { ENUM_FIELDS, RANGE_FIELDS, inspectRealtimeRow };
