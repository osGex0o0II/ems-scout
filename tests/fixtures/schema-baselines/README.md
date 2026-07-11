# Schema Baseline Fixtures

`manifest.json` pins the known database and run-17 artifacts by SHA-256 without
copying production output into this fixture directory. Paths under `out/` are
local evidence and may be absent on a clean checkout; CI must use sanitized
fixture databases with the same audited schema shapes.

The legacy four-table databases intentionally contain historical foreign-key
violations because they were produced while foreign-key enforcement was off.
The baseline migrator must report those violations and preserve data. It must
not claim to repair them by deleting or rebuilding rows.

Before using an artifact in a migration or parity test, run the contract audit
and compare its size, SHA-256, schema shape, and expected counters with this
manifest. Production artifacts are always read-only test inputs.
