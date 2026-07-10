'use strict';

const REALTIME_NORMAL_VALUES = {
  '当前开关机状态': ['开机', '关机'],
  '设定风速': ['自动', '高', '中', '低'],
  '系统模式设置': ['制冷', '通风', '制热', '制热+地暖', '地暖'],
  '集控锁定': ['关闭', '开启'],
  '系统类型': ['两管冷暖', '两管冷暖+地暖', '两管制冷'],
};

const REALTIME_RANGE_VALUES = {
  '室内温度': { min: -10, max: 60 },
  '设定温度': { min: 5, max: 40 },
  '设定温度上限': { min: 5, max: 45 },
  '设定温度下限': { min: 0, max: 40 },
  '通讯地址 (Modbus)': { min: 0, max: 255 },
};

const STATE_COMPARE_FRESH_MINUTES = 30;

function realtimeFieldValue(row, fieldName) {
  return row && row.realtime && row.realtime.fields
    ? String(row.realtime.fields[fieldName] ?? '').trim()
    : '';
}

function minutesBetween(a, b) {
  const ta = a ? new Date(a).getTime() : NaN;
  const tb = b ? new Date(b).getTime() : NaN;
  if (!Number.isFinite(ta) || !Number.isFinite(tb)) return null;
  return Math.round(Math.abs(ta - tb) / 60000);
}

function rowStateCompareMeta(row, dbSourceMtime = '') {
  const realtimeMtime = row && row.realtime ? row.realtime.source_mtime || '' : '';
  const ageMinutes = minutesBetween(dbSourceMtime, realtimeMtime);
  return {
    db_source_mtime: dbSourceMtime || '',
    realtime_source_mtime: realtimeMtime || '',
    state_compare_age_minutes: ageMinutes,
    state_compare_fresh: ageMinutes !== null && ageMinutes <= STATE_COMPARE_FRESH_MINUTES,
    state_compare_stale: ageMinutes !== null && ageMinutes > STATE_COMPARE_FRESH_MINUTES,
  };
}

function isRealtimePointsComplete(realtime) {
  return !!(
    realtime &&
    Number(realtime.realtime_tag_count || 0) > 0 &&
    Number(realtime.realtime_valid_tag_count || 0) >= Number(realtime.realtime_tag_count || 0)
  );
}

function isRealtimeFieldAbnormal(fieldName, value) {
  const normalValues = REALTIME_NORMAL_VALUES[fieldName] || [];
  const v = String(value ?? '').trim();
  return !!(v && normalValues.length && !normalValues.includes(v));
}

function numericField(value) {
  const match = String(value ?? '').match(/-?\d+(?:\.\d+)?/);
  if (!match) return NaN;
  return Number(match[0]);
}

function isRealtimeDetailInvalid(realtime) {
  if (!realtime) return false;
  if (realtime.error) return true;
  if (realtime.default_like) return true;
  if (realtime.valid_fields && Object.values(realtime.valid_fields).some(value => value === false)) return true;
  return Object.entries(REALTIME_NORMAL_VALUES).some(([fieldName, normalValues]) => {
    const value = String(realtime.fields && realtime.fields[fieldName] !== undefined ? realtime.fields[fieldName] : '').trim();
    return value && !normalValues.includes(value);
  }) || Object.entries(REALTIME_RANGE_VALUES).some(([fieldName, range]) => {
    const value = realtime.fields && realtime.fields[fieldName] !== undefined ? realtime.fields[fieldName] : '';
    if (value === '' || value === null || value === undefined) return false;
    const n = numericField(value);
    return !Number.isFinite(n) || n < range.min || n > range.max;
  });
}

function realtimeFieldIssueCount(row) {
  if (!row || !row.realtime || !row.realtime.fields) return 0;
  let count = 0;
  for (const [fieldName] of Object.entries(REALTIME_NORMAL_VALUES)) {
    const value = String(row.realtime.fields[fieldName] !== undefined ? row.realtime.fields[fieldName] : '').trim();
    if (isRealtimeFieldAbnormal(fieldName, value)) count++;
  }
  return count;
}

