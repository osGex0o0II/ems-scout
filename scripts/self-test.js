#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const Database = require('better-sqlite3');
const { checkCardQuality, classifyAreaType, getZone, assessBuildingIdentity, labelSamePageDuplicateCards, classifyPersistentDeviceAnomalyPage, normalizeCardValues, normalizeKnownSourceDefects, classifyKnownMissingIndicatorPage, isAcceptedCaptureQualityReason, isPlaceholderCardName, isKnownCommunicationState, isStructurallyValidOfflinePage, derivePageIdentityMetadata } = require('../src/rules');
const { validateEnumData } = require('../src/enum-validator');
const { inspectRealtimeRow } = require('../src/realtime-quality');
const { ensureHistorySchema, restoreCurrentFromRun } = require('../src/data-history');
const { parseTimestampMillis } = require('../src/time');

const ROOT = path.join(__dirname, '..');

function assert(cond, msg) {
  if (!cond) throw new Error(msg);
}

function expectedLocalTimestamp(value) {
  const instant = new Date(value);
  const offsetMinutes = -instant.getTimezoneOffset();
  const localWallTime = new Date(instant.getTime() + offsetMinutes * 60_000)
    .toISOString()
    .slice(0, 23);
  const sign = offsetMinutes >= 0 ? '+' : '-';
  const absoluteMinutes = Math.abs(offsetMinutes);
  const hours = String(Math.floor(absoluteMinutes / 60)).padStart(2, '0');
  const minutes = String(absoluteMinutes % 60).padStart(2, '0');
  return `${localWallTime}${sign}${hours}:${minutes}`;
}

function runImport(jsonPath, dbPath, args = [], envOverrides = {}) {
  const result = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'import.js'), ...args], {
    cwd: ROOT,
    env: {
      ...process.env,
      EMS_JSON_PATH: jsonPath,
      EMS_DB_PATH: dbPath,
      EMS_SKIP_ENUM_VALIDATION: '1',
      ...envOverrides,
    },
    encoding: 'utf8',
  });
  if (result.status !== 0) {
    throw new Error(`import.js failed\nSTDOUT:\n${result.stdout}\nSTDERR:\n${result.stderr}`);
  }
}

function writeJson(file, data) {
  fs.writeFileSync(file, JSON.stringify(data), 'utf8');
}

function testRules() {
  const loaded34 = Array.from({ length: 20 }, (_, i) => ({
    name: `3F-${i}-KT`,
    switch: i % 2 ? 'OFF' : 'ON',
    mode: '制冷',
    indoor: '26',
    setTemp: '25',
    fan: '中',
    comm: i % 2 ? '关机' : '开机',
  }));
  const notReady34 = loaded34.map(c => ({ ...c, switch: '-', comm: '' }));
  const placeholder = loaded34.map(c => ({ ...c, name: '0-0001-KT' }));
  const labeledPlaceholder = loaded34.map((c, i) => ({ ...c, name: `0-0001-KT#${i + 1}` }));
  const realMixed = loaded34.map((c, i) => ({
    ...c,
    indoor: String(25 + (i % 3)),
    setTemp: String(20 + (i % 4)),
    fan: i % 2 ? '高' : '低',
    indicator: i % 2 ? '3bdc38eda0ae77f26807b2b6cdde4456.png' : '56f45bb314d74cc8da6c6c8e5942d08d.png',
  }));
  const missingComm = realMixed.map((c, i) => i === 0 ? { ...c, comm: '', indicator: '' } : c);
  const invalidTemp = realMixed.map((c, i) => i === 0 ? { ...c, indoor: '-1615.5', setTemp: '3301.4', mode: '-', fan: '-' } : c);
  const missingActiveFields = realMixed.map((c, i) => i === 0 ? { ...c, mode: '-', fan: '-', indoor: '0', setTemp: '0' } : c);
  const missingIndicator = invalidTemp.map((c, i) => i === 0 ? { ...c, indicator: '' } : c);
  const missingSwitch = invalidTemp.map((c, i) => i === 0 ? { ...c, switch: '-' } : c);
  const widespreadInvalid = realMixed.map((c, i) => i < 3 ? { ...c, setTemp: '3301.4' } : c);
  const offlineTemplate = Array.from({ length: 20 }, (_, i) => ({
    name: `8${String(i).padStart(2, '0')}-KT`,
    switch: '-',
    mode: '通风',
    indoor: '0',
    setTemp: '0',
    fan: '0',
    comm: '离线',
    indicator: '833bea6e66e7ab0e55704d655e135c7c.png',
  }));
  const knownMissingIndicator = [
    ...realMixed.slice(0, 5),
    { ...realMixed[5], name: '2-2BC-2M001-KT-1', indicator: 'wrong-neighbor.png', comm: '关机' },
    { ...realMixed[6], name: '2-2BC-2M001-KT-2', indicator: 'wrong-neighbor.png', comm: '关机' },
  ];
  const intermittentMissingIndicator = realMixed.map((c, i) => i === 0
    ? { ...c, name: '4-1F-KT1-104', indicator: '', comm: '', switch: 'ON', mode: '制冷', indoor: '25.8', setTemp: '17', fan: '高' }
    : c);
  const intermittentMissingFields = intermittentMissingIndicator.map((c, i) => i === 0
    ? { ...c, switch: '-', indoor: '-', setTemp: '-', fan: '-' }
    : c);
  const normalizedKnownMissing = normalizeKnownSourceDefects(knownMissingIndicator);
  const knownMissingWithIncompleteOrdinaryCard = normalizedKnownMissing.map((card, index) => index === 0
    ? { ...card, switch: '-', mode: '-', indoor: '-', setTemp: '-', fan: '-' }
    : card);
  const knownMissingWithOnOffConflict = normalizedKnownMissing.map((card, index) => index === 0
    ? { ...card, comm: '开机', switch: 'OFF' }
    : card);
  const knownMissingWithOffOnConflict = normalizedKnownMissing.map((card, index) => index === 0
    ? { ...card, comm: '关机', switch: 'ON' }
    : card);
  const building3Cards = loaded34.map((c, i) => ({ ...c, name: `3-${i}-KT` }));
  const sanitizedInvalid = normalizeCardValues(invalidTemp);
  const labeledDuplicates = labelSamePageDuplicateCards([
    { ...realMixed[0], name: '2-GQ-KT-1', _sourceX: 300, _sourceY: 200 },
    { ...realMixed[1], name: '2-GQ-KT-1', _sourceX: 100, _sourceY: 200 },
  ]);

  assert(!checkCardQuality(loaded34).ok, '3/4号默认值统一时应触发模板检测');
  assert(!checkCardQuality(notReady34).ok, '3/4号默认值且 comm/switch 未完整时应失败');
  assert(!checkCardQuality(placeholder).ok, '0-0001-KT 占位符应失败');
  assert(!checkCardQuality(labeledPlaceholder).ok, '带编号后缀的占位符也应失败');
  assert(isPlaceholderCardName('0-0001-KT#12') && isPlaceholderCardName('   '), '占位名分类必须覆盖编号后缀和空白名称');
  assert(isKnownCommunicationState('开机') && isKnownCommunicationState('关机') && isKnownCommunicationState('离线'), '三个通讯状态必须进入白名单');
  assert(!isKnownCommunicationState('') && !isKnownCommunicationState('未知') && !isKnownCommunicationState('1'), '空值和任意非白名单状态必须保持未知');
  assert(checkCardQuality(realMixed).ok, '非模板真实页且通讯完整时应通过');
  assert(!checkCardQuality(missingComm).ok, '任一卡缺通讯/indicator 时应失败');
  assert(!checkCardQuality(invalidTemp).ok, '异常温度和缺失模式/风速应失败');
  assert(!checkCardQuality(missingActiveFields).ok, '开机/关机设备字段缺失应失败');
  assert(!checkCardQuality(offlineTemplate).ok, '全离线默认模板不应作为 quality_pass 通过');
  assert(isStructurallyValidOfflinePage(offlineTemplate), '真实名称且状态完整的离线页应满足结构门槛');
  assert(!isStructurallyValidOfflinePage(offlineTemplate.map((card, index) => index === 0 ? { ...card, name: '0-0001-KT#1' } : card)), '稳定离线例外不得豁免编号占位名');
  assert(!isStructurallyValidOfflinePage(offlineTemplate.map((card, index) => index === 0 ? { ...card, comm: '未知' } : card)), '稳定离线例外不得豁免未知通讯');
  assert(isAcceptedCaptureQualityReason('offline_template_stable'), '稳定全离线模板应通过最终采集门槛');
  assert(!isAcceptedCaptureQualityReason('stable_partial'), '通讯状态缺失的稳定部分页必须继续阻断');
  assert(isAcceptedCaptureQualityReason('device_anomalies_preserved'), '稳定的有界设备异常应通过最终采集门槛');
  assert(isAcceptedCaptureQualityReason('known_source_indicator_missing'), '精确登记的 EMS indicator 缺失设备应通过最终采集门槛');
  assert(isAcceptedCaptureQualityReason('known_intermittent_indicator_missing'), '间歇性 indicator 缺失设备应作为待复核结果保留');
  assert(!isAcceptedCaptureQualityReason('template_values_unconfirmed'), '默认模板值不得作为最终采集结果放行');
  assert(!isAcceptedCaptureQualityReason(''), '缺少质量原因的页面必须继续阻断');
  assert(classifyPersistentDeviceAnomalyPage(invalidTemp).eligible, '20 张卡中 1 张稳定设备异常应可进入保留候选');
  assert(!classifyPersistentDeviceAnomalyPage(missingComm).eligible, '通讯未解析时不得按设备异常放行');
  assert(!classifyPersistentDeviceAnomalyPage(missingIndicator).eligible, '指示器缺失时不得按设备异常放行');
  assert(!classifyPersistentDeviceAnomalyPage(missingSwitch).eligible, '活动设备开关状态缺失时不得按设备异常放行');
  assert(!classifyPersistentDeviceAnomalyPage(widespreadInvalid).eligible, '异常设备超过页面 10% 时必须阻断');
  assert(!classifyPersistentDeviceAnomalyPage(placeholder).eligible, '占位符卡名不得按设备异常放行');
  assert(!classifyPersistentDeviceAnomalyPage(invalidTemp.slice(0, 1), { rawCount: 20, uniqueCount: 1 }).eligible, '重复塌缩页不得按设备异常放行');
  assert(normalizedKnownMissing.filter(c => !c.indicator && !c.comm).length === 2, '已知缺陷设备不得沿用邻卡 indicator/comm');
  assert(classifyKnownMissingIndicatorPage(normalizedKnownMissing).eligible, '仅两台精确登记设备缺 indicator 时应作为已知源缺陷保留');
  assert(!classifyKnownMissingIndicatorPage(knownMissingWithIncompleteOrdinaryCard).eligible, '已知缺图例外不得豁免同页普通活动设备的字段缺失');
  assert(!classifyKnownMissingIndicatorPage(knownMissingWithOnOffConflict).eligible, '已知缺图例外不得豁免普通卡开机/OFF 冲突');
  assert(!classifyKnownMissingIndicatorPage(knownMissingWithOffOnConflict).eligible, '已知缺图例外不得豁免普通卡关机/ON 冲突');
  assert(!classifyKnownMissingIndicatorPage(normalizedKnownMissing.map((c, i) => i === 0 ? { ...c, indicator: '', comm: '' } : c)).eligible, '出现第三台缺 indicator 时必须阻断');
  assert(classifyKnownMissingIndicatorPage(intermittentMissingIndicator).eligible, '4号楼1F间歇性缺 indicator 且关键字段完整时应保留并继续任务');
  assert(!classifyKnownMissingIndicatorPage(intermittentMissingFields).eligible, '间歇性缺 indicator 设备关键字段也缺失时必须阻断');
  assert(!checkCardQuality(realMixed.slice(0, 1), { rawCount: 20, uniqueCount: 1 }).ok, 'raw 多但 unique 极少的重复塌缩页应失败');
  assert(checkCardQuality(realMixed.slice(0, 7), { rawCount: 10, uniqueCount: 7 }).ok, '轻微重复渲染页应按唯一设备放行');
  assert(assessBuildingIdentity('3号', building3Cards, 30).ok, '3号楼命名空间和子区数应通过身份校验');
  assert(assessBuildingIdentity('6号', [{ name: '6-1F-KT-1' }, { name: '6-2F-KT-1' }], 30).ok, '6号楼 BM 内联缺席时30个普通子区仍应通过身份校验');
  assert(!assessBuildingIdentity('1号', building3Cards, 30).ok, '3号楼卡片不得通过1号楼身份校验');
  assert(!assessBuildingIdentity('2号', building3Cards, 30).ok, '3号楼卡片不得通过2号楼身份校验');
  assert(!assessBuildingIdentity('2号', [{ name: '3-DTT-KT-1' }, { name: '3-DTT-KT-2' }], 30).ok, '楼栋身份校验应阻断跨楼栋卡片');
  assert(sanitizedInvalid[0].indoor === '-' && sanitizedInvalid[0].setTemp === '-', '超范围温度必须归一化为缺失值');
  assert(labeledDuplicates.cards[0].name === '2-GQ-KT-1#2' && labeledDuplicates.cards[1].name === '2-GQ-KT-1#1', '同页重名卡片必须按页面坐标稳定编号');
  assert(labeledDuplicates.cards.every(card => card.sourceName === '2-GQ-KT-1'), '重名编号后必须保留 EMS 原始名称');
  assert(labeledDuplicates.duplicateNames[0].copies === 2, '同页重名元数据必须保留副本数');
  const relabeled = labelSamePageDuplicateCards(labeledDuplicates.cards);
  assert(relabeled.cards.map(card => card.name).join('|') === labeledDuplicates.cards.map(card => card.name).join('|'), '重名编号函数必须幂等，导入不能交换 #1/#2');
  const identityMeta = derivePageIdentityMetadata(labeledDuplicates.cards, labeledDuplicates.duplicateNames);
  assert(identityMeta.count === 2 && identityMeta.rawCount === 2 && identityMeta.uniqueCount === 1, '页面计数必须区分保留卡数与编号前 source-name 数');
  assert(classifyAreaType('3F-WSJ-KT-1', 'grid') === '公区', 'WSJ 应识别为公区');
  assert(classifyAreaType('QL-101-KT', 'grid') === '非公区', 'QL-NNN 应识别为非公区');
  assert(classifyAreaType('ANY', 'group') === '公区', 'group layout 应识别为公区');
  assert(getZone(695, '5号') === 2, '5号 x=695 应为 C座 zone');
}

