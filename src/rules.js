'use strict';

const BLDG_ORDER = ['1号', '2号', '3号', '4号', '5号', '6号'];

function getZuo5(x) {
  if (x <= 400) return 'A座';
  if (x <= 616) return 'B座';
  if (x <= 874) return 'C座';
  if (x <= 1120) return 'D座';
  if (x <= 1424) return 'E座';
  return 'F座';
}

function getZuo6(x) {
  if (x <= 650) return 'A座';
  if (x <= 1220) return 'B座';
  return 'C座';
}

const BLDG_META = {
  '1号': { full: '1号科研综合楼', fullName: '1号科研综合楼', name: '1号科研综合楼', zuoFn: null, getZuo: null, baselineCards: 1493, baselineSubAreas: 30, estimateTime: '2分钟' },
  '2号': { full: '2号学术交流中心', fullName: '2号学术交流中心', name: '2号学术交流中心', zuoFn: null, getZuo: null, baselineCards: 107, baselineSubAreas: 5, estimateTime: '15秒' },
  '3号': { full: '3号公寓楼', fullName: '3号公寓楼', name: '3号公寓楼', zuoFn: null, getZuo: null, baselineCards: 1106, baselineSubAreas: 30, estimateTime: '2分钟' },
  '4号': { full: '4号公寓楼', fullName: '4号公寓楼', name: '4号公寓楼', zuoFn: null, getZuo: null, baselineCards: 1096, baselineSubAreas: 30, estimateTime: '2分钟' },
  '5号': { full: '5号综合服务中心', fullName: '5号综合服务中心', name: '5号综合服务中心', zuoFn: getZuo5, getZuo: getZuo5, baselineCards: 286, baselineSubAreas: 17, estimateTime: '30秒' },
  '6号': { full: '6号科研楼', fullName: '6号科研楼', name: '6号科研楼', zuoFn: getZuo6, getZuo: getZuo6, baselineCards: 2480, baselineSubAreas: 31, estimateTime: '4分钟' },
};

const PUBLIC_KEYWORDS = ['GQ', 'WSJ', 'DTT', 'FDT', 'XFDT', 'CSJ', 'FWJ', 'ZBS', 'ZSG', 'MD', 'RDJHJF'];

const IND_MAP = {
  '3bdc38eda0ae77f26807b2b6cdde4456.png': '关机',
  '56f45bb314d74cc8da6c6c8e5942d08d.png': '开机',
  '833bea6e66e7ab0e55704d655e135c7c.png': '离线',
};

const KNOWN_MISSING_INDICATOR_DEVICES = new Set([
  '2-2BC-2M001-KT-1',
  '2-2BC-2M001-KT-2',
]);

function isPublic(name = '', layout = '') {
  if (layout === 'group') return true;
  if (/^QL-\d/.test(name)) return false;
  return PUBLIC_KEYWORDS.some(k => name.includes(k));
}

function classifyAreaType(name, layout) {
  return isPublic(name, layout) ? '公区' : '非公区';
}

function getZone(x, building) {
  if (building === '5号') {
    if (x <= 400) return 0;
    if (x <= 616) return 1;
    if (x <= 874) return 2;
    if (x <= 1120) return 3;
    if (x <= 1424) return 4;
    return 5;
  }
  if (x <= 650) return 0;
  if (x <= 1220) return 1;
  return 2;
}