function isIndoorTempAbnormal(row) {
  const v = numericField(realtimeFieldValue(row, '室内温度'));
  return Number.isFinite(v) && (v < 5 || v > 40);
}

function isSetTempAbnormal(row) {
  const v = numericField(realtimeFieldValue(row, '设定温度'));
  return Number.isFinite(v) && (v < 5 || v > 40);
}

function isTempAbnormal(row) {
  return isIndoorTempAbnormal(row) || isSetTempAbnormal(row);
}

function isIgnoredRealtimeRow(row) {
  return row && row.match_override_action === 'ignore_duplicate';
}

function isManagedRealtimeRow(row) {
  return !!(row && !isIgnoredRealtimeRow(row) && row.realtime);
}

function isZuoMissing(row) {
  return !!(row && !row.zuo && (row.building === '5号' || row.building === '6号'));
}

function isUnresolvedRealtimeRow(row) {
  return !!(row && !isIgnoredRealtimeRow(row) && (row.area_type === '未匹配' || isZuoMissing(row)));
}

function isRealtimeDbUnmatchedRow(row) {
  return !!(
    row &&
    !isIgnoredRealtimeRow(row) &&
    (row.virtual_match || row.match_override_action === 'create_virtual' || row.match_status === '虚拟纳管')
  );
}

function isTempMissing(row) {
  if (!row || !row.realtime) return false;
  const indoorRaw = realtimeFieldValue(row, '室内温度');
  const setRaw = realtimeFieldValue(row, '设定温度');
  return !indoorRaw || !setRaw || !Number.isFinite(numericField(indoorRaw)) || !Number.isFinite(numericField(setRaw));
}

function commState(row) {
  const cardComm = String(
    (row && row.card_comm) ||
    (row && row.realtime && row.realtime.card_comm) ||
    (row && row.realtime && row.realtime.cardComm) ||
    (row && row.cardComm) ||
    ''
  ).trim();
  const realtimeComm = realtimeFieldValue(row, '通讯状态') || realtimeFieldValue(row, '通信状态');
  const dbComm = String(row && (row.db_comm || row.comm) || '').trim();
  if (cardComm === '离线') return { state: '离线', source: 'card_indicator' };
  if (cardComm === '开机' || cardComm === '关机') return { state: '在线', source: 'card_indicator', raw: cardComm };
  if (realtimeComm === '离线' || realtimeComm === '通讯异常') return { state: '离线', source: 'realtime_field', raw: realtimeComm };
  if (realtimeComm) return { state: '在线', source: 'realtime_field', raw: realtimeComm };
  if (row && row.realtime) return { state: '未知', source: 'missing_card_snapshot' };
  if (dbComm === '离线') return { state: '离线', source: 'db_compat', raw: dbComm };
  if (dbComm === '开机' || dbComm === '关机') return { state: '在线', source: 'db_compat', raw: dbComm };
  return { state: '未知', source: 'missing' };
}

function commStateValue(row) {
  return commState(row).state;
}

function isCommUnknown(row) {
  return commStateValue(row) === '未知';
}

function isOfflineOrCommAbnormal(row) {
  return commState(row).state === '离线';
}

function hasReviewIssue(row) {
  if (!row || isIgnoredRealtimeRow(row)) return false;
  const invalid = isRealtimeDetailInvalid(row.realtime);
  const pointsIncomplete = !isRealtimePointsComplete(row.realtime);
  const detailError = !!(row.realtime && row.realtime.error);
  const defaultLike = !!(row.realtime && row.realtime.default_like);
  const tempAbnormal = isTempAbnormal(row) || isTempMissing(row);
  const areaUnmatched = row.area_type === '未匹配';
  return invalid ||
    pointsIncomplete ||
    detailError ||
    defaultLike ||
    tempAbnormal ||
    isCommUnknown(row) ||
    isOfflineOrCommAbnormal(row) ||
    areaUnmatched ||
    isZuoMissing(row);
}

function isCleanDeviceRow(row) {
  return !!(row && !isIgnoredRealtimeRow(row) && !hasReviewIssue(row));
}

