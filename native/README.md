# EMS Scout Native

Native WinUI 3 desktop product for the EMS air-conditioner workflow. C#/.NET
owns the product and database lifecycle; the packaged Node Sidecar owns only
Playwright/Edge CDP collection.

## Current Product Shape

The native app is a refactor, not a one-for-one port of the old web panel. It keeps seven top-level pages:

- Overview
- Collection Tasks
- Data Management
- Audit Center
- Group Settings
- System Settings
- Diagnostics

Data Management filtered Excel export is the only user-facing export path. Legacy TXT, Markdown, and multi-report generation are not native UI actions.

## Run

Use the packaged Windows App SDK launch path:

```powershell
npm run native:run
```

Do not validate the app by directly running `bin\...\EmsScout.Desktop.exe`. The direct unpackaged executable can fail Windows App SDK runtime initialization without package identity. The `native:run` script closes any previous native app process, then launches through `dotnet run` with the MSIX package profile.

## Validate

```powershell
npm run native:build
npm run native:test
dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore
npm run self-test
node --test sidecar/test/*.test.js tests/architecture/*.test.js tests/contract-audit/*.test.js tests/enumeration/*.test.js tests/field-e2e/*.test.js tests/golden/*.test.js
dotnet build native\tools\EmsScout.DataTool\EmsScout.DataTool.csproj -c Release --no-restore
dotnet build native\tools\EmsScout.SchemaTool\EmsScout.SchemaTool.csproj -c Release --no-restore
dotnet build native\tools\EmsScout.ExportSmoke\EmsScout.ExportSmoke.csproj -c Release --no-restore
```

`native:test` is the clean-clone gate and excludes `Fixture=ProductionEvidence` because
`out/` is intentionally not committed. After trusted run17 evidence is placed locally,
run `npm run native:test:evidence` to execute only the production-evidence parity tests.

Windows x64 package preparation and field validation are separate gates:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\prepare-sidecar.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-native.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\field-e2e.ps1 -Building 1号 -LaunchEdge -RunSingleBuilding
```

The field command writes only to a unique `out\field-e2e-*` directory and never
opens the production database through SQLite.

The x64 package is self-contained and ReadyToRun, but intentionally not trimmed.
WinUI/WinRT and the current versioned JSON, migration, settings, and logging paths
require reflection metadata; enabling trimming without source-generated coverage is unsupported.

## Database Lifecycle

- `EmsScout.Infrastructure.Migrations` is the only code allowed to create or alter SQLite schema.
- Repositories validate their required schema and fail clearly when migration has not run; they never repair schema during a user operation.
- Switching System Settings to an existing database runs the versioned migrator before the new path is saved.
- A collection task freezes its data directory at startup so collection, snapshot validation, import, quality, and realtime stages cannot drift across directories.
- Overview freshness uses the latest completed collection import timestamp, with database file mtime only as a fallback for databases without run history.

## Errors And Logs

- Desktop exception boundaries use shared error categories and stable error codes; raw exception messages are not rendered directly.
- Native NDJSON logs are written to `%LOCALAPPDATA%\EMS Scout\logs\native-YYYY-MM-DD.ndjson` when enabled.
- Startup-critical failures are always retained even when normal NDJSON logging is disabled.
- Log records include level, category, event, workflow, stage, error code, retryability, and redacted exception details.
- Diagnostics discovers these native logs alongside collector and compatibility logs.

## Test Data Safety

- Tests never open production `out/ac.db` through SQLite.
- Run17 integration tests byte-copy the database to a temporary snapshot before querying it.
- `data/1号楼` and `data/2号楼` WAL/SHM fixtures are migration evidence and must not be cleaned as generated files.

## Projects

- `EmsScout.Desktop`: WinUI 3 shell, pages, view models, commands.
- `EmsScout.Application`: use cases and view-ready application contracts.
- `EmsScout.Domain`: device, building, and quality domain model.
- `EmsScout.Infrastructure`: SQLite, filtered Excel export, file system, and OS integrations.
- `EmsScout.DataTool`: CollectionSnapshot validation, shadow/apply import, and native quality CLI.
- `EmsScout.SchemaTool`: versioned SQLite migration CLI.
- `EmsScout.Tests`: migration, SQLite, export, and golden-file tests.
