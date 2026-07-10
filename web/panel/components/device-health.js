'use strict';

(function attachDeviceHealth(global) {
  const TEMP_MIN = 5;
  const TEMP_MAX = 40;
  const RANGE_FIELDS = {
    '室内温度': { min: -10, max: 60 },
    '设定温度': { min: 5, max: 40 },
    '设定温度上限': { min: 5, max: 45 },
    '设定温度下限': { min: 0, max: 40 },
    '通讯地址 (Modbus)': { min: 0, max: 255 },
  };
  const REVIEW_ISSUE_KEYS = new Set([
    'unmatched',
    'offline',
    'comm_unknown',
    'zuo_missing',
    'area_unmatched',
    'detail_error',
    'default_like',
    'points_incomplete',
    'temperature_warning',
    'temperature_missing',
  ]);

  function realtimeField(row, name) {
    return row && row.realtime && row.realtime.fields
      ? String(row.realtime.fields[name] ?? '').trim()
      : '';
  }

  function numericField(value) {
    const match = String(value ?? '').match(/-?\d+(?:\.\d+)?/);
    return match ? Number(match[0]) : NaN;
  }

  function text(value) {
    return String(value ?? '').trim();
  }

  function isIgnored(row) {
    return row && row.match_override_action === 'ignore_duplicate';
  }

  function isManaged(row) {
    return !!(row && row.realtime && !isIgnored(row));
  }

  function pointsComplete(row) {
    if (!row || !row.realtime) return false;
    const total = Number(row.realtime.realtime_tag_count || 0);
    const valid = Number(row.realtime.realtime_valid_tag_count || 0);
    return total > 0 && valid >= total;
  }

  function tempState(row) {
    if (!row || !row.realtime) return { missing: true, abnormal: false };
    const indoorRaw = realtimeField(row, '室内温度');
    const setRaw = realtimeField(row, '设定温度');
    const indoor = numericField(indoorRaw);
    const setTemp = numericField(setRaw);
    const missing = !indoorRaw || !setRaw || !Number.isFinite(indoor) || !Number.isFinite(setTemp);
    const abnormal = (Number.isFinite(indoor) && (indoor < TEMP_MIN || indoor > TEMP_MAX)) ||
      (Number.isFinite(setTemp) && (setTemp < TEMP_MIN || setTemp > TEMP_MAX));
    return { missing, abnormal, indoor, setTemp };
  }

  function hasInvalidRealtimeFields(row) {
    if (!row || !row.realtime) return false;
    if (row.realtime.valid_fields && Object.values(row.realtime.valid_fields).some(value => value === false)) return true;
    const fields = row.realtime.fields || {};
    return Object.entries(RANGE_FIELDS).some(([name, range]) => {
      if (fields[name] === undefined || fields[name] === null || fields[name] === '') return false;
      const n = numericField(fields[name]);
      return !Number.isFinite(n) || n < range.min || n > range.max;
    });
  }

  function isOffline(row) {
    return commState(row).state === '离线';
  }

  function commState(row) {
    const value = text((row && row.comm_state) || (row && row.card_comm) || (row && row.realtime && row.realtime.card_comm));
    if (value === '离线') return { state: '离线', type: 'muted', label: '通讯离线', source: row && (row.comm_state_source || row.card_state_source) || 'card_indicator' };
    if (value === '开机' || value === '关机' || value === '在线') return { state: '在线', type: 'success', label: '通讯在线', raw: value, source: row && (row.comm_state_source || row.card_state_source) || 'card_indicator' };
    const fieldComm = realtimeField(row, '通讯状态') || realtimeField(row, '通信状态');
    if (fieldComm === '离线' || fieldComm === '通讯异常') return { state: '离线', type: 'muted', label: '通讯离线', raw: fieldComm, source: 'realtime_field' };
    if (fieldComm) return { state: '在线', type: 'success', label: '通讯在线', raw: fieldComm, source: 'realtime_field' };
    return { state: '未知', type: 'warning', label: '通讯未知', source: row && row.realtime ? 'missing_card_snapshot' : 'missing' };
  }

  function needsManualClassify(row) {
    return !!(row && !isIgnored(row) && (row.area_type === '未匹配' || (!row.zuo && (row.building === '5号' || row.building === '6号'))));
  }

  function collectDeviceIssues(row) {
    const issues = [];
    const temp = tempState(row);
    if (!row) return [{ key: 'unknown', label: '未知', type: 'muted' }];
    if (!row.realtime && !isIgnored(row)) issues.push({ key: 'missing_realtime', label: '无实时详情', type: 'warning' });
    if (needsManualClassify(row)) issues.push({ key: 'unmatched', label: '需人工分类', type: 'warning' });
    if (isOffline(row)) {
      issues.push({ key: 'offline', label: '通讯离线', type: 'muted' });
    } else if (commState(row).state === '未知') {
      issues.push({ key: 'comm_unknown', label: '通讯未知', type: 'warning' });
    }
    if (!row.zuo && (row.building === '5号' || row.building === '6号')) {
      issues.push({ key: 'zuo_missing', label: '座号未识别', type: 'warning' });
    }
    if (row.area_type === '未匹配') issues.push({ key: 'area_unmatched', label: '区域未匹配', type: 'warning' });
    if (row.realtime && row.realtime.error) issues.push({ key: 'detail_error', label: '详情错误', type: 'danger' });
    if (row.realtime && row.realtime.default_like) issues.push({ key: 'default_like', label: '默认值疑似', type: 'warning' });
    if (hasInvalidRealtimeFields(row)) issues.push({ key: 'field_invalid', label: '字段异常', type: 'warning' });
    if (row.realtime && !pointsComplete(row)) issues.push({ key: 'points_incomplete', label: '点位不完整', type: 'warning' });
    if (temp.abnormal) issues.push({ key: 'temperature_warning', label: '温度异常', type: 'danger' });
    else if (row.realtime && temp.missing) issues.push({ key: 'temperature_missing', label: '温度缺失', type: 'warning' });
    if (isIgnored(row)) issues.push({ key: 'ignored', label: '已忽略', type: 'muted' });
    return issues;
  }

  function isDeviceAbnormal(row) {
    if (!row || isIgnored(row)) return false;
    return collectDeviceIssues(row).some(issue => REVIEW_ISSUE_KEYS.has(issue.key));
  }

  function isDeviceClean(row) {
    return !!row && !isIgnored(row) && !isDeviceAbnormal(row);
  }

  function deriveDeviceHealth(row) {
    if (!row) {
      return { status: 'unknown', label: '未知', badgeType: 'muted', reason: '没有足够字段判断设备状态' };
    }
    const issues = collectDeviceIssues(row);
    const has = key => issues.some(i => i.key === key);
    if (has('unmatched')) {
      return { status: 'unmatched', label: '需人工分类', badgeType: 'warning', reason: '区域或座号仍需人工确认' };
    }
    if (has('missing_realtime')) {
      return { status: 'missing_realtime', label: '无实时详情', badgeType: 'warning', reason: '该行缺少实时详情字段' };
    }
    if (has('offline')) {
      return { status: 'offline', label: '通讯离线', badgeType: 'muted', reason: '卡片通讯状态为离线，不等同于关机' };
    }
    if (has('comm_unknown')) {
      return { status: 'attention', label: '通讯未知', badgeType: 'warning', reason: '当前详情文件缺少卡片通讯快照，需重新采集后判断离线' };
    }
    if (has('temperature_warning') || has('temperature_missing')) {
      return { status: 'temperature_warning', label: '温度需复核', badgeType: has('temperature_warning') ? 'danger' : 'warning', reason: '室内温度或设定温度缺失/超出合理范围' };
    }
    if (has('detail_error') || has('default_like') || has('field_invalid') || has('points_incomplete') || has('area_unmatched') || has('zuo_missing')) {
      return { status: 'attention', label: '需排查', badgeType: 'warning', reason: '存在分类、点位或默认值问题' };
    }
    if (!row.realtime) {
      return { status: 'unknown', label: '未知', badgeType: 'muted', reason: '详情字段不足，无法判断健康态' };
    }
    const lock = realtimeField(row, '集控锁定');
    return {
      status: 'normal',
      label: '正常',
      badgeType: 'success',
      reason: lock === '开启'
        ? '无异常；集控锁定属于管控状态，可在运行状态列查看'
        : '详情字段可用，未发现需排查项',
    };
  }

  function matchesQuickFilter(row, key) {
    const health = deriveDeviceHealth(row);
    const power = realtimeField(row, '当前开关机状态');
    const lock = realtimeField(row, '集控锁定');
    if (key === 'all') return true;
    if (key === 'normal' || key === 'exclude_abnormal') return isDeviceClean(row);
    if (key === 'needs_review') return !isIgnored(row) && (isDeviceAbnormal(row) || health.status === 'unknown');
    if (key === 'unmatched') return needsManualClassify(row);
    if (key === 'offline') return health.status === 'offline';
    if (key === 'comm_unknown') return commState(row).state === '未知';
    if (key === 'db_missing') return false;
    if (key === 'realtime_unmatched') return needsManualClassify(row);
    if (key === 'locked') return lock === '开启';
    if (key === 'temp_abnormal') return tempState(row).abnormal || (row.realtime && tempState(row).missing);
    if (key === 'ignored') return isIgnored(row);
    if (key === 'on') return power === '开机';
    if (key === 'off') return power === '关机';
    return false;
  }

  global.DeviceHealth = {
    realtimeField,
    numericField,
    pointsComplete,
    tempState,
    hasInvalidRealtimeFields,
    isOffline,
    commState,
    collectDeviceIssues,
    isDeviceAbnormal,
    isDeviceClean,
    deriveDeviceHealth,
    matchesQuickFilter,
  };
})(window);
