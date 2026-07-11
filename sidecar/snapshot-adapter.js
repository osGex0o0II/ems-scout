#!/usr/bin/env node
'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { stableStringify, validateSchema } = require('../scripts/audit-contracts');
const snapshotSchema = require('../contracts/collection-snapshot-v1.schema.json');

const CONTRACT_VERSION = 'ems.collection-snapshot/v1';
const HASH_SCOPE = 'canonical-buildings-payload';
const VERSION_FIELDS = new Set([
  'collector',
  'playwright',
  'rules',
  'databaseSchema',
  'sourceRevision',
]);
const DEFAULT_VERSIONS = Object.freeze({
  collector: 'legacy-enum-v5-adapter/1',
  playwright: 'legacy-source',
  rules: 'legacy-source',
  databaseSchema: 'legacy-v0',
  sourceRevision: 'unknown',
});
const ACCEPTED_QUALITY_REASONS = new Set(['quality_pass']);

function parseArguments(argv) {
  const options = { versions: {} };
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--help' || argument === '-h') return { help: true };
    const [name, inlineValue] = splitOption(argument);
    const value = inlineValue === null ? argv[++index] : inlineValue;
    if (value === undefined || value === '') throw argumentError(`missing value for ${name}`);

    switch (name) {
      case '--input': options.input = value; break;
      case '--output': options.output = value; break;
      case '--workflow-id': options.workflowId = value; break;
      case '--completed-at': options.completedAt = value; break;
      case '--scope': options.scope = parseJsonOrLiteral(value, 'scope'); break;
      case '--lineage': options.lineage = parseJsonOrLiteral(value, 'lineage'); break;
      case '--version':
      case '--versions':
        options.versions = { ...options.versions, ...parseVersions(value) };
        break;
      default:
        throw argumentError(`unknown option: ${name}`);
    }
  }

  for (const required of ['input', 'output', 'workflowId']) {
    if (!options[required]) throw argumentError(`--${camelToKebab(required)} is required`);
  }
  if (options.output !== '-' && path.resolve(options.input) === path.resolve(options.output)) {
    throw argumentError('--output must not overwrite --input');
  }
  return options;
}

function splitOption(argument) {
  if (!argument.startsWith('-')) throw argumentError(`unexpected positional argument: ${argument}`);
  const equals = argument.indexOf('=');
  return equals < 0
    ? [argument, null]
    : [argument.slice(0, equals), argument.slice(equals + 1)];
}

function camelToKebab(value) {
  return value.replace(/[A-Z]/g, char => `-${char.toLowerCase()}`);
}

function parseJsonOrLiteral(value, label) {
  const text = value.trim();
  if (!text.startsWith('{')) return text;
  try {
    return JSON.parse(text);
  } catch (error) {
    throw argumentError(`invalid ${label} JSON: ${error.message}`);
  }
}

function parseVersions(value) {
  const parsed = parseJsonOrLiteral(value, 'version');
  if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) return parsed;
  const equals = value.indexOf('=');
  if (equals <= 0 || equals === value.length - 1) {
    throw argumentError('--version must be a JSON object or name=value');
  }
  return { [value.slice(0, equals)]: value.slice(equals + 1) };
}

function argumentError(message) {
  const error = new Error(message);
  error.code = 'INVALID_ARGUMENT';
  return error;
}

