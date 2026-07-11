# EMS Scout versioned contracts

These contracts define the boundary between the packaged Node collection sidecar and the C# application.

- `collection-snapshot-v1.schema.json` is a Draft 2020-12 schema for one complete or scoped collection artifact.
- `workflow-event-v1.schema.json` is a Draft 2020-12 schema for one line of the sidecar's NDJSON standard output.
- `workflow-control-v1.schema.json` is a Draft 2020-12 schema for one command sent to the sidecar's standard input.

Contract version values are stable identifiers, not application versions:

- `ems.collection-snapshot/v1`
- `ems.workflow-event/v1`
- `ems.workflow-control/v1`

Missing source values in a v1 snapshot use JSON `null`. Empty strings, `"-"`, numeric `0`, and string `"0"` are audited as possible legacy sentinels and must not be used to mean missing data. A real measured zero remains numeric zero and is interpreted using its field context.

`artifact.sha256` covers the canonical UTF-8 JSON serialization of the snapshot's `buildings` value, and `artifact.bytes` is the byte length of that same payload. This avoids a self-referential whole-document hash. `lineage.baseArtifactSha256` records the prior artifact used by append or recapture workflows.

Each building, sub-area, page, and card carries a `sourceKey` that is unique within a capture. `deviceUid` is the stable database identity and may be `null` until the identity migration resolves it.

Workflow events are emitted one JSON object per line on standard output. Human-readable and diagnostic logs belong on standard error. Every event uses `workflowId`, `seq`, `timestamp`, `type`, and `stage`; a stream begins with `type=started` at `seq=1` and ends with one `type=terminal` event whose top-level `outcome` is one of the explicit terminal states in the schema.

The desktop requests graceful cancellation by writing one `WorkflowControl v1` `cancel` command to the sidecar's standard input. The sidecar owns child-process cleanup and must still finish stdout with one `cancelled` terminal event. The desktop may force-kill the process tree only after the sidecar grace period expires.

Run the read-only baseline auditor directly without installing packages:

```bash
node scripts/audit-contracts.js out/enum_full_v5.json out/ac.db
```

SQLite inputs and any existing WAL are copied to a private system temporary directory before inspection. The private copy is allowed to initialize WAL bookkeeping and is then placed in `query_only` mode; the source database and its companion-file set are guarded by content hash, size, and modification-time checks.

Exit code `0` means every input has a recognized shape. Exit code `2` means at least one input has an unknown shape, unknown contract version, or an invalid recognized v1 envelope. Read or parser failures use exit code `1`.