function testRealtimeQualityContract() {
  const fields = Object.fromEntries(Array.from({ length: 25 }, (_, index) => [`field-${index}`, String(index)]));
  fields['集控锁定'] = '32896';
  const rawFields = { ...fields };
  const anomaly = inspectRealtimeRow({
    fields,
    rawFields,
    realtimeTagCount: 46,
    realtimeValidTagCount: 46,
  });
  assert(anomaly.collectionComplete, '完整实时点位必须判定为采集完成');
  assert(anomaly.preserveRawValues, '完整实时点位必须保留 EMS 原始值');
  assert(anomaly.rawValueStatus === 'ems_raw_anomaly', '非法集控值必须分类为 EMS 原始异常');
  assert(anomaly.categories.includes('invalidLock'), '非法集控值必须进入 invalidLock 分类');

  const incomplete = inspectRealtimeRow({ fields: {}, rawFields: {}, realtimeTagCount: 0 });
  assert(!incomplete.collectionComplete && incomplete.rawValueStatus === 'collection_incomplete', '缺字段实时结果不得伪装为完整采集');
}

function testCollectionValidationBlocksIncompleteResults() {
  const makeCards = (prefix, count, overrides = {}) => Array.from({ length: count }, (_, index) => ({
    name: `${prefix}-${index + 1}-KT`,
    switch: 'OFF',
    mode: '制冷',
    indoor: '26',
    setTemp: '25',
    fan: '中',
    comm: '关机',
    ...overrides,
  }));

  const completeSixBuilding = {
    building: '6号',
    subAreas: Array.from({ length: 31 }, (_, index) => ({
      idx: index,
      text: index === 19 ? 'BM' : index === 13 ? '6F' : `${index + 1}F`,
      floor: index === 19 ? -2 : index + 1,
      x: index * 10,
      y: 20,
      pages: index === 0
        ? [{ page: '一页', qualityReason: 'quality_pass', cards: makeCards('6', 2480) }]
        : index === 13
          ? []
        : index === 19
          ? []
          : [{ page: '一页', qualityReason: 'quality_pass', cards: [] }],
    })),
  };
  const emptyResult = validateEnumData({ buildings: [completeSixBuilding] }, { buildings: ['6号'] });
  assert(!emptyResult.ok && emptyResult.errors.some(error => error.includes('空子区')), '非 BM 空子区必须阻断导入');
  const inlineOnlyBuilding = {
    building: '6号',
    subAreas: [
      { idx: 0, text: '1F', floor: 1, pages: [{ page: '一页', qualityReason: 'quality_pass', cards: makeCards('6', 1) }] },
      { idx: 1, text: 'BM', floor: -2, pages: [{ page: 'inline', count: 0, rawCount: 0, uniqueCount: 0, cards: [] }] },
    ],
  };
  assert(validateEnumData({ buildings: [inlineOnlyBuilding] }, { buildings: ['6号'] }).ok, '6号 BM inline 占位页面是唯一允许的显式空页');

  const templateBuilding = {
    building: '1号',
    subAreas: Array.from({ length: 30 }, (_, index) => ({
      idx: index,
      text: `${index + 1}F`,
      floor: index + 1,
      x: index * 10,
      y: 20,
      pages: [{
        page: '一页',
        qualityReason: index === 0 ? 'template_values_unconfirmed' : 'quality_pass',
        cards: makeCards('1', index === 0 ? 50 : 50),
      }],
    })),
  };
  const templateResult = validateEnumData({ buildings: [templateBuilding] }, { buildings: ['1号'] });
  assert(!templateResult.ok && templateResult.errors.some(error => error.includes('质量原因')), '未确认模板页必须阻断导入');

  const dynamicBuilding = {
    building: '1号',
    subAreas: [{
      idx: 0,
      text: '现场新增子区',
      floor: 99,
      x: 10,
      y: 20,
      pages: [{
        page: '一页',
        qualityReason: 'quality_pass',
        cards: makeCards('1-现场新增', 1),
      }],
    }],
  };
  const dynamicResult = validateEnumData({ buildings: [dynamicBuilding] }, { buildings: ['1号'] });
  assert(dynamicResult.ok, '卡片数和子区数可以随现场配置变化，但批次内部结构完整时必须通过');

  const countMismatch = JSON.parse(JSON.stringify(dynamicBuilding));
  countMismatch.subAreas[0].pages[0].count = 10;
  countMismatch.subAreas[0].pages[0].rawCount = 10;
  countMismatch.subAreas[0].pages[0].uniqueCount = 10;
  const countMismatchResult = validateEnumData({ buildings: [countMismatch] }, { buildings: ['1号'] });
  assert(!countMismatchResult.ok && countMismatchResult.errors.some(error => error.includes('声明数量')), '页面声明数量与原始 cards 不一致时必须在归一化前阻断');

  const rawOnlyMismatch = JSON.parse(JSON.stringify(dynamicBuilding));
  rawOnlyMismatch.subAreas[0].pages[0].count = 1;
  rawOnlyMismatch.subAreas[0].pages[0].rawCount = 10;
  rawOnlyMismatch.subAreas[0].pages[0].uniqueCount = 1;
  const rawOnlyMismatchResult = validateEnumData({ buildings: [rawOnlyMismatch] }, { buildings: ['1号'] });
  assert(!rawOnlyMismatchResult.ok && rawOnlyMismatchResult.errors.some(error => error.includes('rawCount=10')), 'rawCount 必须独立守恒实际保留卡数');

  const emptySecondPage = JSON.parse(JSON.stringify(dynamicBuilding));
  emptySecondPage.subAreas[0].pages.push({ page: '二页', count: 0, rawCount: 0, uniqueCount: 0, qualityReason: 'quality_pass', cards: [] });
  const emptySecondPageResult = validateEnumData({ buildings: [emptySecondPage] }, { buildings: ['1号'] });
  assert(!emptySecondPageResult.ok && emptySecondPageResult.errors.some(error => error.includes('空页面')), '非 BM 子区中的任一声明空页必须阻断');

  const knownSourcePage = {
    building: '2号',
    subAreas: [{ idx: 0, text: '2BC', floor: 2.5, pages: [{
      page: '一页',
      qualityReason: 'known_source_indicator_missing',
      cards: [
        { name: '2-2BC-2M001-KT-1', switch: '-', mode: '-', indoor: '-', setTemp: '-', fan: '-', indicator: '', comm: '' },
        { name: '2-2BC-2M001-KT-2', switch: '-', mode: '-', indoor: '-', setTemp: '-', fan: '-', indicator: '', comm: '' },
        { name: '2-2BC-201-KT', switch: 'ON', mode: '制冷', indoor: '26', setTemp: '24', fan: '高', indicator: '56f45bb314d74cc8da6c6c8e5942d08d.png', comm: '开机' },
      ],
    }] }],
  };
  assert(validateEnumData({ buildings: [knownSourcePage] }, { buildings: ['2号'] }).ok, '精确已知缺图设备可豁免自身 indicator/comm，普通卡仍须结构完整');
}

