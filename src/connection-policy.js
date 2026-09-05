'use strict';

const LOOPBACK_HOSTS = new Set(['127.0.0.1', 'localhost', '::1']);

function parseHttpUrl(value, allowCredentials = false) {
  try {
    const url = new URL(String(value || '').trim());
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;
    if (!allowCredentials && (url.username || url.password)) return null;
    return url;
  } catch {
    return null;
  }
}

function effectivePort(url) {
  return Number(url.port) || (url.protocol === 'https:' ? 443 : 80);
}

function pathFamilyMatches(candidate, configured) {
  const base = configured.pathname.replace(/\/+$/, '') || '/';
  const current = candidate.pathname.replace(/\/+$/, '') || '/';
  return current === base || current.startsWith(`${base}/`);
}

function isAllowedEmsUrl(candidate, configured) {
  const current = parseHttpUrl(candidate);
  const expected = parseHttpUrl(configured);
  return !!current && !!expected &&
    current.protocol === expected.protocol &&
    current.hostname.toLowerCase() === expected.hostname.toLowerCase() &&
    effectivePort(current) === effectivePort(expected) &&
    pathFamilyMatches(current, expected);
}

function isAllowedCdpUrl(candidate, allowRemote = false) {
  const url = parseHttpUrl(candidate);
  if (!url || !url.port) return false;
  return allowRemote || LOOPBACK_HOSTS.has(url.hostname.toLowerCase());
}

function sanitizeUrlForDisplay(value) {
  const url = parseHttpUrl(value, true);
  if (!url) return '<invalid-url>';
  url.username = '';
  url.password = '';
  url.search = '';
  url.hash = '';
  return url.toString().replace(/\/$/, '');
}

module.exports = {
  isAllowedEmsUrl,
  isAllowedCdpUrl,
  sanitizeUrlForDisplay,
};
