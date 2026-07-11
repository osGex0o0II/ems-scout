'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const root = path.join(__dirname, '..', '..');

function read(relativePath) {
  return fs.readFileSync(path.join(root, relativePath), 'utf8');
}

test('current status numbers match the run17 golden manifest', () => {
  const golden = JSON.parse(read('tests/fixtures/run17/golden-v1.json'));
  const status = read('docs/状态.md');

  for (const value of Object.values(golden.snapshotCounts)) {
    assert.match(status, new RegExp(`\\b${value}\\b`));
  }
  for (const [label, value] of Object.entries(golden.status)) {
    assert.match(status, new RegExp(`\\| ${label}(?:通讯)? \\| ${value} \\|`));
  }
  for (const [building, value] of Object.entries(golden.buildings)) {
    assert.match(status, new RegExp(`${building} ${value.uniqueCards}`));
  }

  assert.doesNotMatch(status, /6685|1565|2522/);
});

test('architecture and handoff preserve current ownership and external gates', () => {
  const architecture = read('docs/architecture.md');
  const handoff = read('docs/交接.md');
  const context = read('.context-summary.md');

  assert.match(architecture, /C# \/ \.NET 10/);
  assert.match(architecture, /Node\.js/);
  assert.match(architecture, /PowerShell/);
  assert.match(architecture, /只有 `EmsScout\.Infrastructure\/Migrations`/);
  assert.match(architecture, /不能描述成真实 EMS 端到端通过/);
  assert.match(handoff, /不通过 SQLite 打开 `out\/ac\.db`/);
  assert.match(context, /Windows CI 已通过 XAML、干净克隆测试、Sidecar payload smoke 和 MSIX 构建/);
  assert.match(context, /安装后运行、MSIX 全生命周期、内置 Sidecar 实际采集和真实 EMS 尚未验证/);
  assert.match(context, /重构已按四组提交/);
  assert.doesNotMatch(context, /等待用户确认是否按建议分组暂存和提交/);
  assert.doesNotMatch(read('docs/状态.md'), /当前没有执行 `git add`、`git commit`/);
  assert.ok(context.split(/\r?\n/).length < 150, 'Context summary must remain concise.');
});

test('communication documentation uses the exact indicator map without majority inference', () => {
  const model = read('docs/data-model.md');

  assert.match(model, /3bdc38eda0ae77f26807b2b6cdde4456\.png[^\n]*关机/);
  assert.match(model, /56f45bb314d74cc8da6c6c8e5942d08d\.png[^\n]*开机/);
  assert.match(model, /833bea6e66e7ab0e55704d655e135c7c\.png[^\n]*离线/);
  assert.match(model, /`IND_MAP` 必须位于 Vue 富集的 try\/catch 外部/);
  assert.doesNotMatch(model, /最多卡片的组|其他组（少数派）/);
});

test('product branding uses EMS Scout for display and EmsScout for .NET identifiers', () => {
  const packageJson = JSON.parse(read('package.json'));
  const visibleSurfaces = [
    'README.md',
    'AGENTS.md',
    '.context-summary.md',
    'native/src/EmsScout.Desktop/MainWindow.xaml',
    'native/src/EmsScout.Desktop/Package.appxmanifest',
    'native/src/EmsScout.Desktop/Pages/HomePage.xaml',
    'native/src/EmsScout.Desktop/ViewModels/DiagnosticsViewModel.cs',
    'src/tui/menus.js',
    'AC-Scout.bat',
    'electron/main.js',
    'electron/preload.js',
    'electron/tray.js',
    'electron/window.js',
    'web/panel/index.html',
    'scripts/report.js',
  ].map(read).join('\n');

  assert.equal(packageJson.name, 'emsscout');
  assert.equal(packageJson.build.productName, 'EMS Scout Legacy');
  assert.match(visibleSurfaces, /EMS Scout/);
  assert.match(read('native/EmsScout.Native.slnx'), /EmsScout\.Application/);
  assert.doesNotMatch(visibleSurfaces,
    /EMS Dashboard 自动化工具包|EMS 空调枚举项目|EMS 空调控制台|AC-Scout v|EMS Legacy Web Panel/);
});

test('project rules and Windows handoff define clean-clone and evidence gates', () => {
  const rules = read('docs/项目规范.md');
  const windows = read('docs/Windows验证清单.md');
  const readme = read('README.md');

  assert.match(rules, /面向用户的产品名称统一写作 `EMS Scout`/);
  assert.match(rules, /只有 `EmsScout\.Infrastructure\/Migrations`/);
  assert.match(rules, /Fixture=ProductionEvidence/);
  assert.match(rules, /不使用 `git add -A`/);
  assert.match(windows, /git clone git@github\.com:osGex0o0II\/ems-scout\.git/);
  assert.match(windows, /Windows 11 24H2/);
  assert.match(windows, /npm run native:test:evidence/);
  assert.match(windows, /out\\field-e2e-\*/);
  assert.match(readme, /\[项目规范\]\(docs\/项目规范\.md\)/);
  assert.match(readme, /\[Windows 验证清单\]\(docs\/Windows验证清单\.md\)/);
});
