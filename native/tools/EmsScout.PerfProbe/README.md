# EmsScout.PerfProbe

Measures native repository operations against an isolated SQLite backup. The source database is opened read-only and is never used by repositories or migrations.

```powershell
dotnet run --project native/tools/EmsScout.PerfProbe --configuration Release -- `
  --db out/ac.db `
  --realtime-dir out `
  --out out/perf-probes
```

Each invocation creates `perf-probe-<timestamp>-<guid>/` under `--out`, copies the source to `probe.db` with SQLite Backup, applies additive migrations to that copy, and writes `performance.json` beside it.

The report records source identity and table counts, group/rule counts, every cold and warm sample, and warm P50/P95 summaries for elapsed time, task-return time, and allocated megabytes.

Each scenario gets a new repository/dashboard graph. Its `cold` sample is that graph's first measured call, followed by 20 `warm` calls on the same graph. This is repository-cold, not process-cold: the process, JIT, SQLite native runtime, and operating-system file cache are shared across scenarios. The JSON records these definitions so results are not presented as fresh-process startup measurements.

The measured scenarios cover configuration and floor reads, all-rule floor initialization, one/all-rule matching, 50-row search, combined 500-row page and facets, cached page navigation, and dashboard overview. It also replays floor initialization for the first enabled group that has rules, both per row and once per distinct building. The selected group name and actual rule count are recorded; this makes the current result directly comparable with the earlier 93-row area-editor sample only when the recorded count is 93.

`synthetic-rule-count-500` and `synthetic-rule-count-1000` cycle the persisted rules into in-memory snapshots with consecutive rule order values, then measure the repository's batch match-count operation. They do not insert rules or change the probe database. These scenarios measure background matching throughput and allocation only; they do not measure UI scrolling, realized rows, layout, window size, or DPI behavior.
