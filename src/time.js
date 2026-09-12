'use strict';

function formatLocalTimestamp(value = new Date()) {
  const instant = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(instant.getTime())) {
    throw new RangeError('Invalid timestamp');
  }

  const offsetMinutes = -instant.getTimezoneOffset();
  const localWallTime = new Date(instant.getTime() + offsetMinutes * 60_000)
    .toISOString()
    .slice(0, 23);
  const sign = offsetMinutes >= 0 ? '+' : '-';
  const absoluteMinutes = Math.abs(offsetMinutes);
  const hours = String(Math.floor(absoluteMinutes / 60)).padStart(2, '0');
  const minutes = String(absoluteMinutes % 60).padStart(2, '0');
  return `${localWallTime}${sign}${hours}:${minutes}`;
}

function parseTimestampMillis(value) {
  if (value === null || value === undefined) return 0;
  const text = String(value).trim();
  if (!text) return 0;

  // Collection subprocesses historically received epoch milliseconds. New
  // desktop launches pass an explicit local offset so the value is readable.
  if (/^[+-]?\d+(?:\.\d+)?$/.test(text)) {
    const epoch = Number(text);
    return Number.isFinite(epoch) && epoch > 0 ? epoch : 0;
  }

  const parsed = Date.parse(text);
  return Number.isNaN(parsed) ? 0 : parsed;
}

module.exports = { formatLocalTimestamp, parseTimestampMillis };