function testEnumeratorPageNormalizationContract() {
  const { pageFromData } = require('../src/enumerate');
  const card = { name: '2-2F-B-KT', switch: 'ON', mode: '制冷', indoor: '26', setTemp: '24', fan: '高', indicator: '56f45bb314d74cc8da6c6c8e5942d08d.png', comm: '开机' };
  const page = pageFromData('一页', { cards: [{ ...card }, { ...card }], qualityReason: 'quality_pass' });
  assert(page.count === 2 && page.rawCount === 2 && page.uniqueCount === 1, 'pageFromData 必须输出保留卡数 2/2 和 source-name 数 1');
  assert(page.cards.map(row => row.name).join('|') === '2-2F-B-KT#1|2-2F-B-KT#2', 'pageFromData 必须保留合法同名设备并稳定编号');
  assert(page.duplicateNames.length === 1 && page.duplicateNames[0].copies === 2, 'pageFromData 必须保留同名来源证据');
}

function runImportResult(jsonPath, dbPath, args = [], envOverrides = {}) {
  return spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'import.js'), ...args], {
    cwd: ROOT,
    env: {
      ...process.env,
      EMS_JSON_PATH: jsonPath,
      EMS_DB_PATH: dbPath,
      EMS_SKIP_ENUM_VALIDATION: '0',
      ...envOverrides,
    },
    encoding: 'utf8',
  });
}

function testPartialImport() {
  const tmp = path.join(ROOT, 'out', 'self-test');
  fs.rmSync(tmp, { recursive: true, force: true });
  fs.mkdirSync(tmp, { recursive: true });
  const json1 = path.join(tmp, 'enum1.json');
  const json2 = path.join(tmp, 'enum2.json');
  const dbPath = path.join(tmp, 'ac-test.db');

  writeJson(json1, {
    buildings: [
      { building: '1号', menuClicked: '1号楼', subAreas: [{ idx: 0, text: '1F', floor: 1, x: 10, y: 20, pages: [{ page: 'default', layout: 'grid', cards: [{ name: '1F-A-KT', switch: 'ON', mode: '制冷', indoor: '26', setTemp: '25', fan: '中', comm: '开机' }] }] }] },
      { building: '2号', menuClicked: '2号楼', subAreas: [{ idx: 0, text: '1F', floor: 1, x: 30, y: 40, pages: [{ page: 'default', layout: 'grid', cards: [{ name: '2F-A-KT', switch: 'OFF', mode: '制冷', indoor: '26', setTemp: '25', fan: '中', comm: '关机' }] }] }] },
    ],
  });
  writeJson(json2, {
    completedAt: '2026-07-12T01:00:00.000Z',
    buildings: [
      { building: '2号', menuClicked: '2号楼', completedAt: '2026-07-12T00:30:00.000Z', subAreas: [{ idx: 0, text: '2F', floor: 2, x: 50, y: 60, pages: [{ page: 'default', layout: 'grid', collectedAt: '2026-07-12T00:20:15.000Z', cards: [
        { name: '2F-B-KT', switch: 'ON', mode: '制冷', indoor: '27', setTemp: '24', fan: '高', comm: '开机' },
        { name: '2F-B-KT', switch: 'ON', mode: '制冷', indoor: '27', setTemp: '24', fan: '高', comm: '开机' },
        { name: '2F-C-KT', switch: 'OFF', mode: '通风', indoor: '26', setTemp: '25', fan: '低', comm: '关机' },
        { name: '2F-D-KT', switch: 'OFF', mode: '通风', indoor: '26', setTemp: '25', fan: '低', indicator: '', comm: '' },
      ], rawCount: 4, uniqueCount: 3, duplicateNames: [{ name: '2F-B-KT', copies: 2 }] }] }] },
    ],
  });

  runImport(json1, dbPath);
  runImport(json2, dbPath, ['--bldg=2号']);

  const db = new Database(dbPath, { readonly: true });
  const rows = db.prepare(`
    SELECT sa.building, COUNT(*) AS cards, GROUP_CONCAT(c.name) AS names
    FROM sub_areas sa
    JOIN pages p ON p.sub_area_id = sa.id
    JOIN cards c ON c.page_id = p.id
    GROUP BY sa.building
    ORDER BY sa.building
  `).all();
  const pageMeta = db.prepare(`
    SELECT p.count, p.raw_count, p.unique_count, p.duplicate_names, p.collected_at
    FROM pages p
    JOIN sub_areas sa ON p.sub_area_id = sa.id
    WHERE sa.building = '2号'
  `).get();
  const latestRun = db.prepare(`
    SELECT card_count, on_count, off_count, offline_count, unknown_count
    FROM collection_runs
    ORDER BY id DESC
    LIMIT 1
  `).get();
  const building2 = db.prepare(`SELECT updated_at FROM buildings WHERE building = '2号'`).get();
  const runPage = db.prepare(`SELECT collected_at FROM run_pages ORDER BY id DESC LIMIT 1`).get();
  db.close();
  fs.rmSync(tmp, { recursive: true, force: true });

  assert(rows.length === 2, '部分导入后应保留未选楼栋');
  assert(rows[0].building === '1号' && rows[0].cards === 1 && rows[0].names === '1F-A-KT', '1号数据应保留');
  assert(rows[1].building === '2号' && rows[1].cards === 4 && rows[1].names.includes('2F-B-KT#1') && rows[1].names.includes('2F-B-KT#2'), '2号同页重名设备应编号后全部入库');
  assert(pageMeta.count === 4 && pageMeta.raw_count === 4 && pageMeta.unique_count === 3, '同页重名卡片编号后仍必须保留输入的原始/唯一计数证据');
  assert(pageMeta.duplicate_names.includes('2F-B-KT'), '重复渲染设备名应入库');
  assert(pageMeta.collected_at === expectedLocalTimestamp('2026-07-12T00:20:15.000Z'), '页面必须保存实际通过采集质量门槛的时间');
  assert(runPage.collected_at === pageMeta.collected_at, '历史批次必须保留页面采集时间');
  assert(latestRun.card_count === 4 && latestRun.on_count === 2 && latestRun.off_count === 1 && latestRun.offline_count === 0 && latestRun.unknown_count === 1, 'run 统计必须按 comm 区分状态，switch=OFF 不得掩盖未知通讯');
  assert(building2.updated_at === expectedLocalTimestamp('2026-07-12T00:30:00.000Z'), '部分导入必须保留楼栋独立采集时间，不能套用顶层时间');
}