function normalizedRealtimeSwitch(row) {
  const power = realtimeFieldValue(row, '当前开关机状态');
  if (power === '开机') return 'ON';
  if (power === '关机') return 'OFF';
  return '';
}

function hasDbRealtimeMismatch(row) {
  if (!row || !row.db_match) return false;
  if (row.state_compare_fresh === false || row.state_compare_stale === true) return false;
  const sw = normalizedRealtimeSwitch(row);
  if (sw && row.db_switch && row.db_switch !== '-' && sw !== row.db_switch) return true;
  const mode = realtimeFieldValue(row, '系统模式设置');
  if (mode && row.db_mode && row.db_mode !== '-' && mode !== row.db_mode) return true;
  const fan = realtimeFieldValue(row, '设定风速');
  if (fan && row.db_fan && row.db_fan !== '-' && fan !== row.db_fan) return true;
  return false;
}

function hasStateValueDifference(row) {
  if (!row || !row.db_match || !row.realtime) return false;
  const dbState = String(row.db_comm || row.comm || '').trim();
  const realtimeState = realtimeFieldValue(row, '当前开关机状态');
  return !!(dbState && realtimeState && dbState !== realtimeState);
}

function stateCompareSummary(rows) {
  const comparable = (rows || []).filter(row => row && row.db_match && row.realtime);
  const ages = comparable
    .map(row => row.state_compare_age_minutes)
    .filter(value => value !== null && value !== undefined && Number.isFinite(Number(value)))
    .map(Number);
  const dbTimes = comparable.map(row => row.db_source_mtime).filter(Boolean).sort();
  const realtimeTimes = comparable.map(row => row.realtime_source_mtime || (row.realtime && row.realtime.source_mtime)).filter(Boolean).sort();
  const rawMismatch = comparable.filter(hasStateValueDifference).length;
  const freshMismatch = comparable.filter(hasDbRealtimeMismatch).length;
  const maxAge = ages.length ? Math.max(...ages) : null;
  return {
    comparable: comparable.length,
    raw_mismatch: rawMismatch,
    fresh_mismatch: freshMismatch,
    stale_mismatch: rawMismatch - freshMismatch,
    stale: maxAge !== null && maxAge > STATE_COMPARE_FRESH_MINUTES,
    fresh_threshold_minutes: STATE_COMPARE_FRESH_MINUTES,
    max_age_minutes: maxAge,
    db_source_mtime: dbTimes.at(-1) || '',
    realtime_source_mtime: realtimeTimes.at(-1) || '',
  };
}

function matchesDbStatus(row, status) {
  if (!status) return true;
  const power = realtimeFieldValue(row, '当前开关机状态');
  if (status === 'on') return power === '开机';
  if (status === 'off') return power === '关机';
  if (status === 'offline') return isOfflineOrCommAbnormal(row);
  if (status === 'unknown') {
    return !power || (power !== '开机' && power !== '关机');
  }
  return true;
}

function matchesIssueFilter(row, issue) {
  if (!issue) return true;
  const invalid = isRealtimeDetailInvalid(row.realtime);
  const pointsIncomplete = !isRealtimePointsComplete(row.realtime);
  const detailError = !!(row.realtime && row.realtime.error);
  const defaultLike = !!(row.realtime && row.realtime.default_like);
  const tempAbnormal = isTempAbnormal(row) || isTempMissing(row);
  const lockedOn = realtimeFieldValue(row, '集控锁定') === '开启';
  const unmatched = isUnresolvedRealtimeRow(row);
  const offline = isOfflineOrCommAbnormal(row);
  const commUnknown = isCommUnknown(row);
  const ignored = isIgnoredRealtimeRow(row);
  if (issue === 'needs_review') return hasReviewIssue(row);
  if (issue === 'normal' || issue === 'exclude_abnormal') return isCleanDeviceRow(row);
  if (issue === 'offline') return offline;
  if (issue === 'comm_unknown') return commUnknown;
  if (issue === 'unmatched') return unmatched && !ignored;
  if (issue === 'field_invalid') return invalid;
  if (issue === 'points_incomplete') return pointsIncomplete;
  if (issue === 'detail_error') return detailError;
  if (issue === 'default_like') return defaultLike;
  if (issue === 'temp_abnormal') return tempAbnormal;
  if (issue === 'locked_on') return lockedOn;
  return true;
}