function adaptLegacySnapshot(legacy, options = {}) {
  assertLegacySnapshot(legacy);
  const workflowId = requiredString(options.workflowId, 'workflowId');
  const completedAt = normalizeDateTime(options.completedAt ?? legacy.completedAt, 'completedAt');
  const versions = normalizeVersions(options.versions);

  const findings = [];
  const buildingKeys = new Set();
  const buildings = legacy.buildings.map((building, buildingIndex) =>
    adaptBuilding(building, buildingIndex, findings, buildingKeys));
  assertUniqueCardSourceKeys(buildings);
  const scope = normalizeScope(options.scope, buildings.map(building => building.building));
  const lineage = normalizeLineage(options.lineage);
  const counts = countSnapshot(buildings);
  const canonicalPayload = Buffer.from(stableStringify(buildings, 0), 'utf8');
  const decision = findings.some(finding => finding.severity === 'error')
    ? 'rejected'
    : findings.length > 0 ? 'accepted_with_findings' : 'accepted';

  const snapshot = {
    contractVersion: CONTRACT_VERSION,
    workflowId,
    completedAt,
    scope,
    lineage,
    versions,
    counts,
    quality: {
      decision,
      findings,
      retries: [],
    },
    artifact: {
      hashScope: HASH_SCOPE,
      sha256: crypto.createHash('sha256').update(canonicalPayload).digest('hex'),
      bytes: canonicalPayload.length,
    },
    buildings,
  };

  const errors = validateSchema(snapshot, snapshotSchema);
  if (errors.length > 0) {
    const error = new Error(`adapted snapshot does not satisfy CollectionSnapshot v1:\n${errors.join('\n')}`);
    error.code = 'CONTRACT_VALIDATION_FAILED';
    throw error;
  }
  return snapshot;
}

function assertLegacySnapshot(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new TypeError('legacy snapshot must be an object');
  }
  if (value.contractVersion !== undefined) {
    throw new TypeError('input already declares a contractVersion; expected legacy enum_full_v5 JSON');
  }
  if (!Array.isArray(value.buildings)) throw new TypeError('legacy snapshot buildings must be an array');
}

function adaptBuilding(raw, buildingIndex, findings, buildingKeys) {
  const value = plainObject(raw);
  const building = requiredString(value.building, `buildings[${buildingIndex}].building`);
  const sourceKey = uniqueSourceKey(`building:${encodeSourceComponent(building)}`, buildingKeys);
  const rawSubAreas = arrayOrEmpty(value.subAreas);
  const subAreaKeys = new Set();
  const subAreas = rawSubAreas.map((subArea, subAreaIndex) =>
    adaptSubArea(
      subArea,
      { building, buildingIndex, subAreaIndex, sourceKey },
      findings,
      subAreaKeys));
  const subAreaCount = nonNegativeInteger(value.subAreaCount, rawSubAreas.length);

  if (subAreaCount !== rawSubAreas.length) {
    findings.push(finding(
      'legacy.sub_area_count_mismatch',
      'warning',
      `Legacy subAreaCount=${subAreaCount}, but ${rawSubAreas.length} sub-area records were present`,
      sourceKey));
  }

  return {
    sourceKey,
    building,
    menuClicked: nullableString(value.menuClicked),
    subAreaCount,
    subAreas,
  };
}

function adaptSubArea(raw, location, findings, subAreaKeys) {
  const value = plainObject(raw);
  const idx = nonNegativeInteger(value.idx, location.subAreaIndex);
  const sourceKey = uniqueSourceKey(`${location.sourceKey}/sub-area:${idx}`, subAreaKeys);
  const text = requiredString(value.text, `sub-area ${sourceKey} text`);
  const rawPages = arrayOrEmpty(value.pages);
  const pageKeys = new Set();

  if (typeof value.err === 'string' && value.err.trim()) {
    findings.push(finding('legacy.sub_area_error', 'warning', value.err.trim(), sourceKey));
  } else if (value.pages !== undefined && !Array.isArray(value.pages)) {
    findings.push(finding(
      'legacy.invalid_pages',
      'error',
      'Legacy sub-area pages was not an array',
      sourceKey));
  }

  return {
    sourceKey,
    idx,
    floor: nullableNumber(value.floor, { zeroIsMissing: false }),
    floorLabel: nullableString(text),
    text,
    x: nullableNumber(value.x, { zeroIsMissing: false }),
    y: nullableNumber(value.y, { zeroIsMissing: false }),
    sourceEvidence: {
      err: sourceScalar(value.err),
    },
    pages: rawPages.map((page, pageIndex) => adaptPage(page, {
      ...location,
      subAreaIndex: location.subAreaIndex,
      subAreaIndexValue: idx,
      pageIndex,
      sourceKey,
    }, findings, pageKeys)),
  };
}

