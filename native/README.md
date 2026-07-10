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
node --check src\enumerate.js
node --check src\panel\server.js
```

## Projects

- `EmsScout.Desktop`: WinUI 3 shell, pages, view models, commands.
- `EmsScout.Application`: use cases and view-ready application contracts.
- `EmsScout.Collection`: Playwright/Edge CDP collection orchestration.
- `EmsScout.Domain`: device, building, and quality domain model.
- `EmsScout.Infrastructure`: SQLite, filtered Excel export, file system, and OS integrations.
- `EmsScout.Legacy`: adapters for current JSON and realtime artifacts.
- `EmsScout.Tests`: migration, SQLite, export, and golden-file tests.
