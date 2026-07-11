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
  assert.match(context, /Windows XAML\/MSIX\/内置 Sidecar\/真实 EMS 尚未验证/);
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