function matchesTempState(row, state) {
  if (!state) return true;
  if (state === 'abnormal') return isTempAbnormal(row) || isTempMissing(row);
  if (state === 'indoor_high') {
    const v = numericField(realtimeFieldValue(row, '室内温度'));
    return Number.isFinite(v) && v > 40;
  }
  if (state === 'indoor_low') {
    const v = numericField(realtimeFieldValue(row, '室内温度'));
    return Number.isFinite(v) && v < 5;
  }
  if (state === 'set_temp_abnormal') return isSetTempAbnormal(row);
  return true;
}

function cardFacetPower(row) {
  const realtimePower = realtimeFieldValue(row, '当前开关机状态');
  if (realtimePower) return realtimePower;
  if (row && row.db_switch === 'ON') return '开机';
  if (row && row.db_switch === 'OFF') return '关机';
  if (row && row.switch === 'ON') return '开机';
  if (row && row.switch === 'OFF') return '关机';
  return '';
}

function computeCardFacets(rows) {
  const list = Array.isArray(rows) ? rows : [];
  const ignored = row => isIgnoredRealtimeRow(row);
  const needsReview = row => !ignored(row) && hasReviewIssue(row);
  return {
    all: list.length,
    normal: list.filter(row => isCleanDeviceRow(row)).length,
    exclude_abnormal: list.filter(row => isCleanDeviceRow(row)).length,
    matched: list.filter(row => isManagedRealtimeRow(row)).length,
    unmatched: list.filter(row => isUnresolvedRealtimeRow(row)).length,
    db_missing: 0,
    realtime_unmatched: list.filter(row => isRealtimeDbUnmatchedRow(row)).length,
    offline: list.filter(isOfflineOrCommAbnormal).length,
    comm_unknown: list.filter(isCommUnknown).length,
    on: list.filter(row => cardFacetPower(row) === '开机').length,
    off: list.filter(row => cardFacetPower(row) === '关机').length,
    locked: list.filter(row => realtimeFieldValue(row, '集控锁定') === '开启').length,
    temp_abnormal: list.filter(row => isTempAbnormal(row) || isTempMissing(row)).length,
    needs_review: list.filter(needsReview).length,
    ignored: list.filter(ignored).length,
    points_incomplete: list.filter(row => !isRealtimePointsComplete(row.realtime)).length,
    field_invalid: list.filter(row => isRealtimeDetailInvalid(row.realtime)).length,
    detail_error: list.filter(row => row.realtime && row.realtime.error).length,
    default_like: list.filter(row => row.realtime && row.realtime.default_like).length,
  };
}

module.exports = {
  REALTIME_NORMAL_VALUES,
  REALTIME_RANGE_VALUES,
  STATE_COMPARE_FRESH_MINUTES,
  realtimeFieldValue,
  minutesBetween,
  rowStateCompareMeta,
  isRealtimePointsComplete,
  isRealtimeFieldAbnormal,
  isRealtimeDetailInvalid,
  realtimeFieldIssueCount,
  numericField,
  isIndoorTempAbnormal,
  isSetTempAbnormal,
  isTempAbnormal,
  isIgnoredRealtimeRow,
  isManagedRealtimeRow,
  isUnresolvedRealtimeRow,
  isRealtimeDbUnmatchedRow,
  isTempMissing,
  isOfflineOrCommAbnormal,
  commState,
  commStateValue,
  isCommUnknown,
  isZuoMissing,
  hasReviewIssue,
  isCleanDeviceRow,
  normalizedRealtimeSwitch,
  hasDbRealtimeMismatch,
  hasStateValueDifference,
  stateCompareSummary,
  matchesDbStatus,
  matchesIssueFilter,
  matchesTempState,
  cardFacetPower,
  computeCardFacets,
};
