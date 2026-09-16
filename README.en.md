<p align="center">
  <img src="docs/images/ems-scout-logo.png" width="220" alt="EMS Scout logo" />
</p>

<h1 align="center">EMS Scout</h1>

<p align="center">
  <em>Field HVAC status, collected, audited, and traceable.</em>
</p>

<p align="center">
  <a href="package.json"><img src="https://img.shields.io/badge/License-ISC-4c566a.svg" alt="ISC License" /></a>
  <a href="https://nodejs.org/"><img src="https://img.shields.io/badge/Runtime-Node.js-339933?logo=node.js&logoColor=white" alt="Node.js" /></a>
  <a href="https://learn.microsoft.com/windows/apps/winui/winui3/"><img src="https://img.shields.io/badge/UI-WinUI%203-0078D4?logo=windows&logoColor=white" alt="WinUI 3" /></a>
  <a href="https://www.sqlite.org/"><img src="https://img.shields.io/badge/Storage-SQLite-003B57?logo=sqlite&logoColor=white" alt="SQLite" /></a>
</p>

<p align="center">
  <a href="README.md">简体中文</a> ·
  <a href="README.ja.md">日本語</a>
</p>

---

## Core Capabilities

| Field collection | Live audit | SQLite history | Windows Native | Excel export |
| --- | --- | --- | --- | --- |
| 6 buildings | 46 live points | Run snapshots and restore | Data management and diagnostics | 13-column building sheets |

## Workflow

```text
EMS pages → Enumeration → Quality checks → SQLite → Live audit → Excel
```

## Real Application Screenshots

### Overview

<p align="center">
  <img src="docs/images/ems-scout-dashboard.png" alt="EMS Scout overview" width="100%" />
</p>

### Data management

<p align="center">
  <img src="docs/images/ems-scout-data-management.png" alt="EMS Scout data management" width="100%" />
</p>

### About

<p align="center">
  <img src="docs/images/ems-scout-about.png" alt="EMS Scout about" width="100%" />
</p>

## Project Structure

```text
.
├── src/                              # Node.js collection, rules, and history core
│   ├── enumerate.js                  # Main Playwright + Edge CDP enumerator
│   ├── enum-validator.js             # Enumeration and device identity checks
│   ├── rules.js                      # State, area, and quality rules
│   ├── realtime-quality.js           # Live-field and raw-value audit
│   └── data-history.js               # SQLite history and restore
├── scripts/                          # Import, collection, audit, and field checks
├── native/
│   ├── src/EmsScout.Desktop/         # WinUI 3 native UI
│   ├── src/EmsScout.Application/     # Use cases, queries, and contracts
│   ├── src/EmsScout.Domain/          # Device, building, and state models
│   ├── src/EmsScout.Infrastructure/ # SQLite, file sources, and Excel export
│   └── tools/EmsScout.ExportSmoke/   # Excel export smoke-test tool
├── data/                             # Local building archives (not tracked remotely)
├── docs/                             # Architecture, data model, and screenshots
├── config/                           # Quality-audit configuration
├── README.md                         # Chinese documentation
├── README.en.md                      # English documentation
├── README.ja.md                      # Japanese documentation
├── package.json                      # Node.js scripts and dependencies
└── CHANGELOG.md                      # Change history
```

## File Guide

| File | Purpose |
| --- | --- |
| [`src/enumerate.js`](src/enumerate.js) | Enumerates device cards through Playwright, Edge CDP, SVG, and Vue data. |
| [`src/verify-live.js`](src/verify-live.js) | Compares the current browser state with SQLite. |
| [`src/rules.js`](src/rules.js) | Classifies power state, area, building identity, and card quality. |
| [`src/enum-validator.js`](src/enum-validator.js) | Checks building scope, counts, pages, duplicates, and import readiness. |
| [`src/realtime-quality.js`](src/realtime-quality.js) | Audits live-point completeness, enum values, temperature ranges, and lock raw values. |
| [`src/data-history.js`](src/data-history.js) | Maintains SQLite runs, metadata, and current-data restore. |
| [`scripts/import.js`](scripts/import.js) | Imports enumeration JSON into SQLite. |
| [`scripts/collect-realtime-all-batch.js`](scripts/collect-realtime-all-batch.js) | Coordinates six-building live collection, progress, and audit. |
| [`scripts/collect-building-realtime-details.js`](scripts/collect-building-realtime-details.js) | Collects live details for one building. |
| [`scripts/collect-building-realtime-batch.js`](scripts/collect-building-realtime-batch.js) | Runs a live collection batch for one building. |
| [`scripts/realtime-browser.js`](scripts/realtime-browser.js) | Manages the Edge and CDP session used for live collection. |
| [`scripts/quality-report.js`](scripts/quality-report.js) | Generates enumeration quality reports. |
| [`scripts/audit-realtime-data.js`](scripts/audit-realtime-data.js) | Audits live data and anomalous raw values. |
| [`scripts/field-e2e.ps1`](scripts/field-e2e.ps1) | Runs isolated field checks with temporary output and SQLite data. |
| `native/src/EmsScout.Desktop` | Overview, collection, data management, audit, settings, and diagnostics pages. |
| `native/src/EmsScout.Infrastructure` | SQLite, live snapshots, quality audits, and Excel export. |
| `native/tests/EmsScout.Tests` | Native application, history, and export contract tests. |
| [`docs/architecture.md`](docs/architecture.md) | Layering and data-flow notes. |
| [`docs/data-model.md`](docs/data-model.md) | Database and Excel export model notes. |
| [`CHANGELOG.md`](CHANGELOG.md) | Change history and field-verification records. |

## Acknowledgements

- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) and WinUI 3
- [Node.js](https://nodejs.org/) and [Playwright](https://playwright.dev/)
- [better-sqlite3](https://github.com/WiseLibs/better-sqlite3)
- [SheetJS](https://sheetjs.com/)
- The SmartPiEMS field system and validation environment

## License

The project is currently declared under the **ISC License** in `package.json`. The repository does not currently include a standalone `LICENSE` file.
