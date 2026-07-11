'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const root = path.join(__dirname, '..', '..');

function read(relativePath) {
  return fs.readFileSync(path.join(root, relativePath), 'utf8');
}

function filesBelow(directory, extension) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const absolute = path.join(directory, entry.name);
    if (entry.isDirectory()) return filesBelow(absolute, extension);
    return entry.isFile() && absolute.endsWith(extension) ? [absolute] : [];
  });
}

test('desktop product uses SQLite and native quality as its data backbone', () => {
  const app = read('native/src/EmsScout.Desktop/App.xaml.cs');
  assert.match(app, /SqliteInventorySnapshotSource/);
  assert.match(app, /SqliteQualityAuditService/);
  assert.match(app, /CollectionSnapshotImporter/);
  assert.doesNotMatch(app, /EnumFullV5SnapshotSource/);
  assert.doesNotMatch(app, /JsonQualityAuditService/);
});

test('unused C# Playwright experiment is absent from the native solution', () => {
  const solution = read('native/EmsScout.Native.slnx');
  assert.doesNotMatch(solution, /EmsScout\.Collection/);
  assert.equal(fs.existsSync(path.join(root, 'native/src/EmsScout.Collection/EmsScout.Collection.csproj')), false);
});

test('current realtime reader belongs to Infrastructure without a legacy assembly', () => {
  const solution = read('native/EmsScout.Native.slnx');
  assert.doesNotMatch(solution, /EmsScout\.Legacy/);
  assert.equal(fs.existsSync(path.join(root, 'native/src/EmsScout.Legacy/EmsScout.Legacy.csproj')), false);
  assert.equal(fs.existsSync(path.join(
    root,
    'native/src/EmsScout.Infrastructure/Realtime/RealtimeLatestJsonSource.cs')),
  true);
});

test('ambiguous Node quality command is explicitly legacy-only', () => {
  const packageJson = JSON.parse(read('package.json'));
  assert.equal(packageJson.scripts.quality, undefined);
  assert.equal(packageJson.scripts['legacy:quality'], 'node scripts/quality-report.js');
});

test('only the versioned migration layer may change SQLite schema', () => {
  const infrastructure = path.join(root, 'native/src/EmsScout.Infrastructure');
  const offenders = filesBelow(infrastructure, '.cs')
    .filter(file => !file.includes(`${path.sep}Migrations${path.sep}`))
    .filter(file => /\b(?:CREATE\s+TABLE|ALTER\s+TABLE)\b/i.test(fs.readFileSync(file, 'utf8')))
    .map(file => path.relative(root, file));
  assert.deepEqual(offenders, []);
});

test('legacy removal gates distinguish product Sidecar files from protected fallbacks', () => {
  const inventory = read('docs/legacy-inventory.md');
  assert.match(inventory, /src\/enumerate\.js[^\n]*\| product \|/);
  assert.match(inventory, /scripts\/import\.js[^\n]*\| protected \|/);
  assert.match(inventory, /scripts\/quality-report\.js[^\n]*\| protected, explicitly named \|/);
  assert.match(inventory, /src\/panel\/[^\n]*\| protected, disabled by environment gate \|/);
  assert.match(inventory, /electron\/[^\n]*\| protected, disabled by environment gate \|/);
  assert.match(inventory, /real-EMS single-building parity/);
});

test('collection task view model delegates environment probes and progress parsing', () => {
  const viewModel = read('native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs');
  const app = read('native/src/EmsScout.Desktop/App.xaml.cs');
  const probe = read('native/src/EmsScout.Infrastructure/Sidecar/CollectionEnvironmentProbe.cs');
  const progress = read('native/src/EmsScout.Application/Workflows/CollectionProgressPresentation.cs');

  assert.match(viewModel, /CollectionEnvironmentProbe environmentProbe/);
  assert.match(viewModel, /CollectionProgressPresenter\.Parse/);
  assert.doesNotMatch(viewModel, /new HttpClient/);
  assert.doesNotMatch(viewModel, /require\('playwright'\)/);
  assert.doesNotMatch(viewModel, /ReadNodeVersionAsync/);
  assert.match(app, /AddSingleton<CollectionEnvironmentProbe>/);
  assert.match(probe, /CheckEdgeCdpAsync/);
  assert.match(progress, /class CollectionProgressPresenter/);
  assert.equal(fs.existsSync(path.join(
    root,
    'native/src/EmsScout.Desktop/ViewModels/ReconciliationRows.cs')),
  true);
});

test('desktop exception boundaries use the shared application failure classifier', () => {
  const desktopRoot = path.join(root, 'native/src/EmsScout.Desktop');
  const sources = filesBelow(desktopRoot, '.cs')
    .filter(file => !file.endsWith(`${path.sep}StartupDatabaseInitializer.cs`))
    .map(file => fs.readFileSync(file, 'utf8'))
    .join('\n');

  assert.match(sources, /ApplicationFailureClassifier\.Classify/);
  assert.doesNotMatch(sources, /\bex\.Message\b/);
});

test('desktop failures and collection progress use the shared NDJSON logger', () => {
  const app = read('native/src/EmsScout.Desktop/App.xaml.cs');
  const diagnostics = read('native/src/EmsScout.Desktop/ViewModels/DiagnosticsViewModel.cs');
  const collection = read('native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs');
  const logger = read('native/src/EmsScout.Infrastructure/Logging/NdjsonApplicationLogger.cs');

  assert.match(app, /AddSingleton<IApplicationLogger>/);
  assert.match(diagnostics, /\*\.ndjson/);
  assert.match(collection, /workflow_progress/);
  assert.match(collection, /WriteFailure/);
  assert.match(logger, /errorCode/);
  assert.match(logger, /retryable/);
  assert.match(logger, /<redacted>/);
});

test('packaged Sidecar includes every local module required by the enumerator', () => {
  const enumerator = read('src/enumerate.js');
  const packaging = read('scripts/prepare-sidecar.ps1');
  const localModules = [...enumerator.matchAll(/require\(['"]\.\/([^'"]+)['"]\)/g)]
    .map(match => `src\\${match[1]}.js`)
    .filter(relativePath => relativePath !== 'src\\enumerate.js');

  for (const relativePath of localModules) {
    assert.match(packaging, new RegExp(relativePath.replaceAll('\\', '\\\\')),
      `${relativePath} must be copied into the packaged Sidecar`);
  }
  for (const moduleName of [
    'capture-polling',
    'capture-quality',
    'capture-result',
    'enumerate-options',
    'enumerate-output',
  ]) {
    assert.match(packaging, new RegExp(`require\\('./src/${moduleName}'\\)`),
      `${moduleName} must be loaded by the package smoke test`);
  }
});

test('Windows x64 packaging inputs are available in a clean clone', () => {
  const profilePath = path.join(
    root,
    'native/src/EmsScout.Desktop/Properties/PublishProfiles/win-x64.pubxml');
  const profile = fs.readFileSync(profilePath, 'utf8');
  const packageScript = read('scripts/package-native.ps1');
  const projectIgnore = read('native/src/EmsScout.Desktop/.gitignore');

  assert.match(profile, /<RuntimeIdentifier>win-x64<\/RuntimeIdentifier>/);
  assert.match(profile, /<SelfContained>true<\/SelfContained>/);
  assert.match(packageScript, /Fixture!=ProductionEvidence/);
  assert.match(projectIgnore, /!Properties\/PublishProfiles\/win-x64\.pubxml/);
});
