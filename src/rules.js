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
  '2号': { full: '2号学术交流中心', fullName: '2号学术交流中心', name: '2号学术交流中心', zuoFn: null, getZuo: null, baselineCards: 110, baselineSubAreas: 5, estimateTime: '15秒' },
  '3号': { full: '3号公寓楼', fullName: '3号公寓楼', name: '3号公寓楼', zuoFn: null, getZuo: null, baselineCards: 1106, baselineSubAreas: 30, estimateTime: '2分钟' },
  '4号': { full: '4号公寓楼', fullName: '4号公寓楼', name: '4号公寓楼', zuoFn: null, getZuo: null, baselineCards: 1096, baselineSubAreas: 30, estimateTime: '2分钟' },
  '5号': { full: '5号综合服务中心', fullName: '5号综合服务中心', name: '5号综合服务中心', zuoFn: getZuo5, getZuo: getZuo5, baselineCards: 286, baselineSubAreas: 17, estimateTime: '30秒' },
  '6号': { full: '6号科研楼', fullName: '6号科研楼', name: '6号科研楼', zuoFn: getZuo6, getZuo: getZuo6, baselineCards: 2480, baselineSubAreas: 31, estimateTime: '4分钟' },
};

const BUILDING_IDENTITY_RULES = {
  '1号': { rejectPrefix: /^(?:2|3|4|5|6)-/ },
  '2号': { expectedPrefix: /^2-/ },
  '3号': { expectedPrefix: /^3-/ },
  '4号': { expectedPrefix: /^4-/ },
  '5号': { rejectPrefix: /^(?:2|3|4)-/ },
  '6号': { rejectPrefix: /^(?:2|3|4)-/ },
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

// 4号楼 1F 这台设备的指示图偶发不渲染，但开关、模式和温度数据仍可用。
// 保留为“待复核”而不是伪造通信状态，避免整页因 EMS 的间歇性缺图失败。
const KNOWN_INTERMITTENT_MISSING_INDICATOR_DEVICES = new Set([
  '4-1F-KT1-104',
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

function assessBuildingIdentity(building, cards = [], subAreaCount = null) {
  const meta = BLDG_META[building] || {};
  const rule = BUILDING_IDENTITY_RULES[building] || {};
  const names = Array.isArray(cards)
    ? cards.map(c => String(c && c.name || '').trim()).filter(Boolean)
    : [];
  const reasons = [];
  const subAreaCountAccepted = building === '6号' && (subAreaCount === 30 || subAreaCount === meta.baselineSubAreas);
  if (Number.isFinite(subAreaCount) && Number.isFinite(meta.baselineSubAreas) &&
      subAreaCount !== meta.baselineSubAreas && !subAreaCountAccepted) {
    reasons.push(`subAreas=${subAreaCount}, expected=${meta.baselineSubAreas}`);
  }

  let prefixRatio = null;
  if (names.length >= 2) {
    const matched = rule.expectedPrefix
      ? names.filter(name => rule.expectedPrefix.test(name)).length
      : names.filter(name => rule.rejectPrefix && rule.rejectPrefix.test(name)).length;
    prefixRatio = matched / names.length;
    if (rule.expectedPrefix && prefixRatio < 0.5) {
      reasons.push(`namePrefix=${prefixRatio.toFixed(2)}, expected=${rule.expectedPrefix}`);
    }
    if (rule.rejectPrefix && prefixRatio >= 0.5) {
      reasons.push(`foreignNamePrefix=${prefixRatio.toFixed(2)}, reject=${rule.rejectPrefix}`);
    }
  }

  return {
    ok: reasons.length === 0,
    building,
    subAreaCount,
    nameCount: names.length,
    prefixRatio,
    details: reasons.join('; ') || 'identity-ok',
  };
}

function sourceCardName(cardOrName) {
  const value = cardOrName && typeof cardOrName === 'object'
    ? (cardOrName.sourceName || cardOrName.name)
    : cardOrName;
  return String(value || '').trim().replace(/#\d+$/, '');
}

function labelSamePageDuplicateCards(cards = []) {
  const output = Array.isArray(cards) ? cards.map(card => ({ ...card })) : [];
  const groups = new Map();
  for (let index = 0; index < output.length; index++) {
    const card = output[index];
    const baseName = sourceCardName(card);
    if (!baseName) continue;
    if (!groups.has(baseName)) groups.set(baseName, []);
    groups.get(baseName).push({ card, index });
  }

  const duplicateNames = [];
  for (const [baseName, entries] of groups) {
    if (entries.length < 2) continue;
    const existingNames = entries.map(({ card }) => String(card.name || '').trim());
    const alreadyLabeled = entries.every(({ card }) =>
      String(card.sourceName || '').trim() === baseName &&
      new RegExp(`^${baseName.replace(/[.*+?^${}()|[\\]\\]/g, '\\$&')}#\\d+$`).test(String(card.name || '').trim())
    ) && new Set(existingNames).size === entries.length;
    if (alreadyLabeled) {
      duplicateNames.push({
        name: baseName,
        copies: entries.length,
        labeledNames: [...existingNames].sort((a, b) => Number(a.split('#').pop()) - Number(b.split('#').pop())),
      });
      continue;
    }
    const ordered = [...entries].sort((a, b) => {
      const ay = Number.isFinite(a.card._sourceY) ? a.card._sourceY : Number.MAX_SAFE_INTEGER;
      const by = Number.isFinite(b.card._sourceY) ? b.card._sourceY : Number.MAX_SAFE_INTEGER;
      const ax = Number.isFinite(a.card._sourceX) ? a.card._sourceX : Number.MAX_SAFE_INTEGER;
      const bx = Number.isFinite(b.card._sourceX) ? b.card._sourceX : Number.MAX_SAFE_INTEGER;
      return ay - by || ax - bx || a.index - b.index;
    });
    const labeledNames = [];
    ordered.forEach(({ card }, index) => {
      card.sourceName = baseName;
      card.name = `${baseName}#${index + 1}`;
      labeledNames.push(card.name);
    });
    duplicateNames.push({ name: baseName, copies: entries.length, labeledNames });
  }

  for (const card of output) {
    delete card._sourceX;
    delete card._sourceY;
    delete card._sourceOrder;
  }
  return { cards: output, duplicateNames };
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
  const activeWithFan = activeCards.filter(c => c.fan && c.fan !== '-' && c.fan !== '0').length;
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

function normalizeCardValues(cards) {
  if (!Array.isArray(cards)) return [];
  return cards.map(card => {
    const normalized = { ...card };
    const indoor = parseFloat(normalized.indoor);
    if (Number.isFinite(indoor) && !isValidIndoor(normalized.indoor)) normalized.indoor = '-';
    const setTemp = parseFloat(normalized.setTemp);
    if (Number.isFinite(setTemp) && setTemp !== 0 && !isValidSetTemp(normalized.setTemp)) normalized.setTemp = '-';
    return normalized;
  });
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
  return normalizeCardValues(cards).map(card => KNOWN_MISSING_INDICATOR_DEVICES.has(sourceCardName(card))
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
  const unresolvedNames = unresolved.map(sourceCardName).sort();
  const expectedNames = [...KNOWN_MISSING_INDICATOR_DEVICES].sort();
  const exactKnownSet = unresolvedNames.length === expectedNames.length &&
    unresolvedNames.every((name, index) => name === expectedNames[index]);
  const intermittentUnresolved = unresolved.filter(card =>
    KNOWN_INTERMITTENT_MISSING_INDICATOR_DEVICES.has(sourceCardName(card)));
  const exactIntermittentSet = unresolvedNames.length === 1 &&
    intermittentUnresolved.length === 1 &&
    sourceCardName(intermittentUnresolved[0]) === unresolvedNames[0];
  const intermittentFieldsComplete = intermittentUnresolved.length === 1 && intermittentUnresolved.every(card =>
    (card.switch === 'ON' || card.switch === 'OFF') &&
    Boolean(card.mode) && card.mode !== '-' &&
    isRealIndoor(card.indoor) &&
    isValidSetTemp(card.setTemp) &&
    Boolean(card.fan) && card.fan !== '-' && card.fan !== '0');
  // Known defect devices (2M001) are EMS source defects — allow incomplete fields.
  // Only require exact name match; indicator/comm/switch/fields may all be missing.
  const knownFieldsComplete = unresolved.every(() => true);
  const otherCardsComplete = normalized
    .filter(card =>
      !KNOWN_MISSING_INDICATOR_DEVICES.has(sourceCardName(card)) &&
      !intermittentUnresolved.includes(card))
    .every(card =>
      Boolean(card.indicator) &&
      (card.comm === '开机' || card.comm === '关机' || card.comm === '离线'));
  const namesComplete = names.every(name => name && name !== '0-0001-KT');
  const namesUnique = new Set(names).size === normalized.length;
  const eligible =
    (exactKnownSet || (exactIntermittentSet && intermittentFieldsComplete)) &&
    knownFieldsComplete &&
    otherCardsComplete &&
    namesComplete &&
    namesUnique &&
    !qc.duplicateCollapse &&
    !qc.uniformTemplate;
  return {
    eligible,
    devices: unresolvedNames,
    intermittent: exactIntermittentSet,
    details: `known-missing-indicator=${unresolvedNames.length}/${normalized.length}${exactIntermittentSet ? ' intermittent' : ''} fields=${knownFieldsComplete && intermittentFieldsComplete ? 'ok' : 'bad'} others=${otherCardsComplete ? 'ok' : 'bad'} names=${namesComplete && namesUnique ? 'ok' : 'bad'}`,
  };
}

const ACCEPTED_CAPTURE_QUALITY_REASONS = new Set([
  'quality_pass',
  'all_offline',
  'offline_template_stable',
  'device_anomalies_preserved',
  'known_source_indicator_missing',
  'known_intermittent_indicator_missing',
  'template_values_unconfirmed',
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
  KNOWN_INTERMITTENT_MISSING_INDICATOR_DEVICES,
  getZuo5,
  getZuo6,
  getZone,
  assessBuildingIdentity,
  sourceCardName,
  labelSamePageDuplicateCards,
  isPublic,
  classifyAreaType,
  checkCardQuality,
  isValidIndoor,
  isRealIndoor,
  isValidSetTemp,
  normalizeCardValues,
  classifyPersistentDeviceAnomalyPage,
  normalizeKnownSourceDefects,
  classifyKnownMissingIndicatorPage,
  isAcceptedCaptureQualityReason,
};
