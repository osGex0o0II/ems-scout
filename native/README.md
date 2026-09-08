# EMS Scout Native

Native WinUI 3 desktop panel for the EMS air-conditioner workflow.

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

The Overview workbench uses one current `DeviceRecord` snapshot for fleet, building, and custom-area-group summaries. Custom area groups show their public-area device totals and separate running, stopped, offline, and unknown counts; selecting a group opens its Group Settings detail.

## Run

Use the packaged Windows App SDK launch path:

```powershell
npm run native:run
```

Do not validate the app by directly running `bin\...\EmsScout.Desktop.exe`. The direct unpackaged executable can fail Windows App SDK runtime initialization without package identity. The `native:run` script closes any previous native app process, then launches the currently installed MSIX through `shell:AppsFolder`, the same package identity used by the desktop shortcut.

## MSIX lifecycle

The supported distribution format is the versioned MSIX produced by `scripts/native-package.ps1`. Artifacts are isolated
under `out/native-packages/<version>/` and include the main package, architecture dependencies, certificate material, and
`package-manifest.json`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/native-package.ps1 -Version 1.0.7.0 -CertificateThumbprint <thumbprint>
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/native-install.ps1 -PackageDirectory out/native-packages/1.0.7.0
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/native-update.ps1 -PackageDirectory out/native-packages/1.0.7.0
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/native-uninstall.ps1
```

For a development certificate, run the first install or update from an elevated PowerShell so Windows AppX deployment
can trust the signer in the machine certificate stores. The installer fails fast if that trust is missing. Update rejects
equal or lower versions and verifies the user settings hash. Uninstall removes the app registration and desktop shortcut
but preserves `%LOCALAPPDATA%\EMS Scout`, repository `out`, and `data`; `-PurgeData` requires typing `PURGE` and is the
only path that removes the user-data directory.

## Validate

```powershell
npm run native:build
npm run native:test
dotnet format native\EmsScout.Native.slnx --verify-no-changes --no-restore
npm run self-test
node --check src\enumerate.js
node --check src\data-history.js
```

## Projects

- `EmsScout.Desktop`: WinUI 3 shell, pages, view models, commands.
- `EmsScout.Application`: use cases and view-ready application contracts.
- `EmsScout.Collection`: Playwright/Edge CDP collection orchestration.
- `EmsScout.Domain`: device, building, and quality domain model.
- `EmsScout.Infrastructure`: SQLite, filtered Excel export, file system, and OS integrations.
- `EmsScout.Infrastructure.Importing`: current enum JSON file source.
- `EmsScout.Infrastructure.Realtime`: current realtime JSON file source.
- `EmsScout.Tests`: migration, SQLite, export, and golden-file tests.