function testImportRejectsInvalidPageBeforeMutation() {
  const tmp = path.join(ROOT, 'out', 'self-test-import-atomic');
  fs.rmSync(tmp, { recursive: true, force: true });
  fs.mkdirSync(tmp, { recursive: true });
  const validPath = path.join(tmp, 'valid.json');
  const invalidPath = path.join(tmp, 'invalid.json');
  const dbPath = path.join(tmp, 'ac-atomic.db');
  const card = { name: '1-1F-101-KT', switch: 'ON', mode: '制冷', indoor: '26', setTemp: '24', fan: '高', indicator: '56f45bb314d74cc8da6c6c8e5942d08d.png', comm: '开机' };
  const valid = { buildings: [{ building: '1号', subAreas: [{ idx: 0, text: '1F', floor: 1, pages: [{ page: '一页', count: 1, rawCount: 1, uniqueCount: 1, qualityReason: 'quality_pass', cards: [card] }] }] }] };
  writeJson(validPath, valid);
  const seeded = runImportResult(validPath, dbPath);
  assert(seeded.status === 0, `合法动态批次应可导入\n${seeded.stdout}\n${seeded.stderr}`);

  const snapshot = () => {
    const db = new Database(dbPath, { readonly: true });
    const value = JSON.stringify({
      cards: db.prepare('SELECT name, comm FROM cards ORDER BY id').all(),
      runs: db.prepare('SELECT id, run_key, status, card_count FROM collection_runs ORDER BY id').all(),
      sources: db.prepare('SELECT building, run_id FROM current_data_sources ORDER BY building').all(),
    });
    db.close();
    return value;
  };
  const before = snapshot();

  for (const invalid of [
    { ...valid, buildings: [{ ...valid.buildings[0], subAreas: [{ ...valid.buildings[0].subAreas[0], pages: [{ ...valid.buildings[0].subAreas[0].pages[0], count: 1, rawCount: 10, uniqueCount: 1 }] }] }] },
    { ...valid, buildings: [{ ...valid.buildings[0], subAreas: [{ ...valid.buildings[0].subAreas[0], pages: [...valid.buildings[0].subAreas[0].pages, { page: '二页', count: 0, rawCount: 0, uniqueCount: 0, qualityReason: 'quality_pass', cards: [] }] }] }] },
    { buildings: [{ building: '6号', subAreas: [
      { idx: 0, text: 'A座1F', floor: 1, pages: [{ page: '一页', count: 1, rawCount: 1, uniqueCount: 1, qualityReason: 'quality_pass', cards: [{ ...card, name: '6-1F-A-KT' }] }] },
      { idx: 1, text: 'BM', floor: -2, pages: [{ page: '一页', count: 0, rawCount: 0, uniqueCount: 0, cards: [] }] },
    ] }] },
    { buildings: [{ building: '1号', subAreas: [{ idx: 0, text: '1F', floor: 1, pages: [{ page: '一页', count: 4, rawCount: 4, uniqueCount: 1, duplicateNames: [{ name: 'room', copies: 3 }], qualityReason: 'quality_pass', cards: Array.from({ length: 4 }, (_, index) => ({ ...card, name: `room#${index + 1}` })) }] }] }] },
    { buildings: [{ building: '1号', subAreas: [{ idx: 0, text: '1F', floor: 1, pages: [{ page: '一页', count: 4, rawCount: 4, uniqueCount: 3, duplicateNames: [{ name: 'A', copies: 2 }], qualityReason: 'quality_pass', cards: ['A', 'A', 'B', 'B'].map(name => ({ ...card, name })) }] }] }] },
    { buildings: [{ building: '1号', subAreas: [{ idx: 0, text: '1F', floor: 1, pages: [{ page: '一页', count: 1, rawCount: 1, uniqueCount: 1, duplicateNames: '{malformed', qualityReason: 'quality_pass', cards: [{ ...card }] }] }] }] },
    { buildings: [{ building: '1号', subAreas: [{ idx: 0, text: '1F', floor: 1, pages: [{ page: '一页', count: 2, rawCount: 2, uniqueCount: 2, qualityReason: 'quality_pass', cards: [
      { ...card, name: 'room#1', sourceName: 'room' },
      { ...card, name: 'room#2', sourceName: 'room' },
    ] }] }] }] },
  ]) {
    writeJson(invalidPath, invalid);
    const rejected = runImportResult(invalidPath, dbPath);
    assert(rejected.status !== 0 && rejected.stderr.includes('采集结果校验失败'), '数量、BM 空页或重复证据矛盾必须由真实导入入口拒绝');
    assert(snapshot() === before, '导入拒绝后当前数据、历史批次和来源绑定必须保持不变');
  }
  fs.rmSync(tmp, { recursive: true, force: true });
}

