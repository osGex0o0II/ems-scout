'use strict';

const RULE_VERSION = 'reconcile-v1.0.0';

const DIFF_TYPE_RULES = {
  NEW_DEVICE: {
    rule: '实时行未被精确匹配、未命中人工覆盖、未找到同楼栋同楼层同名 DB 卡，且不是噪声或重复渲染。',
    reason: 'DB 中未找到匹配卡片，实时详情存在有效设备行。',
  },
  MISSING_IN_REALTIME: {
    rule: 'DB 卡片未被精确匹配、人工覆盖或宽松同名同楼层匹配消费。',
    reason: 'DB 中存在该卡片，但最新实时详情文件没有可匹配设备行。',
  },
  MATCH_FAILED: {
    rule: 'DB 与实时可通过人工覆盖或同名同楼层关联，但精确身份键不一致。',
    reason: '匹配规则未能自动确认同一设备，需要人工覆盖或降级匹配解释。',
  },
  DUPLICATE_RENDER: {
    rule: '实时详情出现同名同页重复行，或 quality_report/pages 元数据记录 raw_count > unique_count。',
    reason: '实时详情存在重复 DOM/重复采集行，DB 已按唯一卡片保留。',
  },
  DATA_NOISE: {
    rule: '实时行存在采集 error、默认模板、字段数量异常或非离线设备有效点位为 0。',
    reason: '实时详情行存在采集脏数据或无效节点。',
  },
  VIRTUAL_OVERRIDE: {
    rule: 'realtime_match_overrides.action=create_virtual 明确声明该实时设备虚拟纳管。',
    reason: '人工覆盖为虚拟纳管：实时详情存在有效点位，当前 SQLite 基线无实体卡片。',
  },
  UNKNOWN: {
    rule: '未命中任何已知归因规则。',
    reason: '无法归类的对账差异。',
  },
};

function explainDiff(input = {}) {
  const type = input.type || 'UNKNOWN';
  const rule = DIFF_TYPE_RULES[type] || DIFF_TYPE_RULES.UNKNOWN;
  const confidence = normalizeConfidence(input.confidence);
  const evidence = buildEvidence(input);
  const decisionPath = buildDecisionPath({
    ...input,
    type,
    evidence,
  });

  return {
    ruleVersion: RULE_VERSION,
    confidence,
    reason: input.reason || rule.reason,
    evidence,
    decisionPath,
    rule: {
      type,
      version: RULE_VERSION,
      description: rule.rule,
    },
  };
}

function normalizeConfidence(value) {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return Math.max(0, Math.min(1, Number(value.toFixed(2))));
  }
  const text = String(value || '').toLowerCase();
  if (text === 'high') return 0.95;
  if (text === 'medium') return 0.7;
  if (text === 'low') return 0.35;
  return 0.5;
}

