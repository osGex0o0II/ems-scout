'use strict';

function sanitizeUrlForDisplay(value) {
  try {
    const url = new URL(value);
    return `${url.protocol}//${url.host}${url.pathname}`;
  } catch {
    return '<invalid-url>';
  }
}

function sanitizeErrorForDisplay(error, sensitiveUrls = []) {
  let text = error && error.message ? String(error.message) : String(error || 'Unknown error');
  for (const value of sensitiveUrls.filter(Boolean)) {
    const safe = sanitizeUrlForDisplay(value);
    text = text.replaceAll(String(value), safe);
    text = text.replaceAll(encodeURIComponent(String(value)), encodeURIComponent(safe));
  }
  text = text.replace(/https?:\/\/[^\s"'<>]+/gi, match => sanitizeUrlForDisplay(match));
  return text.replace(/[\r\n]+/g, ' ').slice(0, 2000);
}

module.exports = { sanitizeErrorForDisplay, sanitizeUrlForDisplay };
