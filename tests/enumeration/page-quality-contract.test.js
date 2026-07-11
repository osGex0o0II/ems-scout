'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { checkCardQuality, classifyStableOfflineTemplatePage } = require('../../src/rules');

const fixturePath = path.resolve(__dirname, '../fixtures/quality/page-quality-v1.json');
const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8'));

test('Node page-quality rules match the shared v1 contract', async t => {
  assert.equal(fixture.version, 1);
  for (const item of fixture.cases) {
    await t.test(item.id, () => {
      const quality = checkCardQuality(item.cards, item.meta || {});
      const stableOffline = classifyStableOfflineTemplatePage(item.cards, item.meta || {}, quality);
      assert.deepEqual({
        ok: quality.ok,
        uniformTemplate: quality.uniformTemplate,
        allOffline: quality.allOffline,
        duplicateCollapse: quality.duplicateCollapse,
        placeholderNames: quality.placeholderNames,
        withResolvedState: quality.withResolvedState,
        activeFieldOk: quality.activeFieldOk,
        invalidIndoor: quality.invalidIndoor,
        invalidSetTemp: quality.invalidSetTemp,
        stableOfflineTemplateEligible: stableOffline.eligible,
      }, item.expected);
    });
  }
});