function testValidationEnabledImportLabelsDuplicatesAndPreservesUnknown() {
  const tmp = path.join(ROOT, 'out', 'self-test-import-normal');
  fs.rmSync(tmp, { recursive: true, force: true });
  fs.mkdirSync(tmp, { recursive: true });
  const duplicatePath = path.join(tmp, 'duplicates.json');
  const duplicateDb = path.join(tmp, 'duplicates.db');
  const baseCard = { name: '2-2F-B-KT', switch: 'ON', mode: '制冷', indoor: '26', setTemp: '24', fan: '高', indicator: '56f45bb314d74cc8da6c6c8e5942d08d.png', comm: '开机' };
  writeJson(duplicatePath, { buildings: [{ building: '2号', subAreas: [{ idx: 0, text: '2F', floor: 2, pages: [
    { page: '一页', count: 2, rawCount: 2, uniqueCount: 1, duplicateNames: [{ name: '2-2F-B-KT', copies: 2 }], qualityReason: 'quality_pass', cards: [{ ...baseCard }, { ...baseCard }] },
    { page: '二页', duplicate_names: [{ name: '2-ROOM', copies: 2 }], qualityReason: 'quality_pass', cards: [{ ...baseCard, name: '2-ROOM#1' }, { ...baseCard, name: '2-ROOM#2' }] },
  ] }] }] });
  const duplicateImport = runImportResult(duplicatePath, duplicateDb);
  assert(duplicateImport.status === 0, `正常导入必须接受证据一致的原始同名卡并编号\n${duplicateImport.stdout}\n${duplicateImport.stderr}`);
  const duplicateSqlite = new Database(duplicateDb, { readonly: true });
  const duplicateRows = duplicateSqlite.prepare("SELECT c.name FROM cards c JOIN pages p ON p.id=c.page_id WHERE p.page_name='一页' ORDER BY c.id").all().map(row => row.name);
  const duplicateMeta = duplicateSqlite.prepare("SELECT count, raw_count, unique_count FROM pages WHERE page_name='一页'").get();
  const aliasMeta = duplicateSqlite.prepare("SELECT count, raw_count, unique_count, duplicate_names FROM pages WHERE page_name='二页'").get();
  duplicateSqlite.close();
  assert(duplicateRows.join('|') === '2-2F-B-KT#1|2-2F-B-KT#2', '正常导入必须稳定编号原始同名卡');
  assert(duplicateMeta.count === 2 && duplicateMeta.raw_count === 2 && duplicateMeta.unique_count === 1, '导入必须保留 source-name 计数证据');
  assert(JSON.parse(aliasMeta.duplicate_names)[0].name === '2-ROOM', 'snake_case duplicate_names 必须贯穿正常导入并保存');
  assert(aliasMeta.count === 2 && aliasMeta.raw_count === 2 && aliasMeta.unique_count === 1, '省略页面计数时必须按最终采用的 snake_case evidence 推导 2/2/1');
  const duplicateQualityOut = path.join(tmp, 'duplicate-quality');
  const duplicateQuality = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-run'], { cwd: ROOT, env: { ...process.env, EMS_DB_PATH: duplicateDb, EMS_QUALITY_OUT: duplicateQualityOut }, encoding: 'utf8' });
  const duplicateReport = JSON.parse(fs.readFileSync(path.join(duplicateQualityOut, 'quality_report_run1.json'), 'utf8'));
  assert(duplicateQuality.status === 0 && duplicateReport.summary.invalid_pages === 0, 'snake_case evidence 的已编号页面必须通过独立质量审计');

  const unknownPath = path.join(tmp, 'unknown.json');
  const unknownDb = path.join(tmp, 'unknown.db');
  const qualityOut = path.join(tmp, 'quality');
  writeJson(unknownPath, { buildings: [{ building: '1号', subAreas: [{ idx: 0, text: '1F', floor: 1, pages: [{ page: '一页', count: 1, rawCount: 1, uniqueCount: 1, qualityReason: 'quality_pass', cards: [{ ...baseCard, name: '1-1F-A-KT', comm: '故障' }] }] }] }] });
  const unknownImport = runImportResult(unknownPath, unknownDb);
  assert(unknownImport.status === 0, `正常导入必须保留未知通讯原值\n${unknownImport.stdout}\n${unknownImport.stderr}`);
  const quality = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-run'], { cwd: ROOT, env: { ...process.env, EMS_DB_PATH: unknownDb, EMS_QUALITY_OUT: qualityOut }, encoding: 'utf8' });
  assert(quality.status === 2, `未知通讯质量报告必须阻断\n${quality.stdout}\n${quality.stderr}`);
  const unknownSqlite = new Database(unknownDb, { readonly: true });
  const unknownState = unknownSqlite.prepare('SELECT c.comm, r.unknown_count, r.status FROM cards c CROSS JOIN collection_runs r ORDER BY r.id DESC LIMIT 1').get();
  unknownSqlite.close();
  const unknownReport = JSON.parse(fs.readFileSync(path.join(qualityOut, 'quality_report_run1.json'), 'utf8'));
  assert(unknownState.comm === '故障' && unknownState.unknown_count === 1 && unknownState.status === 'needs_review', '未知通讯必须原样入库、计入批次并转为需复核');
  assert(unknownReport.summary.unknown_comm === unknownState.unknown_count, '质量 unknown 数必须与历史批次一致');
  fs.rmSync(tmp, { recursive: true, force: true });
}

function testQualityReportAuditsPageStructureAndUnknownComm() {
  const tmp = path.join(ROOT, 'out', 'self-test-quality-structure');
  fs.rmSync(tmp, { recursive: true, force: true });
  fs.mkdirSync(tmp, { recursive: true });
  const jsonPath = path.join(tmp, 'enum.json');
  const dbPath = path.join(tmp, 'ac-quality.db');
  const qualityOut = path.join(tmp, 'quality');
  writeJson(jsonPath, { buildings: [{ building: '1号', subAreas: [{ idx: 0, text: '1F', floor: 1, pages: [{ page: '一页', qualityReason: 'quality_pass', cards: [{ name: '1-1F-101-KT', switch: 'OFF', mode: '制冷', indoor: '26', setTemp: '25', fan: '中', indicator: '3bdc38eda0ae77f26807b2b6cdde4456.png', comm: '故障' }] }] }] }] });
  runImport(jsonPath, dbPath);
  const db = new Database(dbPath);
  const runUnknownCount = db.prepare('SELECT unknown_count FROM collection_runs ORDER BY id DESC LIMIT 1').get().unknown_count;
  db.prepare('UPDATE pages SET raw_count = 10 WHERE page_name = ?').run('一页');
  const saId = db.prepare('SELECT id FROM sub_areas LIMIT 1').get().id;
  db.prepare("INSERT INTO pages (sub_area_id, page_name, count, raw_count, unique_count, quality_reason) VALUES (?, '二页', 0, 0, 0, 'quality_pass')").run(saId);
  db.close();

  const result = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js')], {
    cwd: ROOT,
    env: { ...process.env, EMS_DB_PATH: dbPath, EMS_QUALITY_OUT: qualityOut },
    encoding: 'utf8',
  });
  assert(result.status === 2, `质量报告必须阻断零卡页和非空未知通讯\n${result.stdout}\n${result.stderr}`);
  const report = JSON.parse(fs.readFileSync(path.join(qualityOut, 'quality_report.json'), 'utf8'));
  assert(report.summary.invalid_pages === 2 && report.issues.some(issue => issue.code === 'invalid_pages'), '质量报告必须独立识别零卡页及 raw_count/cards 矛盾');
  assert(report.summary.unknown_comm === 1 && report.issues.some(issue => issue.code === 'unknown_comm'), '非空未知通讯必须进入 unknown_comm 并阻断');
  assert(report.summary.unknown_comm === runUnknownCount, '质量审计 unknown 数必须与导入批次统计一致');
  fs.rmSync(tmp, { recursive: true, force: true });
}

function testQualityReportRejectsMalformedBmPage() {
  const tmp = path.join(ROOT, 'out', 'self-test-quality-bm');
  fs.rmSync(tmp, { recursive: true, force: true });
  fs.mkdirSync(tmp, { recursive: true });
  const jsonPath = path.join(tmp, 'enum.json');
  const dbPath = path.join(tmp, 'ac.db');
  const qualityOut = path.join(tmp, 'quality');
  writeJson(jsonPath, { buildings: [{ building: '6号', subAreas: [{ idx: 0, text: '1F', floor: 1, pages: [{ page: '一页', cards: [{ name: '6-1F-A-KT', switch: 'OFF', mode: '制冷', indoor: '26', setTemp: '25', fan: '中', indicator: '3bdc38eda0ae77f26807b2b6cdde4456.png', comm: '关机' }] }] }, { idx: 1, text: 'BM', floor: -2, pages: [] }] }] });
  runImport(jsonPath, dbPath);
  const db = new Database(dbPath);
  const bmId = db.prepare("SELECT id FROM sub_areas WHERE text='BM'").get().id;
  db.prepare("INSERT INTO pages (sub_area_id, page_name, count, raw_count, unique_count) VALUES (?, '一页', 0, 0, 0)").run(bmId);
  db.close();
  const result = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js')], { cwd: ROOT, env: { ...process.env, EMS_DB_PATH: dbPath, EMS_QUALITY_OUT: qualityOut }, encoding: 'utf8' });
  const report = JSON.parse(fs.readFileSync(path.join(qualityOut, 'quality_report.json'), 'utf8'));
  assert(result.status === 2 && report.summary.invalid_pages === 1, '仅 page_name=inline 且四项计数为零的 BM stub 可豁免');
  fs.rmSync(tmp, { recursive: true, force: true });
}

