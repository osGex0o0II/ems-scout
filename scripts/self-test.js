#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const Database = require('better-sqlite3');
const { checkCardQuality, classifyAreaType, getZone, assessBuildingIdentity, labelSamePageDuplicateCards, classifyPersistentDeviceAnomalyPage, normalizeCardValues, normalizeKnownSourceDefects, classifyKnownMissingIndicatorPage, isAcceptedCaptureQualityReason } = require('../src/rules');
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
  assert(checkCardQuality(realMixed).ok, '非模板真实页且通讯完整时应通过');
  assert(!checkCardQuality(missingComm).ok, '任一卡缺通讯/indicator 时应失败');
  assert(!checkCardQuality(invalidTemp).ok, '异常温度和缺失模式/风速应失败');
  assert(!checkCardQuality(missingActiveFields).ok, '开机/关机设备字段缺失应失败');
  assert(!checkCardQuality(offlineTemplate).ok, '全离线默认模板不应作为 quality_pass 通过');
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
  assert(pageMeta.count === 4 && pageMeta.raw_count === 4 && pageMeta.unique_count === 4, '同页重名卡片编号后应作为独立页面卡片入库');
  assert(pageMeta.duplicate_names.includes('2F-B-KT'), '重复渲染设备名应入库');
  assert(pageMeta.collected_at === expectedLocalTimestamp('2026-07-12T00:20:15.000Z'), '页面必须保存实际通过采集质量门槛的时间');
  assert(runPage.collected_at === pageMeta.collected_at, '历史批次必须保留页面采集时间');
  assert(latestRun.card_count === 4 && latestRun.on_count === 2 && latestRun.off_count === 1 && latestRun.offline_count === 0 && latestRun.unknown_count === 1, 'run 统计必须按 comm 区分状态，switch=OFF 不得掩盖未知通讯');
  assert(building2.updated_at === expectedLocalTimestamp('2026-07-12T00:30:00.000Z'), '部分导入必须保留楼栋独立采集时间，不能套用顶层时间');
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
            name: '0-0001-KT',
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
  testRealtimeQualityContract();
  testCollectionValidationBlocksIncompleteResults();
  testPartialImport();
  testTimestampEnvironmentParsing();
  testSqliteDefaultTimestampUsesLocalOffset();
  testTimestampNormalizationOnImport();
  testTimestampNormalizationOnRestore();
  testQualityReportFailsInvalidFields();
  testQualityReportUsesNativePlaceholderCode();
  testNativeOnlyContract();
  testRealtimeBatchProgressContract();
  console.log('Self-test passed.');
}

if (require.main === module) {
  main();
}