function buildEvidence(input) {
  const db = input.db ? {
    cardId: input.db.card_id,
    sourceCardId: input.db.source_card_id,
    building: input.db.building,
    floor: input.db.floor_label,
    subArea: input.db.sub_area,
    pageName: input.db.page_name,
    name: input.db.name,
    switch: input.db.switch,
    comm: input.db.comm,
    exactIdentity: input.db.exactIdentity || '',
    nameFloorIdentity: input.db.nameFloorIdentity || '',
    duplicateRenderedPage: !!input.evidenceBase?.dbDuplicateRenderedPage,
  } : null;
  const realtime = input.realtime ? {
    rowId: input.realtime.row_id,
    sourceFile: input.realtime.source_file,
    sourceMtime: input.realtime.source_mtime,
    building: input.realtime.building,
    floor: input.realtime.floor_label,
    subArea: input.realtime.sub_area,
    pageName: input.realtime.page_name,
    name: input.realtime.name,
    devId: input.realtime.dev_id,
    deviceId: input.realtime.device_id,
    fieldCount: input.realtime.field_count,
    realtimeTagCount: input.realtime.realtime_tag_count,
    realtimeValidTagCount: input.realtime.realtime_valid_tag_count,
    defaultLike: !!input.realtime.default_like,
    error: input.realtime.error || '',
    exactIdentity: input.realtime.exactIdentity || '',
    nameFloorIdentity: input.realtime.nameFloorIdentity || '',
    noisy: !!input.evidenceBase?.realtimeNoisy,
  } : null;
  return {
    db,
    realtime,
    override: input.override ? {
      id: input.override.id,
      action: input.override.action,
      devId: input.override.dev_id,
      targetCardId: input.override.target_card_id,
      areaTypeOverride: input.override.area_type_override,
      note: input.override.note || '',
    } : null,
    manualOverrides: input.manual || [],
    match: {
      key: input.key || '',
      exactIdentity: input.evidenceBase?.exactIdentity || '',
      dbExactIdentity: input.evidenceBase?.dbExactIdentity || '',
      realtimeExactIdentity: input.evidenceBase?.realtimeExactIdentity || '',
      nameFloorIdentity: input.evidenceBase?.nameFloorIdentity || '',
      qualityDuplicateRenderedPage: !!input.evidenceBase?.qualityDuplicateRenderedPage,
      dbDuplicateRenderedPage: !!input.evidenceBase?.dbDuplicateRenderedPage,
      realtimeNoisy: !!input.evidenceBase?.realtimeNoisy,
    },
  };
}

function buildDecisionPath(input) {
  const steps = [];
  const hasDb = !!input.db;
  const hasRealtime = !!input.realtime;
  const override = input.override || null;
  steps.push(`规则版本 ${RULE_VERSION}`);
  if (hasDb) steps.push('读取 SQLite cards 基线并生成 DB 身份键');
  if (hasRealtime) steps.push('读取 realtime latest JSON 并生成实时身份键');
  steps.push('统一设备 Key 优先级：devId > deviceId > building+floor+name hash');
  if (override) {
    steps.push(`命中人工覆盖 realtime_match_overrides#${override.id} action=${override.action}`);
  } else {
    steps.push('未命中人工覆盖，进入自动规则判断');
  }

  const match = input.evidence || {};
  const matchEvidence = match.match || {};
  if (hasDb && hasRealtime) {
    const exactSame = matchEvidence.dbExactIdentity && matchEvidence.dbExactIdentity === matchEvidence.realtimeExactIdentity;
    const nameFloorSame = input.db.nameFloorIdentity && input.realtime.nameFloorIdentity &&
      input.db.nameFloorIdentity === input.realtime.nameFloorIdentity;
    steps.push(exactSame ? 'DB 与实时 exact identity 一致' : 'DB 与实时 exact identity 不一致');
    if (nameFloorSame) steps.push('楼栋 + 楼层 + 名称一致，允许降级解释');
    else steps.push('楼栋 + 楼层 + 名称未完全一致');
  } else if (hasRealtime) {
    steps.push('实时设备未找到可消费的 DB 卡片');
  } else if (hasDb) {
    steps.push('DB 卡片未找到可消费的实时详情行');
  }

  if (matchEvidence.qualityDuplicateRenderedPage || matchEvidence.dbDuplicateRenderedPage) {
    steps.push('quality/pages 元数据提示重复渲染');
  }
  if (matchEvidence.realtimeNoisy) {
    steps.push('实时详情存在噪声证据：error/defaultLike/有效点位异常');
  }

  steps.push(`归因结果：${input.type}`);
  return steps;
}

function ruleCatalog() {
  return Object.entries(DIFF_TYPE_RULES).map(([type, meta]) => ({
    type,
    ruleVersion: RULE_VERSION,
    rule: meta.rule,
    reason: meta.reason,
  }));
}

module.exports = {
  RULE_VERSION,
  DIFF_TYPE_RULES,
  explainDiff,
  ruleCatalog,
};