function checkCardQuality(cards, meta = {}) {
  if (!cards || cards.length === 0) return { ok: false, details: 'no cards' };
  const n = cards.length;
  const rawCount = Number(meta.rawCount ?? meta.raw_count ?? n) || n;
  const uniqueCount = Number(meta.uniqueCount ?? meta.unique_count ?? n) || n;
  const duplicateCollapse = rawCount >= 3 && uniqueCount <= Math.max(1, Math.floor(rawCount * 0.5));
  const placeholderNames = cards.filter(c => !c.name || c.name === '0-0001-KT').length;
  const switchLoaded = cards.filter(c => c.switch !== '-').length;
  const withMode = cards.filter(c => c.mode !== '-').length;
  const withRealIndoor = cards.filter(c => c.indoor !== '-' && parseFloat(c.indoor) > 0).length;
  const withRealSetTemp = cards.filter(c => c.setTemp !== '-' && parseFloat(c.setTemp) > 0).length;
  const withRealFan = cards.filter(c => c.fan !== '-' && c.fan !== '中' && c.fan !== '0').length;
  const withComm = cards.filter(c => c.comm).length;
  const withIndicator = cards.filter(c => c.indicator).length;
  const withResolvedState = cards.filter(c => c.comm === '开机' || c.comm === '关机' || c.comm === '离线').length;
  const activeCards = cards.filter(c => c.comm === '开机' || c.comm === '关机');
  const activeCount = activeCards.length;
  const activeWithSwitch = activeCards.filter(c => c.switch === 'ON' || c.switch === 'OFF').length;
  const activeWithMode = activeCards.filter(c => c.mode && c.mode !== '-').length;
  const activeWithFan = activeCards.filter(c => ['低', '中', '高', '自动'].includes(c.fan)).length;
  const activeWithIndoor = activeCards.filter(c => isRealIndoor(c.indoor)).length;
  const activeWithSetTemp = activeCards.filter(c => isValidSetTemp(c.setTemp)).length;
  const invalidIndoor = cards.filter(c => hasNumericValue(c.indoor) && !isValidIndoor(c.indoor)).length;
  const invalidSetTemp = cards.filter(c => hasNumericValue(c.setTemp) && parseFloat(c.setTemp) !== 0 && !isValidSetTemp(c.setTemp)).length;
  const activeFieldOk = activeCount === 0 || (
    activeWithSwitch === activeCount &&
    activeWithMode === activeCount &&
    activeWithFan === activeCount &&
    activeWithIndoor === activeCount &&
    activeWithSetTemp === activeCount
  );
  const hasRealTemp = withRealIndoor > 0 || withRealSetTemp > 0;
  const allOffline = n > 0 && cards.every(c => c.comm === '离线');
  const uniqueIndoor = new Set(cards.map(c => c.indoor));
  const uniqueSetTemp = new Set(cards.map(c => c.setTemp));
  const uniqueFan = new Set(cards.map(c => c.fan));
  const uniqueMode = new Set(cards.map(c => c.mode));
  const valOf = set => set.size === 1 ? [...set][0] : '';
  const indoorVal = valOf(uniqueIndoor);
  const setTempVal = valOf(uniqueSetTemp);
  const fanVal = valOf(uniqueFan);
  const modeVal = valOf(uniqueMode);
  const uniformValues = n >= 2 && uniqueIndoor.size <= 1 && uniqueSetTemp.size <= 1 && uniqueFan.size <= 1 && uniqueMode.size <= 1;
  const knownDefaultValues =
    (indoorVal === '0' && setTempVal === '0' && fanVal === '0') ||
    (indoorVal === '0' && setTempVal === '0' && fanVal === '中' && modeVal === '制冷') ||
    (indoorVal === '26' && setTempVal === '25' && fanVal === '中' && modeVal === '制冷');
  const uniqueComm = new Set(cards.map(c => c.comm));
  const uniformComm = n >= 3 && uniqueComm.size <= 1;
  const allOn = n > 0 && cards.every(c => c.comm === '开机');
  const allOff = n > 0 && cards.every(c => c.comm === '关机');
  const uniformTemplate = uniformValues && knownDefaultValues;
  const details = `sw=${switchLoaded}/${n} mode=${withMode}/${n} tmp=${withRealIndoor}/${n} set=${withRealSetTemp}/${n} fan=${withRealFan}/${n} comm=${withComm}/${n} ind=${withIndicator}/${n} ph=${placeholderNames}/${n}${activeCount ? ` active=${activeWithSwitch}/${activeWithMode}/${activeWithFan}/${activeWithIndoor}/${activeWithSetTemp}/${activeCount}` : ''}${invalidIndoor || invalidSetTemp ? ` invalid=${invalidIndoor}/${invalidSetTemp}` : ''}${rawCount > uniqueCount ? ` dup=${rawCount}->${uniqueCount}` : ''}${duplicateCollapse ? ' duplicate-collapse' : ''}${uniformTemplate ? ' template' : ''}`;
  return {
    ok: placeholderNames === 0 && !duplicateCollapse && withResolvedState === n && !uniformTemplate && invalidIndoor === 0 && invalidSetTemp === 0 && activeFieldOk,
    details,
    placeholderNames,
    duplicateCollapse,
    withResolvedState,
    uniformTemplate,
    allOffline,
    hasRealTemp,
    activeCount,
    activeFieldOk,
    activeWithSwitch,
    activeWithMode,
    activeWithFan,
    activeWithIndoor,
    activeWithSetTemp,
    invalidIndoor,
    invalidSetTemp,
  };
}

