const { BLDG_ORDER } = require('../rules');
const { clearScreen, SEP, cb, padVis, prompt, titleBar } = require('./ui');
const { BLDG_ESTIMATE, loadLastCollect, qualityBadge, deltaLabel } = require('./actions');
const { SETTINGS_SCHEMA, toCliArgs } = require('./settings');

async function mainMenu({ status, stats, qualityReport, edgeMode }) {
  clearScreen();
  const modeLabel = edgeMode === '--edge' ? 'CDP' : '自动';
  console.log('\n  AC-Scout v1.0  ' + modeLabel + '模式');
  const last = loadLastCollect();
  let headerLine = '  数据库为空';
  if (last) {
    const d = new Date(last.timestamp);
    const pad = n => String(n).padStart(2, '0');
    const ts = `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
    const typeLabel = last.type === 'full' ? '全量 6 栋' : `部分 ${last.buildings} 栋`;
    const dur = last.duration >= 60 ? (last.duration / 60).toFixed(1) + ' 分钟' : last.duration + ' 秒';
    headerLine = `${ts}  ${typeLabel}  耗时 ${dur}`;
  } else if (status) {
    const d = status.mtime.toLocaleString('zh-CN', { hour12: false });
    headerLine = `${d}  ${status.count} 台设备`;
  }
  SEP();
  console.log('  数据状态');
  console.log('    ' + headerLine);
  console.log('    ' + qualityBadge(qualityReport));
  SEP();
  console.log('  楼栋状态');
  if (stats) {
    console.log('    楼栋      当前/基准     状态      更新');
    for (const s of stats) {
      const t = s.updatedAt ? new Date(s.updatedAt) : null;
      const ts = t ? `${String(t.getMonth()+1).padStart(2,'0')}-${String(t.getDate()).padStart(2,'0')} ${String(t.getHours()).padStart(2,'0')}:${String(t.getMinutes()).padStart(2,'0')}` : '--';
      const curBase = `${s.total}/${s.baseline}`;
      const delta = deltaLabel(s.delta);
      const hint = s.delta === 0 ? 'OK' : (s.delta < 0 ? `${delta} 复采` : delta);
      console.log(`    ${s.building.padEnd(6)} ${curBase.padStart(10)}   ${hint.padEnd(12)} ${ts}`);
    }
  } else {
    console.log('    无数据库状态');
  }
  SEP();
  console.log('  操作');
  console.log('    [1] 采集    [2] 概览    [3] 设置    [0] 退出');
  SEP();
  const ans = await prompt('  请选择 [0-3]: ');
  return ans.trim();
}

async function buildingSelectMenu(selected, stats = []) {
  clearScreen();
  titleBar('选择采集楼栋');
  const sel = selected.length > 0 ? selected.join('、') : '\u2014';
  if (selected.length > 0) {
    const est = BLDG_ORDER.filter(b => selected.includes(b)).reduce((s, b) => {
      const t = BLDG_ESTIMATE[b].time;
      if (t.includes('秒')) return s + parseInt(t) / 60;
      if (t.includes('分钟')) return s + parseInt(t);
      return s + 1;
    }, 0);
    console.log('  当前: ' + sel + '  |  预计: ~' + est.toFixed(1) + ' 分钟\n');
  } else {
    console.log('  当前: ' + sel + '\n');
  }
  for (const b of BLDG_ORDER) {
    const m = BLDG_ESTIMATE[b];
    const s = stats.find(r => r.building === b);
    const on = selected.includes(b);
    const current = s ? `${s.total}/${s.baseline}` : `--/${m.cards}`;
    const delta = s ? deltaLabel(s.delta) : '--';
    const hint = s && s.delta < 0 ? '  需复采' : '';
    console.log(`    [${BLDG_ORDER.indexOf(b) + 1}] ${cb(on)} ${padVis(b + ' ' + m.name, 22)}  当前 ${current.padStart(9)}  ${delta.padStart(3)}  ~${m.time}${hint}`);
  }
  console.log(`    [7] ${cb(selected.length === BLDG_ORDER.length)} 全部楼栋`);
  console.log('');
  console.log('    [C] 开始采集   [Q] 返回上级');
  const ans = await prompt('  编号 [1-7/C/Q]（可连续输入如 12345）: ');
  return ans.trim().toUpperCase();
}

async function settingsMenu(settings) {
  const keys = Object.keys(settings);
  while (true) {
    clearScreen();
    titleBar('设置');
    const cliPreview = toCliArgs(settings);
    if (cliPreview.length > 0) {
      console.log('  CLI: ' + cliPreview.join(' '));
    } else {
      console.log('  全部默认（无额外参数）');
    }
    console.log('');
    for (let i = 0; i < keys.length; i++) {
      const k = keys[i];
      const s = settings[k];
      const sc = SETTINGS_SCHEMA[k];
      const val = sc.type === 'toggle'
        ? (s ? '开' : '关')
        : (s === sc.default ? sc.default : s);
      const mark = sc.type === 'toggle' ? cb(s) : `[${val}]`;
      console.log(`    [${i + 1}] ${mark} ${sc.label.padEnd(16)} 默认: ${sc.default}`);
    }
    console.log('');
    console.log('    [S] 保存并返回   [Q] 放弃返回');
    const ans = await prompt('  编号 [1-' + keys.length + '/S/Q]: ');
    const trimmed = ans.trim().toUpperCase();
    if (trimmed === 'Q') return null;
    if (trimmed === 'S') return settings;
    const n = parseInt(trimmed);
    if (n >= 1 && n <= keys.length) {
      const k = keys[n - 1];
      const sc = SETTINGS_SCHEMA[k];
      if (sc.type === 'toggle') {
        settings[k] = !settings[k];
      } else {
        const idx = sc.options.indexOf(settings[k]);
        settings[k] = sc.options[(idx + 1) % sc.options.length];
      }
      continue;
    }
    console.log('\n  无效输入。');
    await prompt('\n  按 Enter 继续...');
  }
}

module.exports = {
  mainMenu,
  buildingSelectMenu,
  settingsMenu,
};
