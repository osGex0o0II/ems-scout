# ADR 0001: C# Product Core With A Packaged Node Collector

- Status: Accepted
- Date: 2026-07-11

## Context

EMS Scout has a mature Node/Playwright collector with EMS-specific Shadow DOM,
SVG, Vue, timing, retry, and recovery behavior. The native WinUI application
already owns the operational UI, SQLite queries, annotations, groups, watches,
reconciliation, and filtered Excel export. The current application still runs
Node scripts from a source checkout and therefore is not independently
deployable.

Rewriting the collector in C# now would replace a proven browser host without
removing the page-side JavaScript boundary. Returning to Electron would discard
the native product and duplicate the operational UI.

## Decision

1. C#/.NET 10 is the product and data backbone.
2. A packaged Windows x64 Node sidecar owns only source collection through
   Playwright and Edge CDP.
3. The sidecar emits versioned `CollectionSnapshot` artifacts and versioned
   NDJSON workflow events. It does not expose internal logs as an API.
4. C# validates contracts, migrates and imports SQLite, assigns stable device
   identities, runs business quality audits, reconciles realtime data, and
   exports the current filtered device set.
5. PowerShell owns isolated field E2E and installed-package verification only.
6. SQL migrations have one owner in `EmsScout.Infrastructure`; repositories and
   Node scripts must stop creating or altering schema after migration parity is
   proven.

## Consequences

- The product intentionally uses two runtime languages at the collection
  boundary.
- Existing CommonJS collector code remains valid while new sidecar modules gain
  contract checks and can move to TypeScript incrementally.
- The first supported installed package is Windows x64 because the current
  native Node dependency and Playwright driver are x64 artifacts.
- C# Playwright is an optional later shadow experiment, not an active migration
  commitment.
- Electron/Web, TUI, Node import, and legacy report removal are gated by
  installed-package smoke tests and full-building parity evidence.

## Required Gates

- A clean Windows x64 machine can run the installed application without a
  global Node installation or source checkout.
- Every workflow emits one terminal event and binds output artifacts to a
  workflow/run identifier and SHA-256.
- All known database shapes migrate idempotently without losing notes, custom
  groups, group members, watches, or realtime overrides.
- Field E2E writes only to its isolated run directory and proves the production
  database, WAL, and SHM are unchanged.
- Legacy code is deleted only after the equivalent native workflow has passed
  shadow parity and field use.
