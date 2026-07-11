#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const Database = require('better-sqlite3');
const { checkCardQuality, classifyAreaType, getZone, classifyPersistentDeviceAnomalyPage, normalizeKnownSourceDefects, classifyKnownMissingIndicatorPage, isAcceptedCaptureQualityReason } = require('../src/rules');
const { BLDG_ORDER } = require('../src/rules');
const { createRealtimeService } = require('../src/panel/services/realtime-service');
const { createReconcileService } = require('../src/panel/services/reconcile-service');

const ROOT = path.join(__dirname, '..');

function assert(cond, msg) {
  if (!cond) throw new Error(msg);
}

function runImport(jsonPath, dbPath, args = []) {
  const result = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'import.js'), ...args], {
    cwd: ROOT,
    env: { ...process.env, EMS_JSON_PATH: jsonPath, EMS_DB_PATH: dbPath, EMS_SKIP_ENUM_VALIDATION: '1' },
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
  const normalizedKnownMissing = normalizeKnownSourceDefects(knownMissingIndicator);

  assert(!checkCardQuality(loaded34).ok, '3/4号默认值统一时应触发模板检测');
  assert(!checkCardQuality(notReady34).ok, '3/4号默认值且 comm/switch 未完整时应失败');
  assert(!checkCardQuality(placeholder).ok, '0-0001-KT 占位符应失败');
  assert(checkCardQuality(realMixed).ok, '非模板真实页且通讯完整时应通过');
  assert(!checkCardQuality(missingComm).ok, '任一卡缺通讯/indicator 时应失败');
  assert(!checkCardQuality(invalidTemp).ok, '异常温度和缺失模式/风速应失败');
  assert(!checkCardQuality(missingActiveFields).ok, '开机/关机设备字段缺失应失败');
  assert(!checkCardQuality(offlineTemplate).ok, '全离线默认模板不应作为 quality_pass 通过');
  assert(isAcceptedCaptureQualityReason('offline_template_stable'), '稳定全离线模板应通过最终采集门槛');
  assert(!isAcceptedCaptureQualityReason('all_offline'), '全离线不能绕过页面质量证据');
  assert(!isAcceptedCaptureQualityReason('stable_partial'), '通讯状态缺失的稳定部分页必须继续阻断');
  assert(isAcceptedCaptureQualityReason('device_anomalies_preserved'), '稳定的有界设备异常应通过最终采集门槛');
  assert(isAcceptedCaptureQualityReason('known_source_indicator_missing'), '精确登记的 EMS indicator 缺失设备应通过最终采集门槛');
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
  assert(!checkCardQuality(realMixed.slice(0, 1), { rawCount: 20, uniqueCount: 1 }).ok, 'raw 多但 unique 极少的重复塌缩页应失败');
  assert(checkCardQuality(realMixed.slice(0, 7), { rawCount: 10, uniqueCount: 7 }).ok, '轻微重复渲染页应按唯一设备放行');
  assert(classifyAreaType('3F-WSJ-KT-1', 'grid') === '公区', 'WSJ 应识别为公区');
  assert(classifyAreaType('QL-101-KT', 'grid') === '非公区', 'QL-NNN 应识别为非公区');
  assert(classifyAreaType('ANY', 'group') === '公区', 'group layout 应识别为公区');
  assert(getZone(695, '5号') === 2, '5号 x=695 应为 C座 zone');
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
    buildings: [
      { building: '2号', menuClicked: '2号楼', subAreas: [{ idx: 0, text: '2F', floor: 2, x: 50, y: 60, pages: [{ page: 'default', layout: 'grid', cards: [
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
    SELECT p.count, p.raw_count, p.unique_count, p.duplicate_names
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
  db.close();
  fs.rmSync(tmp, { recursive: true, force: true });

  assert(rows.length === 2, '部分导入后应保留未选楼栋');
  assert(rows[0].building === '1号' && rows[0].cards === 1 && rows[0].names === '1F-A-KT', '1号数据应保留');
  assert(rows[1].building === '2号' && rows[1].cards === 3 && rows[1].names.includes('2F-B-KT'), '2号数据应被替换');
  assert(pageMeta.count === 3 && pageMeta.raw_count === 4 && pageMeta.unique_count === 3, '页面重复渲染元数据应保留，cards 表应按唯一卡入库');
  assert(pageMeta.duplicate_names.includes('2F-B-KT'), '重复渲染设备名应入库');
  assert(latestRun.card_count === 3 && latestRun.on_count === 1 && latestRun.off_count === 1 && latestRun.offline_count === 0 && latestRun.unknown_count === 1, 'run 统计必须按 comm 区分状态，switch=OFF 不得掩盖未知通讯');
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
  const result = spawnSync(process.execPath, [path.join(ROOT, 'scripts', 'quality-report.js'), '--run-id=latest-run'], {
    cwd: ROOT,
    env: { ...process.env, EMS_DB_PATH: dbPath, EMS_QUALITY_OUT: qualityOut },
    encoding: 'utf8',
  });
  if (result.status !== 2) {
    throw new Error(`quality-report.js should fail invalid fields\nSTDOUT:\n${result.stdout}\nSTDERR:\n${result.stderr}`);
  }
  const report = JSON.parse(fs.readFileSync(path.join(qualityOut, 'quality_report_run1.json'), 'utf8'));
  assert(report.summary.invalid_card_fields === 1, '质量报告应标记异常/缺失卡字段');
  assert(report.summary.active_field_incomplete_pages === 1, '质量报告应标记开关机页字段不完整');

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
  if (acceptedResult.status !== 2) {
    throw new Error(`accepted known finding test should still fail on baseline delta only\nSTDOUT:\n${acceptedResult.stdout}\nSTDERR:\n${acceptedResult.stderr}`);
  }
  const acceptedReport = JSON.parse(fs.readFileSync(path.join(acceptedOut, 'quality_report_run1.json'), 'utf8'));
  assert(acceptedReport.summary.invalid_card_fields === 0, '已接受 EMS 源异常应从异常卡字段阻断项移出');
  assert(acceptedReport.summary.active_field_incomplete_pages === 0, '已接受 EMS 源异常应从页面字段不完整阻断项移出');
  assert(acceptedReport.summary.known_findings === 2, '已接受 EMS 源异常仍应在报告中可见');

  fs.rmSync(tmp, { recursive: true, force: true });
}

function testReconcileServiceShape() {
  const dbPath = path.join(ROOT, 'out', 'ac.db');
  const realtimeLatestFiles = BLDG_ORDER.map(b => path.join(ROOT, 'out', `realtime_${b}_latest.json`));
  if (!fs.existsSync(dbPath) || realtimeLatestFiles.some(file => !fs.existsSync(file))) return;

  const openReadonlyDb = () => new Database(dbPath, { readonly: true });
  const outDir = path.join(ROOT, 'out');
  const service = createReconcileService({
    root: ROOT,
    outDir,
    openReadonlyDb,
    realtimeService: createRealtimeService({ root: ROOT, outDir, buildings: BLDG_ORDER }),
  });
  const result = service.diff();
  assert(result && result.summary && Array.isArray(result.diffItems), '对账服务应返回 summary 和 diffItems');
  assert(result.summary.dbCount === 6568, '当前 DB 基线应为 6568 张卡');
  assert(result.summary.realtimeCount === 6575, '当前实时详情应为 6575 行');
  assert(result.summary.diff === 7, '当前实时详情与 DB 基线差异应为 7');
  assert(result.diffItems.length === 7, '对账结果必须逐设备归因到 7 条差异');
  assert(result.summary.ruleVersion === 'reconcile-v1.0.0', '对账结果应记录规则版本');
  const types = new Set(result.diffItems.map(item => item.type));
  assert(types.has('DUPLICATE_RENDER'), '对账结果应包含重复渲染归因');
  assert(types.has('VIRTUAL_OVERRIDE'), '对账结果应包含虚拟纳管归因');
  assert(types.has('MATCH_FAILED'), '对账结果应包含匹配失败归因');
  assert(result.diffItems.every(item => item.key && item.reason && typeof item.confidence === 'number' && item.source), '每条差异应可追溯');
  assert(result.diffItems.every(item => item.ruleVersion === 'reconcile-v1.0.0'), '每条差异应记录 ruleVersion');
  assert(result.diffItems.every(item => item.evidence && Object.prototype.hasOwnProperty.call(item.evidence, 'db') && Object.prototype.hasOwnProperty.call(item.evidence, 'realtime')), '每条差异应包含结构化 evidence');
  assert(result.diffItems.every(item => Array.isArray(item.decisionPath) && item.decisionPath.length >= 4), '每条差异应包含决策链');

  const replay = service.replay('', 'reconcile-v1.0.0');
  assert(JSON.stringify(result.diffItems.map(i => [i.key, i.type])) === JSON.stringify(replay.diffItems.map(i => [i.key, i.type])), 'replay 应可确定性重放当前对账结果');
  assert(typeof service.exportExcel === 'undefined', '对账服务不应暴露独立 Excel 导出入口');
  const audit = service.audit({ building: '2号' });
  assert(audit && audit.summary && audit.drift, '对账审计应保留摘要和漂移信息');
}

function testLegacyWebExportRemoved() {
  const serverJs = fs.readFileSync(path.join(ROOT, 'src', 'panel', 'server.js'), 'utf8');
  const reportsRoutes = fs.readFileSync(path.join(ROOT, 'src', 'panel', 'routes', 'reports-routes.js'), 'utf8');
  const indexHtml = fs.readFileSync(path.join(ROOT, 'web', 'panel', 'index.html'), 'utf8');
  const appJs = fs.readFileSync(path.join(ROOT, 'web', 'panel', 'app.js'), 'utf8');
  const stylesCss = fs.readFileSync(path.join(ROOT, 'web', 'panel', 'styles.css'), 'utf8');
  const cardsRoutes = fs.readFileSync(path.join(ROOT, 'src', 'panel', 'routes', 'cards-routes.js'), 'utf8');
  const cardsService = fs.readFileSync(path.join(ROOT, 'src', 'panel', 'services', 'cards-service.js'), 'utf8');

  assert(!serverJs.includes("require('xlsx')"), '旧 Web 面板后端不应依赖 xlsx 生成导出文件');
  assert(!serverJs.includes('function exportCards'), 'server.js 不应保留旧 Web Excel 生成器');
  assert(!serverJs.includes('XLSX.writeFile'), 'server.js 不应写出旧 Web Excel 文件');
  assert(!serverJs.includes('panel-export'), 'server.js 不应引用旧 Web 导出目录');
  assert(!serverJs.includes('listReports'), 'server.js 不应保留旧导出历史索引');
  assert(!serverJs.includes('legacyReportExportsEnabled'), 'server.js 不应保留旧 Web 导出 gate');
  assert(!serverJs.includes('export_dir'), 'About API 不应返回旧 Web 导出目录');
  assert(!serverJs.includes('recent_exports'), 'About API 不应返回旧 Web 导出历史');
  assert(!reportsRoutes.includes('/api/files/download'), 'reports route 不应暴露旧 Web 文件下载入口');
  assert(!reportsRoutes.includes('/api/reports'), 'reports route 不应暴露旧 Web 导出历史入口');
  assert(!indexHtml.includes('exportFilterBtn'), '旧 Web 面板不应显示导出按钮');
  assert(!indexHtml.includes('健康态') && !indexHtml.includes('点位质量') && !indexHtml.includes('异常提示'), '旧 Web 面板不应保留旧数据表头');
  assert(!appJs.includes('exportCurrentFilter'), '旧 Web 面板不应保留导出函数');
  assert(!appJs.includes('/api/cards/export'), '旧 Web 面板前端不应调用旧导出 API');
  assert(!appJs.includes('reportRows'), '旧 Web 面板不应展示旧导出历史表');
  assert(!appJs.includes('DeviceTable.renderRow'), '旧 Web 面板不应继续渲染旧数据表行');
  assert(!appJs.includes('健康态') && !appJs.includes('点位质量') && !appJs.includes('异常提示'), '旧 Web 面板脚本不应保留旧数据表文案');
  assert(!stylesCss.includes('report-table'), '旧 Web 面板不应保留旧导出历史表样式');
  assert(!stylesCss.includes('realtime-wide') && !stylesCss.includes('actions-col'), '旧 Web 面板不应保留旧数据表宽表/操作列样式');
  assert(cardsRoutes.includes('旧 Web 面板 Excel 导出已移除'), '旧导出 API 应返回移除提示');
  assert(!cardsRoutes.includes('cardsService.exportCards'), '旧导出 API 不应调用导出服务');
  assert(!cardsService.includes('exportCards'), 'cards service 不应暴露旧导出能力');
}

function main() {
  testRules();
  testPartialImport();
  testQualityReportFailsInvalidFields();
  testReconcileServiceShape();
  testLegacyWebExportRemoved();
  console.log('Self-test passed.');
}

if (require.main === module) {
  main();
}