function adaptPage(raw, location, findings, pageKeys) {
  const value = plainObject(raw);
  const pageLabel = nullableString(value.page) || 'default';
  const sourceKey = uniqueSourceKey(
    `${location.sourceKey}/page:${encodeSourceComponent(pageLabel)}`,
    pageKeys);
  const rawCards = arrayOrEmpty(value.cards);
  const cardBySourceKey = new Map();
  const observationCounts = new Map();
  for (const [cardIndex, card] of rawCards.entries()) {
    const adapted = adaptCard(card, {
      ...location,
      cardIndex,
      pageLabel,
      sourceKey,
    });
    observationCounts.set(adapted.sourceKey, (observationCounts.get(adapted.sourceKey) || 0) + 1);
    if (!cardBySourceKey.has(adapted.sourceKey)) cardBySourceKey.set(adapted.sourceKey, adapted);
  }
  const cards = [...cardBySourceKey.values()];
  const rawCount = nonNegativeInteger(value.rawCount, rawCards.length);
  const sourceUniqueCount = nonNegativeInteger(value.uniqueCount, cards.length);
  const uniqueCount = cards.length;
  const reason = nullableString(value.qualityReason) || 'legacy_unclassified';
  const decision = ACCEPTED_QUALITY_REASONS.has(reason) ? 'accepted' : 'accepted_with_findings';
  const duplicates = adaptDuplicates(value.duplicateNames, cards, observationCounts, {
    building: location.building,
    subAreaIndex: location.subAreaIndexValue,
    pageName: pageLabel,
  });

  if (decision !== 'accepted') {
    findings.push(finding(
      `legacy.page_quality.${safeFindingCode(reason)}`,
      'warning',
      `Legacy page quality decision: ${reason}`,
      sourceKey));
  }
  if (typeof value.err === 'string' && value.err.trim()) {
    findings.push(finding('legacy.page_error', 'warning', value.err.trim(), sourceKey));
  }
  if (rawCount < cards.length || sourceUniqueCount !== cards.length) {
    findings.push(finding(
      'legacy.page_count_mismatch',
      'warning',
      `Legacy page counts raw=${rawCount}, unique=${sourceUniqueCount}, records=${cards.length}`,
      sourceKey));
  }
  const duplicateExtras = duplicates.reduce((total, duplicate) => total + duplicate.copies - 1, 0);
  if (rawCount - uniqueCount !== duplicateExtras) {
    throw new TypeError(
      `page ${sourceKey} duplicate evidence accounts for ${duplicateExtras} observations, ` +
      `but raw-unique=${rawCount - uniqueCount}`);
  }

  return {
    sourceKey,
    page: pageLabel,
    rawCount,
    uniqueCount,
    duplicates,
    layout: nullableString(value.layout),
    quality: {
      decision,
      reason,
      attempts: 1,
    },
    sourceEvidence: {
      count: sourceScalar(value.count),
      onHref: sourceScalar(value.onHref),
      offHref: sourceScalar(value.offHref),
      qualityReason: sourceScalar(value.qualityReason),
      duplicateNames: cloneJsonValue(value.duplicateNames),
      err: sourceScalar(value.err),
    },
    cards,
  };
}

