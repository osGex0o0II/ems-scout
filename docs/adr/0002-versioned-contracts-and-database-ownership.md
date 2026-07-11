# ADR 0002: Versioned Contracts And Single Database Ownership

- Status: Accepted
- Date: 2026-07-11

## Context

The legacy collection JSON has no contract version and mixes missing-value
sentinels. SQLite DDL is duplicated across SQL scripts, Node modules, quality
scripts, and C# repositories. Existing databases have `PRAGMA user_version=0`
and multiple schema shapes. Core imports recreate row IDs while user-owned
notes, groups, and overrides must survive.

## Decision

1. `contracts/collection-snapshot-v1.schema.json` is the source contract for
   collected facts.
2. `contracts/workflow-event-v1.schema.json` is the process contract between
   WinUI and the sidecar.
3. `contracts/workflow-control-v1.schema.json` is the versioned stdin control
   contract. Cancellation is requested through this channel so the sidecar can
   emit its terminal event before process cleanup.
4. Missing measurements use JSON `null`. Raw source values and normalized
   values are separate fields; `""`, `"-"`, and numeric zero are not generic
   missing-value aliases.
5. A capture-local `sourceKey` identifies one rendered source location.
   A C#-owned `deviceUid` identifies the managed device across captures.
6. C# migrations are ordered and recorded through `PRAGMA user_version` and a
   migration journal. The initial baseline is additive and idempotent.
7. WAL-safe backups use the SQLite backup API or `VACUUM INTO`; copying only the
   main database file is not an accepted migration backup.
8. Quality and realtime artifacts are bound to a run. Mutable `latest` files are
   compatibility pointers, not authoritative history.

## Migration Order

1. `v1-baseline`: recognize legacy shapes and add migration metadata and
   missing extension structures without rebuilding core tables.
2. `v2-identity`: add `source_key` and `device_uid`, dual-write legacy keys, and
   report every ambiguous mapping.
3. `v3-run-data`: bind collection, quality, realtime, and artifact hashes to a
   durable run/workflow record.

## Safety Rules

- Never run the destructive legacy `scripts/schema.sql` as a migrator.
- Never silently delete or merge ambiguous devices.
- Never claim a legacy foreign-key violation was repaired by dropping data.
- Never mutate a production artifact during audit or shadow parity testing.