function testQualityReportAuditsStoredSourceIdentityEvidence() {
  const tmp = path.join(ROOT, 'out', 'self-test-quality-identities');
  fs.rmSync(tmp, { recursive: true, force: true });
  fs.mkdirSync(tmp, { recursive: true });
  const makeCard = (name, index = 0) => ({ name, switch: index % 2 ? 'OFF' : 'ON', mode: index % 2 ? '通风' : '制冷', indoor: String(24 + index), setTemp: String(20 + index), fan: index % 2 ? '低' : '高', indicator: index % 2 ? '3bdc38eda0ae77f26807b2b6cdde4456.png' : '56f45bb314d74cc8da6c6c8e5942d08d.png', comm: index % 2 ? '关机' : '开机' });

  const validJson = path.join(tmp, 'valid.json');
  const validDb = path.join(tmp, 'valid.db');
  const validOut = path.join(tmp, 'valid-quality');
  const repeated = makeCard('2-2F-B-KT');
  writeJson(validJson, { buildings: [{ building: '2号', subAreas: [{ idx: 0, text: '2F', floor: 2, pages: [
    { page: '一页', count: 3, rawCount: 3, uniqueCount: 1, duplicateNames: [{ name: '2-2F-B-KT', copies: 3 }], qualityReason: 'quality_pass', cards: [{ ...repeated }, { ...repeated }, { ...repeated }] },
    { page: '二页', count: 4, rawCount: 4, uniqueCount: 4, duplicateNames: [], qualityReason: 'quality_pass', cards: [
      makeCard('2-INDEPENDENT#1', 0), makeCard('2-INDEPENDENT#2', 1),
      makeCard('2-INDEPENDENT#7', 2), makeCard('2-INDEPENDENT#9', 3),
    ] },
  ] }] }] });
  assert(runImportResult(validJson, validDb).status === 0, '合法三副本 source group 应通过正常导入');
  const validNamesDb = new Database(validDb, { readonly: true });
  const independentNames = validNamesDb.prepare("SELECT c.name FROM cards c JOIN pages p ON p.id=c.page_id WHERE p.page_name='二页' ORDER BY c.id").all().map(row => row.name);
  validNamesDb.close();
  assert(independentNames.join('|') === '2-INDEPENDENT#1|2-INDEPENDENT#2|2-INDEPENDENT#7|2-INDEPENDENT#9', '正常导入必须逐字保留无 duplicate claim 的独立数字后缀设备名');
  const validQuality = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-run'], { cwd: ROOT, env: { ...process.env, EMS_DB_PATH: validDb, EMS_QUALITY_OUT: validOut }, encoding: 'utf8' });
  const validReport = JSON.parse(fs.readFileSync(path.join(validOut, 'quality_report_run1.json'), 'utf8'));
  assert(validQuality.status === 0 && validReport.summary.invalid_pages === 0, '证据完整的编号三副本页不得被独立审计误判');

  const corruptJson = path.join(tmp, 'corrupt.json');
  const corruptDb = path.join(tmp, 'corrupt.db');
  const corruptOut = path.join(tmp, 'corrupt-quality');
  const distinctCards = Array.from({ length: 4 }, (_, index) => makeCard(`1-1F-${index + 1}-KT`, index));
  writeJson(corruptJson, { buildings: [{ building: '1号', subAreas: [{ idx: 0, text: '1F', floor: 1, pages: [{ page: '一页', count: 4, rawCount: 4, uniqueCount: 4, qualityReason: 'quality_pass', cards: distinctCards }] }] }] });
  assert(runImportResult(corruptJson, corruptDb).status === 0, '四个不同 source identity 的基准批次应可导入');
  const corruptSqlite = new Database(corruptDb);
  corruptSqlite.prepare("UPDATE run_pages SET unique_count=1, duplicate_names='' WHERE run_id=1").run();
  corruptSqlite.close();
  const corruptQuality = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-run'], { cwd: ROOT, env: { ...process.env, EMS_DB_PATH: corruptDb, EMS_QUALITY_OUT: corruptOut }, encoding: 'utf8' });
  const corruptReport = JSON.parse(fs.readFileSync(path.join(corruptOut, 'quality_report_run1.json'), 'utf8'));
  const statusDb = new Database(corruptDb, { readonly: true });
  const corruptStatus = statusDb.prepare('SELECT status FROM collection_runs WHERE id=1').get().status;
  statusDb.close();
  assert(corruptQuality.status === 2 && corruptReport.summary.invalid_pages === 1, '4/4/1 且无 duplicate evidence 必须成为 blocking invalid_pages');
  assert(corruptStatus === 'needs_review', 'source identity 元数据矛盾必须把历史批次置为需复核');

  const malformedDb = new Database(corruptDb);
  malformedDb.prepare("UPDATE run_pages SET unique_count=4, duplicate_names='{malformed' WHERE run_id=1").run();
  malformedDb.close();
  const malformedQuality = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-run'], { cwd: ROOT, env: { ...process.env, EMS_DB_PATH: corruptDb, EMS_QUALITY_OUT: corruptOut }, encoding: 'utf8' });
  const malformedReport = JSON.parse(fs.readFileSync(path.join(corruptOut, 'quality_report_run1.json'), 'utf8'));
  assert(malformedQuality.status === 2 && malformedReport.summary.invalid_pages === 1, '非空但无法解析的 duplicate metadata 必须在独立审计中阻断');

  const partialDb = new Database(corruptDb);
  const partialIds = partialDb.prepare('SELECT id FROM run_cards WHERE run_id=1 ORDER BY id').all();
  const partialRename = partialDb.prepare('UPDATE run_cards SET name=? WHERE id=?');
  partialIds.forEach((row, index) => partialRename.run(index < 2 ? 'A' : 'B', row.id));
  partialDb.prepare('UPDATE run_pages SET unique_count=3, duplicate_names=? WHERE run_id=1').run(JSON.stringify([{ name: 'A', copies: 2 }]));
  partialDb.close();
  const partialQuality = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-run'], { cwd: ROOT, env: { ...process.env, EMS_DB_PATH: corruptDb, EMS_QUALITY_OUT: corruptOut }, encoding: 'utf8' });
  const partialReport = JSON.parse(fs.readFileSync(path.join(corruptOut, 'quality_report_run1.json'), 'utf8'));
  assert(partialQuality.status === 2 && partialReport.summary.invalid_pages === 1, '每个重复 raw name 组都必须有完整 duplicate claim');

  const mutationDb = new Database(corruptDb);
  const cardIds = mutationDb.prepare('SELECT id FROM run_cards WHERE run_id=1 ORDER BY id').all();
  const rename = mutationDb.prepare('UPDATE run_cards SET name=? WHERE id=?');
  cardIds.forEach((row, index) => rename.run(`room#${index + 1}`, row.id));
  mutationDb.prepare("UPDATE run_pages SET unique_count=2, duplicate_names=? WHERE run_id=1").run(JSON.stringify([{ name: 'room', copies: 3 }]));
  mutationDb.close();
  const extraNumberedQuality = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-run'], { cwd: ROOT, env: { ...process.env, EMS_DB_PATH: corruptDb, EMS_QUALITY_OUT: corruptOut }, encoding: 'utf8' });
  const extraNumberedReport = JSON.parse(fs.readFileSync(path.join(corruptOut, 'quality_report_run1.json'), 'utf8'));
  assert(extraNumberedQuality.status === 2 && extraNumberedReport.summary.invalid_pages === 1, 'copies=3 不得吞掉同组额外 room#4 成员');

  const mixedDb = new Database(corruptDb);
  const mixedIds = mixedDb.prepare('SELECT id FROM run_cards WHERE run_id=1 ORDER BY id').all();
  const mixedRename = mixedDb.prepare('UPDATE run_cards SET name=? WHERE id=?');
  mixedIds.forEach((row, index) => mixedRename.run(index < 3 ? 'room' : 'room#1', row.id));
  mixedDb.close();
  const mixedQuality = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-run'], { cwd: ROOT, env: { ...process.env, EMS_DB_PATH: corruptDb, EMS_QUALITY_OUT: corruptOut }, encoding: 'utf8' });
  const mixedReport = JSON.parse(fs.readFileSync(path.join(corruptOut, 'quality_report_run1.json'), 'utf8'));
  assert(mixedQuality.status === 2 && mixedReport.summary.invalid_pages === 1, '旧式 raw base 行不得与同组编号变体混合');
  fs.rmSync(tmp, { recursive: true, force: true });
}

function testTimestampNormalizationOnImport() {
  const tmp = path.join(ROOT, 'out', 'self-test-timestamps');
  fs.rmSync(tmp, { recursive: true, force: true });
  fs.mkdirSync(tmp, { recursive: true });
  const jsonPath = path.join(tmp, 'enum-legacy-timestamps.json');
  const dbPath = path.join(tmp, 'ac-timestamps.db');
  writeJson(jsonPath, {
    completedAt: '2026-08-31T03:00:00',
    buildings: [{
      building: '1号',
      completedAt: '2026-08-31T03:01:00+08:00',
      subAreas: [{
        idx: 0,
        text: '1F',
        floor: 1,
        x: 10,
        y: 20,
        pages: [{
          page: 'default',
          collectedAt: '2026-08-31T03:02:00',
          cards: [{ name: '1F-A-KT', switch: 'OFF', mode: '制冷', indoor: '26', setTemp: '25', fan: '中', comm: '关机' }],
        }],
      }],
    }],
  });

  runImport(jsonPath, dbPath, [], { EMS_RUN_STARTED_AT: '2026-08-31T02:59:00+08:00' });
  const db = new Database(dbPath, { readonly: true });
  const values = db.prepare(`
    SELECT r.started_at, r.completed_at, r.imported_at, b.updated_at, p.collected_at, rp.collected_at AS run_collected_at
    FROM collection_runs r
    JOIN buildings b ON b.building = '1号'
    JOIN pages p ON p.id = 1
    JOIN run_pages rp ON rp.source_page_id = p.id
    ORDER BY r.id DESC
    LIMIT 1
  `).get();
  db.close();
  fs.rmSync(tmp, { recursive: true, force: true });

  assert(values.started_at === expectedLocalTimestamp('2026-08-30T18:59:00Z'), '采集开始时间必须使用本机时区保存');
  assert(Date.parse(values.completed_at) >= Date.parse(values.updated_at), '批次完成时间必须不早于楼栋采集完成时间');
  assert(Date.parse(values.imported_at) >= Date.parse(values.completed_at), '导入时间必须不早于批次完成时间');
  assert(/(?:Z|[+-][0-9]{2}:?[0-9]{2})$/i.test(values.imported_at), '导入时间必须包含明确时区');
  assert(values.updated_at === expectedLocalTimestamp('2026-08-30T19:01:00Z'), '+08:00 楼栋时间必须转换为本机时区');
  assert(values.collected_at === expectedLocalTimestamp('2026-08-31T03:02:00Z'), '无时区页面时间必须按 UTC 兼容解析后使用本机时区保存');
  assert(values.run_collected_at === values.collected_at, '历史页面快照必须保留归一化后的采集时间');
}