function adaptCard(raw, location) {
  const value = plainObject(raw);
  const name = requiredString(value.name, `card ${location.cardIndex} name`);
  const nameFloor = nullableInteger(value._nameFloor);
  const sourceKey = buildDeviceSourceKey({
    building: location.building,
    subAreaIndex: location.subAreaIndexValue,
    pageName: location.pageLabel,
    deviceName: name,
  });

  return {
    sourceKey,
    deviceUid: null,
    name,
    switch: normalizeSwitch(value.switch),
    mode: nullableString(value.mode, { zeroIsMissing: true }),
    indoor: nullableNumber(value.indoor, { zeroIsMissing: true }),
    setTemp: nullableNumber(value.setTemp, { zeroIsMissing: true }),
    fan: nullableString(value.fan, { zeroIsMissing: true }),
    indicator: nullableString(value.indicator),
    comm: normalizeComm(value.comm),
    sourceEvidence: {
      raw: {
        name: sourceScalar(value.name),
        switch: sourceScalar(value.switch),
        mode: sourceScalar(value.mode),
        indoor: sourceScalar(value.indoor),
        setTemp: sourceScalar(value.setTemp),
        fan: sourceScalar(value.fan),
        indicator: sourceScalar(value.indicator),
        comm: sourceScalar(value.comm),
      },
      nameFloor,
    },
  };
}

function adaptDuplicates(rawDuplicates, cards, observationCounts, identity) {
  const cardKeysByName = new Map();
  for (const card of cards) {
    const keys = cardKeysByName.get(card.name) || [];
    keys.push(card.sourceKey);
    cardKeysByName.set(card.name, keys);
  }

  const declared = arrayOrEmpty(rawDuplicates);
  const duplicateMap = new Map();
  for (const raw of declared) {
    const value = typeof raw === 'string' ? { name: raw } : plainObject(raw);
    const name = nullableString(value.name);
    if (!name) continue;
    const primaryKey = buildDeviceSourceKey({ ...identity, deviceName: name });
    const observedCopies = observationCounts.get(primaryKey) || 0;
    const copies = Math.max(2, nonNegativeInteger(value.copies, 2), observedCopies);
    duplicateMap.set(name, Math.max(duplicateMap.get(name) || 0, copies));
  }
  for (const card of cards) {
    const observedCopies = observationCounts.get(card.sourceKey) || 0;
    if (observedCopies > 1) {
      duplicateMap.set(card.name, Math.max(observedCopies, duplicateMap.get(card.name) || 0));
    }
  }

  return [...duplicateMap.entries()].map(([name, copies]) => {
    if (!(cardKeysByName.get(name) || []).length) {
      throw new TypeError(`duplicate evidence references absent card: ${name}`);
    }
    const sourceKeys = Array.from({ length: copies }, (_, index) => buildDeviceSourceKey({
      ...identity,
      deviceName: name,
      occurrence: index + 1,
    }));
    return { name, copies, sourceKeys };
  });
}

function buildDeviceSourceKey({ building, subAreaIndex, pageName, deviceName, occurrence = 1 }) {
  if (!Number.isSafeInteger(subAreaIndex)) throw new TypeError('subAreaIndex must be a safe integer');
  if (!Number.isSafeInteger(occurrence) || occurrence < 1) {
    throw new TypeError('occurrence must be a positive safe integer');
  }
  const components = [
    normalizeIdentityText(building, 'building'),
    String(subAreaIndex),
    normalizeIdentityText(pageName, 'pageName'),
    normalizeIdentityText(deviceName, 'deviceName'),
  ];
  if (occurrence > 1) components.push(String(occurrence));

  let canonical = 'ems.source-key/v1;';
  for (const component of components) {
    canonical += `${Buffer.byteLength(component, 'utf8')}:${component};`;
  }
  return `sk1_${crypto.createHash('sha256').update(canonical, 'utf8').digest('hex')}`;
}

function normalizeIdentityText(value, label) {
  return requiredString(value, label).normalize('NFC').toUpperCase();
}

function uniqueSourceKey(candidate, seen) {
  let result = candidate;
  let occurrence = 2;
  while (seen.has(result)) result = `${candidate}/occurrence:${occurrence++}`;
  seen.add(result);
  return result;
}

