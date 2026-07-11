'use strict';

const { isIdentifier } = require('./workflow-event');

const PROGRESS_PREFIX = '[PROGRESS]';
const ACTION_PREFIX = '[ACTION]';

function adaptLegacyLine(line, fallbackStage) {
  if (typeof line !== 'string') return { kind: 'log', line: String(line ?? '') };

  if (line.startsWith(PROGRESS_PREFIX)) {
    const raw = line.slice(PROGRESS_PREFIX.length).trim();
    try {
      const data = JSON.parse(raw);
      if (!data || typeof data !== 'object' || Array.isArray(data)) {
        return { kind: 'malformed', marker: 'progress', line };
      }
      return {
        kind: 'progress',
        stage: legacyStage(data, fallbackStage),
        progress: canonicalProgress(data),
      };
    } catch {
      return { kind: 'malformed', marker: 'progress', line };
    }
  }

  if (line.startsWith(ACTION_PREFIX)) {
    const action = line.slice(ACTION_PREFIX.length).trim();
    if (!isIdentifier(action, 64, false)) {
      return { kind: 'malformed', marker: 'action', line };
    }
    return { kind: 'action', action };
  }

  return { kind: 'log', line };
}

function canonicalProgress(data) {
  const progress = {};
  const explicitPercent = finiteNumber(data.percent);
  const subAreaPercent = ratioPercent(data.curSa, data.totalSa);
  const devicePercent = ratioPercent(data.deviceDone, data.deviceTotal);
  const percent = explicitPercent ?? subAreaPercent ?? devicePercent;
  if (percent !== null) progress.percent = clamp(percent, 0, 100);

  if (typeof data.message === 'string' && data.message.trim()) {
    progress.message = data.message.trim();
  }

  const deviceDone = nonNegativeInteger(data.deviceDone);
  const deviceTotal = nonNegativeInteger(data.deviceTotal);
  const subAreaCurrent = nonNegativeInteger(data.curSa);
  const subAreaTotal = nonNegativeInteger(data.totalSa);
  const buildingCurrent = nonNegativeInteger(data.buildingIndex);
  const buildingTotal = nonNegativeInteger(data.buildingTotal);

  if (deviceDone !== null || deviceTotal !== null) {
    if (deviceDone !== null) progress.current = deviceDone;
    if (deviceTotal !== null) progress.total = deviceTotal;
    progress.unit = 'device';
  } else if (subAreaCurrent !== null || subAreaTotal !== null) {
    if (subAreaCurrent !== null) progress.current = subAreaCurrent;
    if (subAreaTotal !== null) progress.total = subAreaTotal;
    progress.unit = 'sub_area';
  } else if (buildingCurrent !== null || buildingTotal !== null) {
    if (buildingCurrent !== null) progress.current = buildingCurrent;
    if (buildingTotal !== null) progress.total = buildingTotal;
    progress.unit = 'building';
  }

  progress.data = data;
  return progress;
}

function legacyStage(data, fallbackStage) {
  return typeof data.phase === 'string' && isIdentifier(data.phase, 64, false)
    ? data.phase
    : fallbackStage;
}

function finiteNumber(value) {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function nonNegativeInteger(value) {
  return Number.isSafeInteger(value) && value >= 0 ? value : null;
}

function ratioPercent(current, total) {
  const numerator = finiteNumber(current);
  const denominator = finiteNumber(total);
  if (numerator === null || denominator === null || denominator <= 0) return null;
  return numerator / denominator * 100;
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

module.exports = {
  ACTION_PREFIX,
  PROGRESS_PREFIX,
  adaptLegacyLine,
  canonicalProgress,
};
