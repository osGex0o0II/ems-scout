'use strict';

const path = require('path');

function argumentValue(argv, name) {
  const hit = argv.find(argument => argument.startsWith(name + '='));
  return hit ? hit.slice(name.length + 1) : '';
}

function normalizeEmsUrl(value) {
  try {
    const url = new URL(value);
    url.hash = '';
    return url.toString();
  } catch {
    return value;
  }
}

function matchesEmsPageUrl(value, expectedValue) {
  try {
    const expected = new URL(expectedValue);
    const current = new URL(value);
    return current.host === expected.host &&
      (current.pathname.includes('/ui') || expected.pathname.includes(current.pathname));
  } catch {
    return value.includes('172.29.248.4') || value.includes('localhost') || value.includes('/ui');
  }
}

function parseEnumerateOptions(argv = process.argv.slice(2), env = process.env, root = path.resolve(__dirname, '..')) {
  const rawEmsUrl = env.EMS_URL || 'http://172.29.248.4:8000/ui';
  const buildingArgument = argv.find(argument => argument.startsWith('--bldg='));
  const recaptureArgument = argv.find(argument => argument.startsWith('--recapture='));
  const recaptureTargets = recaptureArgument
    ? recaptureArgument.slice('--recapture='.length).split(',').map(value => {
      const [building, x, y] = value.split(':');
      return { building, x: parseInt(x), y: parseInt(y) };
    })
    : [];
  const outDirectory = path.resolve(argumentValue(argv, '--out-dir') || env.EMS_OUT_DIR || path.join(root, 'out'));

  return {
    EMS_URL: normalizeEmsUrl(rawEmsUrl),
    CDP_URL: argumentValue(argv, '--cdp-url') || env.CDP_URL || 'http://127.0.0.1:9222',
    OUT_DIR: outDirectory,
    OUT_FILE: path.join(outDirectory, 'enum_full_v5.json'),
    ENABLE_NETWORK_MONITOR: !argv.includes('--no-net-monitor'),
    ENABLE_SELF_DIAGNOSE: argv.includes('--self-diagnose'),
    USE_CDP: argv.includes('--edge'),
    USE_AUTO_LAUNCH: argv.includes('--auto-launch'),
    DRY_RUN: argv.includes('--dry'),
    APPEND: argv.includes('--append'),
    CHECK_LOGIN: !argv.includes('--skip-login-check'),
    FAIL_IF_NOT_LOGGED_IN: argv.includes('--fail-if-not-logged-in'),
    FILTER: buildingArgument
      ? buildingArgument.slice('--bldg='.length).split(',').map(value => value.trim()).filter(Boolean)
      : null,
    RECAPTURE_TARGETS: recaptureTargets,
    RECAPTURE_MODE: recaptureTargets.length > 0,
    LOG_LEVEL: argumentValue(argv, '--log-level'),
    LOG_CATEGORIES: argumentValue(argv, '--log-category'),
    LOG_FILE: argv.includes('--log-file'),
  };
}

module.exports = {
  matchesEmsPageUrl,
  normalizeEmsUrl,
  parseEnumerateOptions,
};
