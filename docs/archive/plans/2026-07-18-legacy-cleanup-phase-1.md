# Legacy Cleanup Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove unreferenced legacy diagnostics, archive completed plans that have no active consumers, and align canonical status documentation with the verified Windows gate.

**Architecture:** Keep the WinUI product, packaged Node Sidecar, field E2E, protected fallbacks, and production evidence unchanged. Delete only files proven to have no runtime consumer, enforce their absence with an architecture test, and treat generated local directories as disposable outputs.

**Tech Stack:** Node.js architecture tests, Markdown project records, Git path/reference audit, .NET 10 verification.

## Global Constraints

- Do not delete `src/enumerate.js`, `sidecar/`, realtime Sidecar scripts, contracts, native tools, or packaging scripts.
- Do not modify or delete `out/`, `data/`, database backups, WAL/SHM files, or `handover.md`.
- Do not remove Electron, Web panel, TUI, Node import/quality, or legacy reports before their external gates pass.
- Preserve the existing local deletion of `scripts/prepare-sidecar.ps1`; exclude it from this change.

---

### Task 1: Enforce the dead-diagnostic boundary

**Files:**
- Modify: `tests/architecture/product-boundary.test.js`
- Delete: `scripts/dashboard.js`
- Delete: `scripts/views.sql`

- [x] Add a test asserting both obsolete files are absent and native reconciliation/data repositories remain present.
- [x] Run only that test and confirm it fails because the obsolete files still exist.
- [x] Delete the two obsolete files.
- [x] Re-run the focused test and confirm it passes.

### Task 2: Align cleanup records

**Files:**
- Modify: `docs/legacy-inventory.md`
- Modify: `docs/状态.md`
- Modify: `CHANGELOG.md`
- Move: unreferenced completed files from `docs/superpowers/plans/` to `docs/archive/plans/`

- [x] Record the two deleted diagnostics and retain the remaining protected diagnostic tools.
- [x] Replace stale cloud-install status with Run `29640744819` evidence.
- [x] Move only plans with no references outside their own file.
- [x] Record the cleanup in the changelog.

### Task 3: Verify and clean generated outputs

**Files:**
- Verify: repository source and tests
- Remove locally: `.codegraph/`, `artifacts/`, `node_modules/`, `.omo/`, `.superpowers/`

- [x] Run Node, self-test, native tests, formatting, diff checks, and Release build against an isolated tree containing the intended source changes.
- [x] Confirm protected local files remain untouched.
- [x] Remove only the approved generated directories after verification.
