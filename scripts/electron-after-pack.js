'use strict';

const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const ROOT = path.join(__dirname, '..');

function rel(file) {
  return path.relative(ROOT, file);
}

function fail(message) {
  console.error('[electron-after-pack] ' + message);
  process.exit(1);
}

module.exports = async function afterPack(context) {
  if (context.electronPlatformName !== 'win32') return;

  const electronPkg = require(path.join(ROOT, 'node_modules', 'electron', 'package.json'));
  const productFilename = context.packager && context.packager.appInfo && context.packager.appInfo.productFilename
    ? context.packager.appInfo.productFilename
    : 'EMS Scout Legacy';
  const appDir = path.join(context.appOutDir, 'resources', 'app');
  const nativeFile = path.join(appDir, 'node_modules', 'better-sqlite3', 'build', 'Release', 'better_sqlite3.node');
  if (!fs.existsSync(nativeFile)) {
    fail('packaged native module not found: ' + rel(nativeFile));
  }

  const content = fs.readFileSync(nativeFile);
  fs.unlinkSync(nativeFile);
  fs.writeFileSync(nativeFile, content);

  const electronRebuildCli = path.join(ROOT, 'node_modules', '@electron', 'rebuild', 'lib', 'cli.js');
  if (!fs.existsSync(electronRebuildCli)) {
    fail('electron-rebuild CLI not found: ' + rel(electronRebuildCli));
  }

  const rebuild = spawnSync(process.execPath, [
    electronRebuildCli,
    `--version=${electronPkg.version}`,
    '--module-dir',
    appDir,
    '--only',
    'better-sqlite3',
    '--force',
  ], {
    cwd: ROOT,
    stdio: 'inherit',
    env: process.env,
  });

  if (rebuild.error) fail('electron-rebuild failed to start: ' + rebuild.error.message);
  if (rebuild.status !== 0) process.exit(rebuild.status || 1);

  const packagedElectronExe = path.join(context.appOutDir, `${productFilename}.exe`);
  const load = spawnSync(packagedElectronExe, ['-e', "require('better-sqlite3'); console.log('packaged native ok')"], {
    cwd: appDir,
    stdio: 'inherit',
    env: {
      ...process.env,
      ELECTRON_RUN_AS_NODE: '1',
    },
  });
  if (load.error) fail('packaged native check failed to start: ' + load.error.message);
  if (load.status !== 0) process.exit(load.status || 1);

  console.log('[electron-after-pack] packaged Electron ABI ready:', rel(nativeFile));
};