function testTimestampEnvironmentParsing() {
  const localTimestamp = '2026-08-31T03:00:00.000+08:00';
  const expected = Date.parse(localTimestamp);

  assert(parseTimestampMillis(localTimestamp) === expected, '带本机偏移的采集开始时间必须可用于计算耗时');
  assert(parseTimestampMillis(String(expected)) === expected, '历史数字形式的采集开始时间必须保持兼容');
  assert(parseTimestampMillis('') === 0, '空采集开始时间必须按未提供处理');
}

function testSqliteDefaultTimestampUsesLocalOffset() {
  const db = new Database(':memory:');
  ensureHistorySchema(db);
  db.prepare("INSERT INTO floor_catalog (building, floor_label) VALUES ('1号', '1F')").run();
  const row = db.prepare('SELECT created_at, updated_at FROM floor_catalog LIMIT 1').get();
  db.close();

  const localOffset = expectedLocalTimestamp(new Date()).slice(-6);
  assert(row.created_at.endsWith(localOffset), 'SQLite 默认创建时间必须使用本机时区偏移');
  assert(row.updated_at.endsWith(localOffset), 'SQLite 默认更新时间必须使用本机时区偏移');
}

function testTimestampNormalizationOnRestore() {
  const tmp = path.join(ROOT, 'out', 'self-test-restore-timestamps');
  fs.rmSync(tmp, { recursive: true, force: true });
  fs.mkdirSync(tmp, { recursive: true });
  const jsonPath = path.join(tmp, 'enum.json');
  const dbPath = path.join(tmp, 'ac-restore-timestamps.db');
  writeJson(jsonPath, {
    completedAt: '2026-08-31T03:00:00.000Z',
    buildings: [{
      building: '1号',
      completedAt: '2026-08-31T03:01:00.000Z',
      subAreas: [{
        idx: 0,
        text: '1F',
        floor: 1,
        x: 10,
        y: 20,
        pages: [{
          page: 'default',
          collectedAt: '2026-08-31T03:02:00.000Z',
          cards: [{ name: '1F-A-KT', switch: 'OFF', mode: '制冷', indoor: '26', setTemp: '25', fan: '中', comm: '关机' }],
        }],
      }],
    }],
  });

  runImport(jsonPath, dbPath);
  const db = new Database(dbPath);
  db.prepare("UPDATE collection_runs SET completed_at = '2026-08-31T03:00:00' WHERE id = 1").run();
  db.prepare("UPDATE run_buildings SET updated_at = '2026-08-31T03:01:00+08:00' WHERE run_id = 1").run();
  db.prepare("UPDATE run_pages SET collected_at = '2026-08-31T03:02:00' WHERE run_id = 1").run();
  const restored = restoreCurrentFromRun(db, 1);
  const values = db.prepare(`
    SELECT b.updated_at, p.collected_at
    FROM buildings b
    JOIN sub_areas sa ON sa.building = b.building
    JOIN pages p ON p.sub_area_id = sa.id
    LIMIT 1
  `).get();
  db.close();
  fs.rmSync(tmp, { recursive: true, force: true });

  assert(restored.completed_at === expectedLocalTimestamp('2026-08-31T03:00:00Z'), '恢复结果时间必须使用本机时区');
  assert(values.updated_at === expectedLocalTimestamp('2026-08-30T19:01:00Z'), '恢复后的楼栋时间必须转换为本机时区');
  assert(values.collected_at === expectedLocalTimestamp('2026-08-31T03:02:00Z'), '恢复后的页面时间必须按 UTC 兼容解析后使用本机时区');
}

function testQualityReportFailsInvalidFields() {
  const tmp = path.join(ROOT, 'out', 'self-test-quality');
  fs.rmSync(tmp, { recursive: true, force: true });
  fs.mkdirSync(tmp, { recursive: true });
  const jsonPath = path.join(tmp, 'enum-invalid.json');
  const dbPath = path.join(tmp, 'ac-quality.db');
  const qualityOut = path.join(tmp, 'quality');

  writeJson(jsonPath, {
    buildings: [
      {
        building: '1号',
        menuClicked: '1号楼',
        subAreas: [
          {
            idx: 0,
            text: '1F',
            floor: 1,
            x: 10,
            y: 20,
            pages: [
              {
                page: 'default',
                layout: 'grid',
                qualityReason: 'quality_pass',
                cards: [
                  {
                    name: '1-0101-KT',
                    switch: 'ON',
                    mode: '-',
                    indoor: '-1615.5',
                    setTemp: '3301.4',
                    fan: '-',
                    indicator: '56f45bb314d74cc8da6c6c8e5942d08d.png',
                    comm: '开机',
                  },
                ],
              },
            ],
          },
        ],
      },
    ],
  });

  runImport(jsonPath, dbPath);
  const latestSelectionDb = new Database(dbPath);
  latestSelectionDb.prepare(`
    INSERT INTO collection_runs
      (run_key, completed_at, imported_at, status, scope, buildings, card_count)
    VALUES (?, ?, ?, ?, ?, ?, ?)
  `).run('failed-newer', '2099-01-01T00:00:00+08:00', '2099-01-01T00:00:00+08:00', 'failed', 'full', '[]', 0);
  latestSelectionDb.close();
  const result = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-imported'], {
    cwd: ROOT,
    env: { ...process.env, EMS_DB_PATH: dbPath, EMS_QUALITY_OUT: qualityOut },
    encoding: 'utf8',
  });
  if (result.status !== 2) {
    throw new Error(`quality-report.js should fail invalid fields\nSTDOUT:\n${result.stdout}\nSTDERR:\n${result.stderr}`);
  }
  const report = JSON.parse(fs.readFileSync(path.join(qualityOut, 'quality_report_run1.json'), 'utf8'));
  const latestAlias = JSON.parse(fs.readFileSync(path.join(qualityOut, 'quality_report.json'), 'utf8'));
  const firstQualityDb = new Database(dbPath, { readonly: true });
  const firstQualityRun = firstQualityDb.prepare('SELECT status FROM collection_runs WHERE id = ?').get(report.run_id);
  firstQualityDb.close();
  assert(latestAlias.run_id === report.run_id, 'latest-run 质量审计必须同步 canonical 报告供原生界面刷新');
  assert(firstQualityRun.status === 'needs_review', '存在阻断质量问题的批次必须标记为需复核');
  assert(report.summary.invalid_card_fields === 1, '质量报告应标记异常/缺失卡字段');
  assert(report.summary.active_field_incomplete_pages === 1, '质量报告应标记开关机页字段不完整');
  assert(report.run_id === 1, 'latest-imported 不得选择 failed 批次');
  assert(/(?:Z|[+-][0-9]{2}:[0-9]{2})$/i.test(report.generated_at_local), '质量报告时间必须包含本机时区偏移');

  const knownPath = path.join(tmp, 'known-findings.json');
  writeJson(knownPath, {
    findings: [
      {
        id: 'self-test-pending-device',
        type: 'device_invalid_fields',
        status: 'blocking_pending_source_check',
        building: '1号',
        floor: 1,
        subArea: '1F',
        page: 'default',
        device: '1-0101-KT',
      },
    ],
  });
  const pendingOut = path.join(tmp, 'quality-pending');
  const pending = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-run'], {
    cwd: ROOT,
    env: { ...process.env, EMS_DB_PATH: dbPath, EMS_QUALITY_OUT: pendingOut, EMS_QUALITY_KNOWN_FINDINGS: knownPath },
    encoding: 'utf8',
  });
  if (pending.status !== 2) {
    throw new Error(`pending known finding must remain blocking\nSTDOUT:\n${pending.stdout}\nSTDERR:\n${pending.stderr}`);
  }
  const pendingReport = JSON.parse(fs.readFileSync(path.join(pendingOut, 'quality_report_run1.json'), 'utf8'));
  assert(pendingReport.summary.known_findings === 2, '待复核已知异常应同时标注卡片和页面问题');
  assert(pendingReport.summary.invalid_card_fields === 1, '待复核已知异常不能隐藏异常卡字段');
  assert(pendingReport.summary.active_field_incomplete_pages === 1, '待复核已知异常不能隐藏页面字段不完整');

  const accepted = JSON.parse(fs.readFileSync(knownPath, 'utf8'));
  accepted.findings[0].status = 'accepted_ems_source_defect';
  writeJson(knownPath, accepted);
  const acceptedOut = path.join(tmp, 'quality-accepted');
  const acceptedResult = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-run'], {
    cwd: ROOT,
    env: { ...process.env, EMS_DB_PATH: dbPath, EMS_QUALITY_OUT: acceptedOut, EMS_QUALITY_KNOWN_FINDINGS: knownPath },
    encoding: 'utf8',
  });
  if (acceptedResult.status !== 0) {
    throw new Error(`accepted known finding test should pass without a fixed data baseline\nSTDOUT:\n${acceptedResult.stdout}\nSTDERR:\n${acceptedResult.stderr}`);
  }
  const acceptedReport = JSON.parse(fs.readFileSync(path.join(acceptedOut, 'quality_report_run1.json'), 'utf8'));
  assert(acceptedReport.summary.invalid_card_fields === 0, '已接受 EMS 源异常应从异常卡字段阻断项移出');
  assert(acceptedReport.summary.active_field_incomplete_pages === 0, '已接受 EMS 源异常应从页面字段不完整阻断项移出');
  assert(acceptedReport.summary.known_findings === 2, '已接受 EMS 源异常仍应在报告中可见');
  assert(!acceptedReport.issues.some(issue => issue.code === 'baseline_delta'), '质量报告不得把历史数量差异生成阻断项');
  assert(!Object.prototype.hasOwnProperty.call(acceptedReport.buildings[0], 'baseline_cards'), '楼栋报告不得输出固定卡数基准');
  const acceptedDb = new Database(dbPath, { readonly: true });
  const acceptedStatus = acceptedDb.prepare('SELECT status FROM collection_runs WHERE id = ?').get(report.run_id);
  acceptedDb.close();
  assert(acceptedStatus.status === 'completed', '没有阻断问题的批次不得因卡数变化被降级');

  fs.rmSync(tmp, { recursive: true, force: true });
}