function hasNumericValue(value) {
  if (value === null || value === undefined || value === '') return false;
  const n = parseFloat(value);
  return Number.isFinite(n);
}

function isValidIndoor(value) {
  const n = parseFloat(value);
  return Number.isFinite(n) && n >= 0 && n <= 60;
}

function isRealIndoor(value) {
  const n = parseFloat(value);
  return Number.isFinite(n) && n > 0 && n <= 60;
}

function isValidSetTemp(value) {
  const n = parseFloat(value);
  return Number.isFinite(n) && n >= 5 && n <= 40;
}

function classifyPersistentDeviceAnomalyPage(cards, meta = {}) {
  if (!Array.isArray(cards) || cards.length === 0) {
    return { eligible: false, anomalyCount: 0, anomalyRatio: 0, anomalies: [], signature: '', details: 'no cards' };
  }

  const n = cards.length;
  const qc = checkCardQuality(cards, meta);
  const names = cards.map(c => String(c.name || '').trim());
  const namesComplete = names.every(name => name && name !== '0-0001-KT');
  const namesUnique = new Set(names).size === n;
  const commComplete = cards.every(c => c.comm === '开机' || c.comm === '关机' || c.comm === '离线');
  const indicatorsComplete = cards.every(c => Boolean(String(c.indicator || '').trim()));
  const activeCards = cards.filter(c => c.comm === '开机' || c.comm === '关机');
  const activeSwitchesComplete = activeCards.every(c => c.switch === 'ON' || c.switch === 'OFF');

  const anomalies = cards.flatMap((card, index) => {
    const active = card.comm === '开机' || card.comm === '关机';
    const fields = [];
    if (hasNumericValue(card.indoor) && !isValidIndoor(card.indoor)) fields.push('indoor');
    if (hasNumericValue(card.setTemp) && parseFloat(card.setTemp) !== 0 && !isValidSetTemp(card.setTemp)) fields.push('setTemp');
    if (active && !isRealIndoor(card.indoor) && !fields.includes('indoor')) fields.push('indoor');
    if (active && !isValidSetTemp(card.setTemp) && !fields.includes('setTemp')) fields.push('setTemp');
    if (active && (!card.mode || card.mode === '-')) fields.push('mode');
    if (active && (!card.fan || card.fan === '-' || card.fan === '0')) fields.push('fan');
    return fields.length ? [{ index, name: names[index], fields, card }] : [];
  });

  const anomalyCount = anomalies.length;
  const anomalyRatio = anomalyCount / n;
  const bounded = anomalyCount > 0 && anomalyCount <= 2 && anomalyRatio <= 0.1;
  const eligible =
    !qc.ok &&
    namesComplete &&
    namesUnique &&
    !qc.duplicateCollapse &&
    commComplete &&
    indicatorsComplete &&
    activeSwitchesComplete &&
    !qc.uniformTemplate &&
    bounded;
  const identitySignature = names.join('|');
  const anomalySignature = anomalies.map(({ name, fields, card }) => [
    name,
    fields.join(','),
    card.switch || '',
    card.comm || '',
    card.indoor || '',
    card.setTemp || '',
    card.mode || '',
    card.fan || '',
    card.indicator || '',
  ].join('|')).join('||');

  return {
    eligible,
    anomalyCount,
    anomalyRatio,
    anomalies,
    signature: eligible ? `${identitySignature}::${anomalySignature}` : '',
    details: `device-anomalies=${anomalyCount}/${n} names=${namesComplete && namesUnique ? 'ok' : 'bad'} comm=${commComplete ? 'ok' : 'bad'} ind=${indicatorsComplete ? 'ok' : 'bad'} active-switch=${activeSwitchesComplete ? 'ok' : 'bad'}`,
  };
}

function normalizeKnownSourceDefects(cards) {
  if (!Array.isArray(cards)) return [];
  return cards.map(card => KNOWN_MISSING_INDICATOR_DEVICES.has(String(card && card.name || '').trim())
    ? { ...card, indicator: '', comm: '' }
    : card);
}

