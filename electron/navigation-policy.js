'use strict';

function parse(value) {
  try {
    return new URL(value);
  } catch {
    return null;
  }
}

function isSamePanelOrigin(candidate, panelUrl) {
  const target = parse(candidate);
  const panel = parse(panelUrl);
  return Boolean(target && panel && target.origin === panel.origin);
}

function isExternalBrowserUrl(candidate) {
  const target = parse(candidate);
  return Boolean(target && target.protocol === 'https:');
}

module.exports = { isExternalBrowserUrl, isSamePanelOrigin };