function testQualityReportUsesNativePlaceholderCode() {
  const tmp = path.join(ROOT, 'out', 'self-test-quality-placeholder');
  fs.rmSync(tmp, { recursive: true, force: true });
  fs.mkdirSync(tmp, { recursive: true });
  const jsonPath = path.join(tmp, 'enum-placeholder.json');
  const dbPath = path.join(tmp, 'ac-placeholder.db');
  const qualityOut = path.join(tmp, 'quality');

  writeJson(jsonPath, {
    buildings: [{
      building: '1号',
      menuClicked: '1号楼',
      subAreas: [{
        idx: 0,
        text: '1F',
        floor: 1,
        x: 10,
        y: 20,
        pages: [{
          page: 'default',
          layout: 'grid',
          qualityReason: 'quality_pass',
          cards: [{
            name: '0-0001-KT#2',
            switch: 'OFF',
            mode: '制冷',
            indoor: '26',
            setTemp: '25',
            fan: '中',
            indicator: '3bdc38eda0ae77f26807b2b6cdde4456.png',
            comm: '关机',
          }],
        }],
      }],
    }],
  });

  runImport(jsonPath, dbPath);
  const result = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-run'], {
    cwd: ROOT,
    env: { ...process.env, EMS_DB_PATH: dbPath, EMS_QUALITY_OUT: qualityOut },
    encoding: 'utf8',
  });
  if (result.status !== 2) {
    throw new Error(`quality-report.js should block placeholder cards\nSTDOUT:\n${result.stdout}\nSTDERR:\n${result.stderr}`);
  }
  const report = JSON.parse(fs.readFileSync(path.join(qualityOut, 'quality_report_run1.json'), 'utf8'));
  assert(report.summary.placeholder_cards === 1, '质量报告应标记占位符卡片');
  assert(report.issues.some(issue => issue.code === 'placeholder_cards'), '占位符问题码必须与原生质量门禁一致');
  fs.rmSync(tmp, { recursive: true, force: true });
}

function testNativeOnlyContract() {
  const retained = [
    'src/enumerate.js',
    'scripts/import.js',
    'scripts/validate-enum.js',
    'scripts/quality-report.js',
    'scripts/audit-realtime-data.js',
    'scripts/collect-realtime-all-batch.js',
    'scripts/field-e2e.ps1',
    'native/src/EmsScout.Desktop/EmsScout.Desktop.csproj',
  ];
  const retired = [
    path.join('src', 'panel', 'server.js'),
    path.join('web', 'panel', 'index.html'),
    path.join('electron', 'main.js'),
    path.join('src', 'tui', 'actions.js'),
    path.join('src', 'collect.js'),
    path.join('native', 'src', 'EmsScout.Legacy', 'EmsScout.Legacy.csproj'),
  ];
  for (const relative of retained) assert(fs.existsSync(path.join(ROOT, relative)), `Native-only retained path missing: ${relative}`);
  for (const relative of retired) assert(!fs.existsSync(path.join(ROOT, relative)), `Retired architecture still exists: ${relative}`);
  const packageJson = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8'));
  const scriptNames = Object.keys(packageJson.scripts || {});
  const scriptText = Object.values(packageJson.scripts || {}).join('\n');
  assert(!scriptNames.some(name => name.startsWith('legacy')), 'package scripts must not expose legacy commands');
  assert(!scriptText.includes('electron') && !scriptText.includes('src/collect.js') && !scriptText.includes('dump-aircons.js') && !scriptText.includes('dump-public.js'), 'package scripts must not expose retired entry points');
  assert(!packageJson.main && !packageJson.build, 'package metadata must not define a legacy desktop application');
  assert(!fs.existsSync(path.join(ROOT, 'src', 'data-history.js')) || !fs.readFileSync(path.join(ROOT, 'src', 'data-history.js'), 'utf8').includes(path.join('src', 'panel')), 'data history must not depend on the panel');
}

function testRealtimeBatchProgressContract() {
  const batchScript = path.join(ROOT, 'scripts', 'collect-building-realtime-batch.js');
  const allScript = fs.readFileSync(path.join(ROOT, 'scripts', 'collect-realtime-all-batch.js'), 'utf8');
  const { composeOverallDone } = require(batchScript);
  assert(composeOverallDone(1495, 1096) === 2591, 'overall progress must add completed buildings exactly once');
  assert(
    allScript.includes('EMS_OVERALL_DONE_BASE: String(results.reduce((acc, r) => acc + (r.summary.devices || 0), 0))'),
    'parent must pass completed-building total as the child progress base',
  );
  assert(
    !allScript.includes('EMS_OVERALL_DONE_BASE: String(_overallDoneBase + results.reduce'),
    'parent must not add the completed-building total twice',
  );

  const result = spawnSync(process.execPath, ['-e', [
    'const { withHardTimeout } = require(' + JSON.stringify(batchScript) + ');',
    "withHardTimeout(() => new Promise(() => {}), 20, 'contract').then(() => process.exit(2)).catch(err => {",
    "  if (!String(err.message).includes('contract timed out')) process.exit(3);",
    '  process.exit(0);',
    '});',
  ].join('\n')], { cwd: ROOT, encoding: 'utf8' });
  assert(result.status === 0, 'batch hard timeout contract failed: ' + (result.stderr || result.stdout));
}

function main() {
  testRules();
  testEnumeratorPageNormalizationContract();
  testRealtimeQualityContract();
  testCollectionValidationBlocksIncompleteResults();
  testPartialImport();
  testImportRejectsInvalidPageBeforeMutation();
  testValidationEnabledImportLabelsDuplicatesAndPreservesUnknown();
  testTimestampEnvironmentParsing();
  testSqliteDefaultTimestampUsesLocalOffset();
  testTimestampNormalizationOnImport();
  testTimestampNormalizationOnRestore();
  testQualityReportFailsInvalidFields();
  testQualityReportUsesNativePlaceholderCode();
  testQualityReportAuditsPageStructureAndUnknownComm();
  testQualityReportRejectsMalformedBmPage();
  testQualityReportAuditsStoredSourceIdentityEvidence();
  testNativeOnlyContract();
  testRealtimeBatchProgressContract();
  console.log('Self-test passed.');
}

if (require.main === module) {
  main();
}