function classifyKnownMissingIndicatorPage(cards, meta = {}) {
  if (!Array.isArray(cards) || cards.length === 0) {
    return { eligible: false, devices: [], details: 'no cards' };
  }
  const normalized = normalizeKnownSourceDefects(cards);
  const qc = checkCardQuality(normalized, meta);
  const names = normalized.map(card => String(card.name || '').trim());
  const unresolved = normalized.filter(card => !card.indicator || !card.comm);
  const unresolvedNames = unresolved.map(card => String(card.name || '').trim()).sort();
  const expectedNames = [...KNOWN_MISSING_INDICATOR_DEVICES].sort();
  const exactKnownSet = unresolvedNames.length === expectedNames.length &&
    unresolvedNames.every((name, index) => name === expectedNames[index]);
  const knownFieldsComplete = unresolved.every(card =>
    (card.switch === 'ON' || card.switch === 'OFF') &&
    card.mode && card.mode !== '-' &&
    card.fan && card.fan !== '-' && card.fan !== '0' &&
    isRealIndoor(card.indoor) &&
    isValidSetTemp(card.setTemp));
  const otherCardsComplete = normalized
    .filter(card => !KNOWN_MISSING_INDICATOR_DEVICES.has(String(card.name || '').trim()))
    .every(card =>
      Boolean(card.indicator) &&
      (card.comm === '开机' || card.comm === '关机' || card.comm === '离线'));
  const namesComplete = names.every(name => name && name !== '0-0001-KT');
  const namesUnique = new Set(names).size === normalized.length;
  const eligible =
    exactKnownSet &&
    knownFieldsComplete &&
    otherCardsComplete &&
    namesComplete &&
    namesUnique &&
    !qc.duplicateCollapse &&
    !qc.uniformTemplate;
  return {
    eligible,
    devices: unresolvedNames,
    details: `known-missing-indicator=${unresolvedNames.length}/${normalized.length} fields=${knownFieldsComplete ? 'ok' : 'bad'} others=${otherCardsComplete ? 'ok' : 'bad'} names=${namesComplete && namesUnique ? 'ok' : 'bad'}`,
  };
}

function classifyStableOfflineTemplatePage(cards, meta = {}, quality = null) {
  if (!Array.isArray(cards) || cards.length < 2) {
    return { eligible: false, details: 'not enough cards' };
  }
  const qc = quality || checkCardQuality(cards, meta);
  const names = cards.map(card => String(card && card.name || '').trim());
  const namesComplete = names.every(name => name && name !== '0-0001-KT');
  const namesUnique = new Set(names).size === cards.length;
  const indicatorsComplete = cards.every(card => Boolean(String(card.indicator || '').trim()));
  const offlineStateComplete = cards.every(card => card.comm === '离线' && card.switch === '-');
  const eligible = qc.uniformTemplate &&
    qc.allOffline &&
    !qc.duplicateCollapse &&
    namesComplete &&
    namesUnique &&
    indicatorsComplete &&
    offlineStateComplete;
  return {
    eligible,
    details: `offline-template=${qc.uniformTemplate ? 'yes' : 'no'} names=${namesComplete && namesUnique ? 'ok' : 'bad'} ind=${indicatorsComplete ? 'ok' : 'bad'} state=${offlineStateComplete ? 'ok' : 'bad'}${qc.duplicateCollapse ? ' duplicate-collapse' : ''}`,
  };
}

const ACCEPTED_CAPTURE_QUALITY_REASONS = new Set([
  'quality_pass',
  'offline_template_stable',
  'device_anomalies_preserved',
  'known_source_indicator_missing',
]);

function isAcceptedCaptureQualityReason(value) {
  return ACCEPTED_CAPTURE_QUALITY_REASONS.has(String(value || '').trim());
}

module.exports = {
  BLDG_ORDER,
  BLDG_META,
  PUBLIC_KEYWORDS,
  IND_MAP,
  KNOWN_MISSING_INDICATOR_DEVICES,
  getZuo5,
  getZuo6,
  getZone,
  isPublic,
  classifyAreaType,
  checkCardQuality,
  isValidIndoor,
  isRealIndoor,
  isValidSetTemp,
  classifyPersistentDeviceAnomalyPage,
  normalizeKnownSourceDefects,
  classifyKnownMissingIndicatorPage,
  classifyStableOfflineTemplatePage,
  isAcceptedCaptureQualityReason,
};