function assertUniqueCardSourceKeys(buildings) {
  const sourceKeys = new Set();
  for (const building of buildings) {
    for (const subArea of building.subAreas) {
      for (const page of subArea.pages) {
        for (const card of page.cards) {
          if (sourceKeys.has(card.sourceKey)) {
            throw new TypeError(`duplicate canonical card source identity: ${card.sourceKey}`);
          }
          sourceKeys.add(card.sourceKey);
        }
      }
    }
  }
}

function encodeSourceComponent(value) {
  return encodeURIComponent(String(value)).replace(/!/g, '%21').replace(/'/g, '%27')
    .replace(/\(/g, '%28').replace(/\)/g, '%29').replace(/\*/g, '%2A');
}

function normalizeSwitch(value) {
  if (typeof value !== 'string') return null;
  const normalized = value.trim().toUpperCase();
  return normalized === 'ON' || normalized === 'OFF' ? normalized : null;
}

function normalizeComm(value) {
  const normalized = nullableString(value);
  return ['开机', '关机', '离线', '未知'].includes(normalized) ? normalized : '未知';
}

function nullableString(value, options = {}) {
  if (value === null || value === undefined) return null;
  if (typeof value !== 'string') value = String(value);
  const normalized = value.trim();
  if (!normalized || normalized === '-') return null;
  if (options.zeroIsMissing && normalized === '0') return null;
  return normalized;
}

function nullableNumber(value, options = {}) {
  if (value === null || value === undefined || value === '' || value === '-') return null;
  if (options.zeroIsMissing && (value === 0 || value === '0')) return null;
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value !== 'string') return null;
  const normalized = value.trim();
  if (!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/.test(normalized)) return null;
  const number = Number(normalized);
  return Number.isFinite(number) ? number : null;
}

function nullableInteger(value) {
  if (Number.isInteger(value)) return value;
  if (typeof value === 'string' && /^[+-]?\d+$/.test(value.trim())) {
    const number = Number(value);
    return Number.isSafeInteger(number) ? number : null;
  }
  return null;
}

function sourceScalar(value) {
  if (value === null || value === undefined) return null;
  return ['string', 'number', 'boolean'].includes(typeof value) &&
    (typeof value !== 'number' || Number.isFinite(value))
    ? value
    : null;
}

function cloneJsonValue(value) {
  if (value === undefined) return null;
  if (Array.isArray(value)) return value.map(cloneJsonValue);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, cloneJsonValue(item)]));
  }
  return sourceScalar(value);
}

function normalizeScope(value, buildingNames) {
  if (value === undefined || value === null || value === 'full') {
    return { mode: 'full', buildings: uniqueStrings(buildingNames), targets: [] };
  }
  if (typeof value === 'string') {
    if (!['full', 'building', 'sub_area', 'recapture', 'append'].includes(value)) {
      throw new TypeError(`unknown scope mode: ${value}`);
    }
    return { mode: value, buildings: uniqueStrings(buildingNames), targets: [] };
  }
  const scope = plainObject(value);
  return {
    mode: scope.mode,
    buildings: uniqueStrings(arrayOrEmpty(scope.buildings)),
    targets: uniqueStrings(arrayOrEmpty(scope.targets)),
  };
}

function normalizeLineage(value) {
  if (value === undefined || value === null || value === 'none') {
    return { baseArtifactSha256: null, parentWorkflowId: null };
  }
  if (typeof value !== 'object' || Array.isArray(value)) throw new TypeError('lineage must be a JSON object');
  return {
    baseArtifactSha256: nullableString(value.baseArtifactSha256),
    parentWorkflowId: nullableString(value.parentWorkflowId),
  };
}

function normalizeVersions(value) {
  const versions = { ...DEFAULT_VERSIONS, ...plainObject(value) };
  for (const key of Object.keys(versions)) {
    if (!VERSION_FIELDS.has(key)) throw new TypeError(`unknown version field: ${key}`);
  }
  return Object.fromEntries([...VERSION_FIELDS].map(key => [key, requiredString(versions[key], `versions.${key}`)]));
}

