#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const { validateEnumData, formatValidation } = require('../src/enum-validator');

const ROOT = path.join(__dirname, '..');
const JSON_PATH = process.env.EMS_JSON_PATH || path.join(ROOT, 'out', 'enum_full_v5.json');

function argValue(name) {
  const hit = process.argv.find(a => a.startsWith(name + '='));
  return hit ? hit.slice(name.length + 1) : '';
}

const bldgArg = argValue('--bldg');
const selectedBuildings = bldgArg ? bldgArg.split(',').map(s => s.trim()).filter(Boolean) : [];

if (!fs.existsSync(JSON_PATH)) {
  console.error('ERROR 采集结果不存在: ' + JSON_PATH);
  process.exit(1);
}

let data;
try {
  data = JSON.parse(fs.readFileSync(JSON_PATH, 'utf8'));
} catch (err) {
  console.error('ERROR 采集结果 JSON 无法解析: ' + err.message);
  process.exit(1);
}

const result = validateEnumData(data, { buildings: selectedBuildings });
for (const line of formatValidation(result)) console.log(line);

if (!result.ok) {
  console.error('');
  console.error('采集结果校验失败，已阻止导入数据库。请重新采集或先检查 Edge 页面是否切到正确楼栋。');
  process.exit(2);
}

console.log('采集结果校验通过。');
