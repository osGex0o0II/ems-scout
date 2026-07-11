# EMS Scout Node Sidecar Protocol

`runner.js` wraps an existing collector without changing it. The wrapper reserves stdout for
`WorkflowEvent v1` NDJSON and redirects all child human-readable output to stderr.

The product collection entry point is `collect.js`. It runs the proven legacy enumerator and, only
after a successful collection, converts the retained `enum_full_v5.json` evidence into the canonical
`collection_snapshot_v1.json` artifact bound to `EMS_WORKFLOW_ID`.

```powershell
node sidecar/runner.js `
  --workflow-id=collect-20260711-001 `
  --stage=enumeration `
  -- node sidecar/collect.js --edge --append --bldg=1号 --out-dir=out
```

For a quality/audit command where exit code 2 means a completed report with findings:

```powershell
node sidecar/runner.js `
  --workflow-id=quality-20260711-001 `
  --stage=quality `
  --exit-2-outcome=succeeded_with_findings `
  -- node scripts/quality-report.js
```

The installed application should replace both `node` tokens with its bundled `node.exe` path.
The runner does not resolve Node from `PATH` and has no npm dependencies.

## Cancellation control

Standard input is reserved for `WorkflowControl v1` JSON commands. To request cancellation, write
one line and keep reading stdout until its terminal event:

```json
{"contractVersion":"ems.workflow-control/v1","workflowId":"collect-20260711-001","timestamp":"2026-07-11T08:00:00.000Z","type":"cancel","reason":"user_requested"}
```

The runner emits `action=cancel_requested`, asks the child process to stop, and emits one
`outcome=cancelled` terminal event with exit code `130`. It force-stops an unresponsive child after
five seconds while keeping the runner alive long enough to finish the event stream.

## Event stream

Every stdout line is one JSON object conforming to the canonical
[`../contracts/workflow-event-v1.schema.json`](../contracts/workflow-event-v1.schema.json). A stream:

1. starts with exactly one `started` event at `seq=1`;
2. has contiguous sequence numbers and one `workflowId`;
3. may contain `progress` and `action` events;
4. ends with exactly one `terminal` event and has no later events.

The terminal `outcome` is authoritative. The wrapper also returns a corresponding nonzero process
exit code for non-success outcomes.

| Child result | Terminal outcome |
|---|---|
| exit 0 | `succeeded` |
| exit 2 | `rejected` (default) |
| exit 2 with `--exit-2-outcome=succeeded_with_findings` | `succeeded_with_findings` |
| exit 3 | `auth_required` |
| exit 4 | `rejected` |
| other nonzero exit | `internal_error` |
| signal or `[ACTION]return` | `cancelled` |
| `[ACTION]switch_to_cdp` | `auth_required` |

Legacy `[PROGRESS]{...}` and `[PROGRESS] {...}` lines become typed `progress` events. The original
payload remains under `progress.data`; known counters are also normalized to `percent`, `current`,
`total`, and `unit`. Legacy `[ACTION]...` lines become `action` events.

Run the dependency-free tests with:

```bash
node --test sidecar/test/*.test.js
```