function normalizeDateTime(value, label) {
  const text = requiredString(value, label);
  const parsed = new Date(text);
  if (!Number.isFinite(parsed.getTime())) throw new TypeError(`${label} must be an RFC 3339 date-time`);
  return parsed.toISOString();
}

function countSnapshot(buildings) {
  let subAreaCount = 0;
  let pageCount = 0;
  let rawCardCount = 0;
  let uniqueCardCount = 0;
  for (const building of buildings) {
    subAreaCount += building.subAreas.length;
    for (const subArea of building.subAreas) {
      pageCount += subArea.pages.length;
      for (const page of subArea.pages) {
        rawCardCount += page.rawCount;
        uniqueCardCount += page.cards.length;
      }
    }
  }
  return {
    buildingCount: buildings.length,
    subAreaCount,
    pageCount,
    rawCardCount,
    uniqueCardCount,
  };
}

function requiredString(value, label) {
  const normalized = nullableString(value);
  if (!normalized) throw new TypeError(`${label} must be a non-empty string`);
  return normalized;
}

function nonNegativeInteger(value, fallback) {
  return Number.isInteger(value) && value >= 0 ? value : fallback;
}

function uniqueStrings(values) {
  return [...new Set(values.map(value => requiredString(value, 'scope value')))];
}

function arrayOrEmpty(value) {
  return Array.isArray(value) ? value : [];
}

function plainObject(value) {
  return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

function finding(code, severity, message, sourceKey) {
  return { code, severity, message, sourceKey };
}

function safeFindingCode(value) {
  return value.toLowerCase().replace(/[^a-z0-9._-]+/g, '_').replace(/^_+|_+$/g, '') || 'unknown';
}

function serializeSnapshot(snapshot) {
  return `${stableStringify(snapshot, 2)}\n`;
}

function readLegacySnapshot(inputPath) {
  const text = fs.readFileSync(inputPath, 'utf8').replace(/^\uFEFF/, '');
  return JSON.parse(text);
}

function writeOutput(outputPath, text) {
  if (outputPath === '-') {
    process.stdout.write(text);
    return;
  }
  const absolute = path.resolve(outputPath);
  fs.mkdirSync(path.dirname(absolute), { recursive: true });
  const temporary = `${absolute}.tmp-${process.pid}`;
  try {
    fs.writeFileSync(temporary, text, { encoding: 'utf8', flag: 'wx' });
    fs.renameSync(temporary, absolute);
  } finally {
    fs.rmSync(temporary, { force: true });
  }
}

function usage() {
  return [
    'Usage: node sidecar/snapshot-adapter.js --input <legacy.json> --output <v1.json|-> --workflow-id <id> [options]',
    '',
    'Options:',
    '  --completed-at <RFC3339>       Override the legacy completedAt value',
    '  --scope <mode|JSON>             Collection scope metadata (default: full)',
    '  --lineage <none|JSON>           Base artifact and parent workflow metadata',
    '  --version <name=value|JSON>     Override one or more version fields; repeatable',
  ].join('\n');
}

function main(argv = process.argv.slice(2)) {
  let options;
  try {
    options = parseArguments(argv);
    if (options.help) {
      process.stdout.write(`${usage()}\n`);
      return 0;
    }
    const legacy = readLegacySnapshot(options.input);
    const snapshot = adaptLegacySnapshot(legacy, options);
    writeOutput(options.output, serializeSnapshot(snapshot));
    return 0;
  } catch (error) {
    process.stderr.write(`[snapshot-adapter] ${error.message}\n`);
    if (error.code === 'INVALID_ARGUMENT') process.stderr.write(`${usage()}\n`);
    return 1;
  }
}

if (require.main === module) process.exitCode = main();

module.exports = {
  CONTRACT_VERSION,
  DEFAULT_VERSIONS,
  HASH_SCOPE,
  adaptLegacySnapshot,
  buildDeviceSourceKey,
  countSnapshot,
  main,
  parseArguments,
  serializeSnapshot,
};
