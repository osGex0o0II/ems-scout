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
    'page-navigation',
    'url-sanitizer',
  ]) {
    assert.match(packaging, new RegExp(`require\\('./src/${moduleName}'\\)`),
      `${moduleName} must be loaded by the package smoke test`);
  }
  assert.match(packaging, /playwright-core\\lib\\vite/,
    'MSIX payload must prune Playwright tooling assets whose hashed names look like PRI qualifiers');
});

test('Windows x64 packaging inputs are available in a clean clone', () => {
  const packageJson = JSON.parse(read('package.json'));
  const profilePath = path.join(
    root,
    'native/src/EmsScout.Desktop/Properties/PublishProfiles/win-x64.pubxml');
  const profile = fs.readFileSync(profilePath, 'utf8');
  const packageScript = read('scripts/package-native.ps1');
  const projectIgnore = read('native/src/EmsScout.Desktop/.gitignore');
  const desktopProject = read('native/src/EmsScout.Desktop/EmsScout.Desktop.csproj');

  assert.match(profile, /<RuntimeIdentifier>win-x64<\/RuntimeIdentifier>/);
  assert.match(profile, /<SelfContained>true<\/SelfContained>/);
  assert.match(packageScript, /Fixture!=ProductionEvidence/);
  assert.match(packageScript, /dotnet clean \$Solution/,
    'packaging must clean the solution-level intermediate graph before publish');
  assert.match(packageScript, /dotnet clean \$DesktopProject[^]*Platform=x64/,
    'packaging must clean x64 desktop intermediates before publish');
  assert.match(packageScript, /windows-sdk-environment\.ps1/,
    'packaging must initialize the Windows SDK task environment');
  assert.match(projectIgnore, /!Properties\/PublishProfiles\/win-x64\.pubxml/);
  assert.match(desktopProject, /<PublishTrimmed>false<\/PublishTrimmed>/);
  assert.doesNotMatch(desktopProject, /<PublishTrimmed[^>]*>True<\/PublishTrimmed>/i);
  assert.match(read('scripts/prepare-sidecar.ps1'), /Assert-SafeOwnedDirectory/);
  assert.doesNotMatch(packageJson.scripts['native:build'], /--no-restore/,
    'native:build must refresh the restore graph after dependency changes');
  assert.match(packageJson.scripts['native:build'], /scripts[\\/]build-native\.ps1/,
    'native:build must use the clean Windows build entry');
  const buildScript = read('scripts/build-native.ps1');
  const sdkEnvironment = read('scripts/windows-sdk-environment.ps1');
  assert.match(buildScript, /windows-sdk-environment\.ps1/,
    'native build must initialize the Windows SDK task environment');
  assert.match(sdkEnvironment, /PROCESSOR_ARCHITECTURE/,
    'the shared SDK environment must supply the architecture required by MSIX tasks');
  assert.match(buildScript, /dotnet clean/);
  assert.match(buildScript, /EmsScout\.Desktop\.csproj/);
  assert.match(buildScript, /Platform=x64/);
  assert.match(buildScript, /dotnet build/);
  const targetFamily = desktopProject.match(/windows10\.0\.(\d+)\.0/)?.[1];
  const buildToolsFamily = desktopProject.match(
    /Microsoft\.Windows\.SDK\.BuildTools" Version="10\.0\.(\d+)\./)?.[1];
  assert.equal(buildToolsFamily, targetFamily,
    'MSIX BuildTools must match the desktop target Windows SDK family');
});

test('desktop owns and cleans its isolated Edge CDP session', () => {
  const viewModel = read('native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs');
  const app = read('native/src/EmsScout.Desktop/App.xaml.cs');
  const probe = read('native/src/EmsScout.Infrastructure/Sidecar/CollectionEnvironmentProbe.cs');
  assert.match(viewModel, /browser-sessions/);
  assert.match(viewModel, /IDisposable/);
  assert.match(viewModel, /Kill\(entireProcessTree: true\)/);
  assert.match(viewModel, /--remote-debugging-port=0/);
  assert.match(viewModel, /OwnedEdgeCdpEndpoint/);
  assert.match(viewModel, /_ownedEdgeProcess\.HasExited/);
  assert.match(viewModel, /if \(!emsOpened\)[^]*throw new InvalidOperationException/,
    'opening EMS must fail closed when the owned CDP endpoint never becomes reachable');
  assert.doesNotMatch(viewModel, /--remote-debugging-port=\{settings\.EdgeCdpPort\}/);
  assert.match(probe, /json\/new\?/);
  assert.doesNotMatch(viewModel, /ArgumentList\.Add\(settings\.EmsUrl\)/);
  assert.match(app, /Services is IDisposable/);
  assert.match(viewModel, /_activeTask\?\.Cancel\(\)/);
});

test('switch href state is never guessed from image frequency', () => {
  for (const file of ['src/enumerate.js', 'src/verify-live.js']) {
    const source = read(file);
    assert.doesNotMatch(source, /hrefs\.sort\(\(a, b\) => switchByHref\[b\] - switchByHref\[a\]\)/);
  }
});

test('ExportSmoke runtime boundary catches path and filesystem failures', () => {
  const source = read('native/tools/EmsScout.ExportSmoke/Program.cs');
  assert.match(source, /try\s*\{\s*var dbPath = Path\.GetFullPath\(options\.DatabasePath\)/,
    'runtime try boundary must begin immediately before path normalization');
  assert.match(source, /Console\.Error\.WriteLine\("ERROR: " \+ ex\.Message\);/);
});

test('build and CI artifacts never package archived or production EMS data', () => {
  const packageJson = JSON.parse(read('package.json'));
  const workflow = read('.github/workflows/windows-x64.yml');
  const packagedFiles = packageJson.build.files.join('\n');

  assert.doesNotMatch(packagedFiles, /^(?:data|out)\//m);
  assert.doesNotMatch(workflow, /data\/1号楼\/ac\.db/);
  assert.doesNotMatch(workflow, /artifacts\/ci\/\*\*/);
  assert.match(workflow, /tests\/fixtures\/schema-baselines\/archived-core-v0\.sql/);
});
