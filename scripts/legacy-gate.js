#!/usr/bin/env node
'use strict';

const envName = process.argv[2] || 'EMS_ENABLE_LEGACY_PANEL';
const label = process.argv[3] || 'legacy entry';

if (process.env[envName] === '1') {
  process.exit(0);
}

console.error(`${label} is disabled by default.`);
console.error('Current user export path: native app Data Management -> Export current filtered Excel.');
console.error(`To run intentionally, set ${envName}=1 and rerun the command.`);
process.exit(2);
