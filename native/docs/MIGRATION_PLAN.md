# Native Migration Status

The architecture decisions are recorded in `docs/adr/0001-*` and
`docs/adr/0002-*`. This file tracks only remaining migration gates.

## Current Product Path

1. The packaged Node Sidecar collects EMS pages through Playwright and Edge CDP.
2. `CollectionSnapshot v1` and `WorkflowEvent v1` cross the process boundary.
3. C# validates the snapshot, runs versioned SQLite migrations, imports data,
   assigns stable device identities, and performs quality auditing.
4. WinUI reads SQLite for overview, data management, history, groups, watches,
   and the only user-facing Excel export.
5. PowerShell owns isolated field E2E and Windows x64 package verification.

## Remaining Gates

- Build and install the Windows x64 MSIX on a clean machine; prove collection
  uses the bundled Node runtime without a source checkout or global Node.
- Run a logged-in real-EMS single-building field E2E and retain its isolated
  WorkflowEvent, snapshot, database, quality, and Excel evidence.
- Run full-building shadow parity against the installed product.
- Remove Node import, Node base-quality, Electron/Web, TUI, and legacy reports
  only after the installed-package and field-parity gates pass.

## Cross-Platform Gates Completed

- Versioned contracts, SQLite migration/import ownership, schema guards, stable identity, rollback, cancellation, concurrency, and idempotency.
- Shared Node/C# quality fixtures and run17 golden parity.
- Content-level 12-column Excel tests, temporary-database ExportSmoke, and production-database test isolation.
- Collection task responsibility split, shared error classification, and native structured NDJSON logging.
- Architecture tests that prevent runtime DDL, direct UI exception text, and environment/progress logic from returning to the collection ViewModel.

## Non-Goals

- Rewriting the proven EMS DOM collector in C#.
- Restoring TXT, Markdown, or multi-report exports to the native UI.
- Guessing device identity when legacy evidence is ambiguous.
