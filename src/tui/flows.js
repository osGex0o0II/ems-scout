const { BLDG_ORDER, BLDG_META } = require('../rules');
const { SEP, prompt, clearScreen, confirmScreen } = require('./ui');
const { mainMenu, buildingSelectMenu, settingsMenu } = require('./menus');
const { load: loadSettings, toCliArgs } = require('./settings');
const {
  BLDG_ESTIMATE,
  detectEdgeMode,
  parseMin,
  saveLastCollect,
  loadQualityReport,
  doEnumeration,
  doImport,
  dbStatus,
  loadDataForOverview,
  loadBuildingStats,
  qualityLine,
  qualityBadge,
  deltaLabel,
} = require('./actions');

function showOverview(data) {
  if (!data) { console.log('  数据库不存在或为空。'); return; }
  const q = loadQualityReport();
  const total = data.length;
  const on = data.filter(r => r.switch === 'ON').length;
  const off = data.filter(r => r.switch === 'OFF').length;
  const offline = data.filter(r => r.switch === '-' || r.comm === '离线').length;
  const pub = data.filter(r => r.pub).length;
  const nonPub = total - pub;
  console.log(`  设备总数: ${total}`);
  console.log(`  开机:     ${on}`);
  console.log(`  关机:     ${off}`);
  console.log(`  离线:     ${offline}`);
  console.log(`  公区:     ${pub}`);
  console.log(`  非公区:   ${nonPub}`);
  console.log(`  ${qualityBadge(q)}`);
  console.log('');
  console.log('  楼栋   总数    开机    关机    离线    基准');
  console.log('  ----  ------  ------  ------  ------  ------');
  for (const b of BLDG_ORDER) {
    const meta = BLDG_META[b];
    const bi = data.filter(r => r.building === b);
    const bon = bi.filter(r => r.switch === 'ON').length;
    const boff = bi.filter(r => r.switch === 'OFF').length;
    const bof = bi.filter(r => r.switch === '-' || r.comm === '离线').length;
    const delta = bi.length - meta.baselineCards;
    console.log(`  ${b.padEnd(4)}  ${String(bi.length).padStart(6)}  ${String(bon).padStart(6)}  ${String(boff).padStart(6)}  ${String(bof).padStart(6)}  ${deltaLabel(delta).padStart(6)}`);
  }
}

async function overviewFlow() {
  clearScreen();
  console.log('\n  数据概览\n');
  const data = loadDataForOverview();
  showOverview(data);
  console.log('');
  SEP();
  await prompt('\n  按 Enter 返回...');
}

async function collectFlow(state) {
  const selB = [];
  while (true) {
    const s = await buildingSelectMenu(selB, loadBuildingStats() || []);
    if (s === 'Q') break;
    if (s === 'C') {
      if (selB.length === 0) { console.log('\n  至少选一栋！'); await prompt('\n  按 Enter 继续...'); continue; }
      const est = selB.reduce((sum, b) => sum + parseMin(BLDG_ESTIMATE[b].time), 0);
      const cfm = await confirmScreen('采集确认', [
        '将执行: 采集 → 导入 → 质量审计',
        '楼栋: ' + selB.join(' '),
        '预计: ~' + est.toFixed(1) + ' 分钟',
      ]);
      if (cfm !== 'Y') break;
      const t0 = Date.now();
      for (let retry = true; retry; ) {
        retry = false;
        try {
          await doEnumeration(state, selB, false);
          await doImport(selB);
          const dur = ((Date.now() - t0) / 1000).toFixed(1);
          saveLastCollect({
            type: selB.length === BLDG_ORDER.length ? 'full' : 'partial',
            buildings: selB.length === BLDG_ORDER.length ? '全部' : selB.length,
            duration: parseFloat(dur),
            timestamp: new Date().toISOString(),
          });
          SEP();
          const durStr = dur >= 60 ? (dur / 60).toFixed(1) + ' 分钟' : dur + ' 秒';
          console.log(`  采集完成，耗时 ${durStr}，数据库已更新。\n`);
          console.log('  ' + qualityLine(loadQualityReport()));
          console.log('  质量审计已更新。筛选后导出 Excel 请使用数据管理页。\n');
          await prompt('\n  按 Enter 返回...');
        } catch (e) {
          if (e.message === 'SWITCH_TO_CDP') {
            state.edgeMode = '--edge';
            console.log('\n  切换到 CDP 模式重试...');
            retry = true;
            continue;
          }
          if (e.message === 'RETURN_TO_MENU') {
            console.log('\n  已返回主菜单。');
            break;
          }
          console.error('\n  失败:', e.message);
          await prompt('\n  按 Enter 返回...');
        }
        break;
      }
      break;
    }
    if (/^[1-7]+$/.test(s)) {
      for (const ch of s) {
        const v = parseInt(ch);
        if (v === 7) { if (selB.length === BLDG_ORDER.length) selB.length = 0; else { selB.length = 0; selB.push(...BLDG_ORDER); } }
        else { const b = BLDG_ORDER[v - 1]; const p = selB.indexOf(b); if (p >= 0) selB.splice(p, 1); else selB.push(b); }
      }
      continue;
    }
    console.log('\n  无效输入。');
    await prompt('\n  按 Enter 继续...');
  }
}

async function runCollectTui() {
  const state = {
    edgeMode: detectEdgeMode(),
    settings: loadSettings(),
  };
  const settingsArgs = toCliArgs(state.settings);
  if (settingsArgs.length > 0) console.log('设置参数: ' + settingsArgs.join(' ') + '\n');
  console.log(state.edgeMode === '--edge' ? '提示: CDP 模式 (Edge 9222)。\n' : '提示: 采集将自动启动 Edge。\n');

  while (true) {
    const ans = await mainMenu({
      status: dbStatus(),
      stats: loadBuildingStats(),
      qualityReport: loadQualityReport(),
      edgeMode: state.edgeMode,
    });

    if (ans === '0') { clearScreen(); console.log('已退出。'); return; }
    if (ans === '2') { await overviewFlow(); continue; }
    if (ans === '1') { await collectFlow(state); continue; }
    if (ans === '3') {
      const s = await settingsMenu(state.settings);
      if (s) {
        const { save } = require('./settings');
        state.settings = s;
        save(s);
        const sa = toCliArgs(s);
        if (sa.length > 0) console.log('设置已保存: ' + sa.join(' '));
        else console.log('设置已保存（全部默认）');
        await prompt('\n  按 Enter 返回...');
      }
      continue;
    }
    console.log('\n  无效输入。');
    await prompt('\n  按 Enter 继续...');
  }
}

module.exports = {
  runCollectTui,
  collectFlow,
  overviewFlow,
  showOverview,
};
