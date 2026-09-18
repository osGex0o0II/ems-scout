# Batch Timeline Governance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将原生审计页改造成面向数据管理员的批次治理工作台，完整展示历史批次、质量与实时证据，并在保护当前数据和恢复关系的前提下安全删除历史快照。

**Architecture:** 保留现有 WinUI 3、应用层批次接口和 SQLite 快照结构，在应用层增加批次治理模型与删除影响预览，在仓储层集中执行当前批次、运行中任务、恢复依赖和文件清理保护。审计页采用时间线默认视图、表格清理模式和统一详情面板；任务页复用同一治理服务，不再自行决定删除安全性。

**Tech Stack:** C# 10/.NET 10, WinUI 3, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, Node.js, better-sqlite3, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-17-batch-timeline-governance-design.md`

## Global Constraints

- 当前设备数据、设备备注、标签、人工匹配规则和导出契约不得因删除历史快照而改变。
- `needs_review` 批次默认可见；质量或实时证据缺失必须显示为需复核/不可用，不得伪造为通过或有效集控锁状态。
- 当前活动批次、采集任务运行期间相关批次、恢复链必要节点不得删除；保护必须在仓储层再次校验。
- SQLite 历史记录删除使用事务；数据库提交和文件删除采用两阶段语义，文件失败必须返回待清理结果。
- `collection_runs.id` 是内部技术主键，永久递增且永不复用；展示批次号 `run_no` 允许回收空缺，所有治理操作和恢复关系必须使用不可变 `batch_uid`、`run_key` 与唯一操作 ID。
- 日志、质量报告、批次 JSON、NDJSON、实时汇总和 SQLite 都是本地运行数据；不得新增云端上传或远程同步。
- 不删除共享的 `quality_report.json`、`quality_report.txt` 和 `realtime_*_latest.json` 别名文件。
- 现场数据验证仅使用隔离临时目录；不得把生产 `out/ac.db` 描述为临时 E2E 数据库。

## File Map

- Create: `native/src/EmsScout.Application/Collection/CollectionRunGovernance.cs` for governance records, evidence states, delete impact and operation result contracts.
- Create: `native/src/EmsScout.Application/Collection/ICollectionRunActivity.cs` for the process-wide collection activity guard.
- Create: `native/src/EmsScout.Infrastructure/Sqlite/CollectionRunArtifactCleaner.cs` for safe local artifact discovery and two-phase cleanup.
- Modify: `native/src/EmsScout.Application/Collection/CollectionRuns.cs` to expose list-all, detail, preview, batch delete and operation contracts.
- Modify: `native/src/EmsScout.Application/Collection/CollectionRunFilter.cs` and `CollectionDataSourceCatalog.cs` to cover `needs_review` and consistent current-run semantics.
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs` for all storage-side governance checks and transactional deletion.
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteSchemaMigrator.cs` and `scripts/schema.sql` for run identity history and local operation records.
- Modify: `src/data-history.js` and `scripts/import.js` for Node/native parity and non-reused run identities.
- Modify: `native/src/EmsScout.Desktop/ViewModels/AuditViewModel.cs`, `CollectionRunRow.cs`, and `CollectionRunDetail.cs` for the timeline, cleanup selection and evidence projection.
- Modify: `native/src/EmsScout.Desktop/Pages/AuditPage.xaml` and `AuditPage.xaml.cs` for the new workspace and explicit confirmations.
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs` and `TasksPage.xaml.cs` to register active collection work and reuse the same delete workflow.
- Modify: `native/src/EmsScout.Desktop/App.xaml.cs` to register the activity guard and governance dependencies.
- Test: `native/tests/EmsScout.Tests/CollectionRunGovernanceTests.cs`, `CollectionRunRepositoryTests.cs`, `CollectionRunMetadataTests.cs`, `HistoryDataUiContractTests.cs`, `TasksPageUiContractTests.cs`, and `DashboardOverviewServiceTests.cs`.
- Test: `tests/run-id-allocation.test.js` and `scripts/self-test.js` for Node identity and import parity.

---

### Task 1: Establish the governance contracts and test fixtures

**Files:**
- Create: `native/src/EmsScout.Application/Collection/CollectionRunGovernance.cs`
- Create: `native/src/EmsScout.Application/Collection/ICollectionRunActivity.cs`
- Modify: `native/src/EmsScout.Application/Collection/CollectionRuns.cs`
- Modify: `native/tests/EmsScout.Tests/EmsScout.Tests.csproj` only if the new source files are not included by the existing SDK glob.
- Test: `native/tests/EmsScout.Tests/CollectionRunGovernanceTests.cs`

**Interfaces:**
- Produces `RunEvidenceStatus`, `RunGovernanceDetail`, `RunDeleteImpact`, `RunOperationRecord`, `CollectionRunDeleteResult`, and `ICollectionRunActivity` for Tasks 2-8.
- `ICollectionRunActivity.IsActive` reports whether any collection task is running; `Begin()` returns `IDisposable` and increments a process-local active count, while disposal decrements it exactly once.
- `RunDeleteImpact.CanDelete` and `BlockingReasons` are the single source of truth for UI enablement and repository error messages.

- [ ] **Step 1: Write failing unit tests for immutable operation identity and activity guard behavior.**

```csharp
[Fact]
public void OperationUsesUniqueIdAndDoesNotContainDevicePayload()
{
    var operation = RunOperationRecord.Delete("field-41", 41, 3, "completed");
    Assert.NotEqual(Guid.Empty, operation.OperationId);
    Assert.DoesNotContain("payload", operation.Summary, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void ActivityGuardRemainsActiveUntilEveryLeaseIsDisposed()
{
    var guard = new CollectionRunActivityRegistry();
    using var first = guard.Begin();
    using var second = guard.Begin();
    Assert.True(guard.IsActive);
    first.Dispose();
    Assert.True(guard.IsActive);
    second.Dispose();
    Assert.False(guard.IsActive);
}
```

- [ ] **Step 2: Run the focused tests and verify they fail because the contracts do not exist.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionRunGovernanceTests`

Expected: FAIL with missing governance types.

- [ ] **Step 3: Add the records and guard with explicit, non-sensitive fields.**

Add these exact contracts:

```csharp
public interface ICollectionRunActivity
{
    bool IsActive { get; }
    IDisposable Begin();
}

public sealed class CollectionRunActivityRegistry : ICollectionRunActivity
{
    private int _activeCount;

    public bool IsActive => Volatile.Read(ref _activeCount) > 0;

    public IDisposable Begin()
    {
        Interlocked.Increment(ref _activeCount);
        return new Lease(this);
    }

    private void Release() => Interlocked.Decrement(ref _activeCount);

    private sealed class Lease(CollectionRunActivityRegistry owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}

public sealed record RunEvidenceStatus(
    string Kind,
    string State,
    string Reason,
    int? ExpectedCount = null,
    int? ActualCount = null,
    string? SourcePath = null);

public sealed record RunArtifactCandidate(string RelativePath, string Kind, bool IsShared, bool IdentityVerified);

public sealed record ArtifactCleanupResult(
    int DeletedCount,
    IReadOnlyList<string> PendingPaths,
    IReadOnlyList<string> PendingReasons)
{
    public bool IsComplete => PendingPaths.Count == 0;
}

public sealed record RunDeleteImpact(
    long RunId,
    string RunKey,
    bool IsCurrent,
    int SnapshotCards,
    int RealtimeRows,
    int Pages,
    int SubAreas,
    int Buildings,
    IReadOnlyList<RunArtifactCandidate> Artifacts,
    IReadOnlyList<string> BlockingReasons)
{
    public bool CanDelete => BlockingReasons.Count == 0;
}

public sealed record RunOperationRecord(
    Guid OperationId,
    string OperationType,
    long RunId,
    string RunKey,
    string OccurredAt,
    string Result,
    string Summary,
    int DeletedCards,
    int DeletedPages,
    int DeletedSubAreas,
    int DeletedBuildings,
    int PendingArtifacts)
{
    public static RunOperationRecord Delete(string runKey, long runId, int deletedCards, string result) =>
        new(Guid.NewGuid(), "delete", runId, runKey, StoredTimestamp.FormatLocal(DateTimeOffset.Now),
            result, $"删除历史快照 {runKey}", deletedCards, 0, 0, 0, 0);
}
```

`RunOperationRecord` must never contain device names, point values, payload JSON or report正文. `CollectionRunDeleteResult` must carry `ArtifactCleanupResult` and the operation ID.

Update `CollectionRunDeleteResult` to the exact shape `CollectionRunDeleteResult(long RunId, string RunKey, string CompletedAt, int DeletedCards, int DeletedPages, int DeletedSubAreas, int DeletedBuildings, Guid OperationId, ArtifactCleanupResult ArtifactCleanup)`.

- [ ] **Step 4: Run the focused tests and commit the contract-only change.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionRunGovernanceTests`

Expected: PASS.

Commit: `git add native/src/EmsScout.Application/Collection native/tests/EmsScout.Tests/CollectionRunGovernanceTests.cs && git commit -m "feat: add batch governance contracts"`

### Task 2: Add persistent run identity and local operation schema

**Files:**
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteSchemaMigrator.cs`
- Modify: `scripts/schema.sql`
- Modify: `src/data-history.js`
- Test: `native/tests/EmsScout.Tests/CollectionRunRepositoryTests.cs`
- Test: `tests/run-id-allocation.test.js`

**Interfaces:**
- Produces `run_id_registry(technical_id, allocated_at)`, `run_key_registry(run_key, first_seen_at, deleted_at, last_run_id)` and `run_operations(operation_id, operation_type, run_id, batch_uid, run_key, occurred_at, result, summary, deleted_cards, deleted_pages, deleted_sub_areas, deleted_buildings, pending_artifacts)`.
- Existing databases are migrated additively. Existing `collection_runs.run_key` rows are inserted into the registry with `INSERT OR IGNORE`; no history rows are dropped.

- [ ] **Step 1: Add failing tests for legacy migration, run-key tombstones and ID reuse.**

```csharp
[Fact]
public async Task MigratesRunKeyRegistryWithoutChangingExistingRuns()
{
    var databasePath = CreateDatabase();
    var migrator = new SqliteSchemaMigrator(() => databasePath);
    await migrator.MigrateAsync();
    Assert.Equal(1L, await ScalarLongAsync(databasePath, "SELECT COUNT(*) FROM run_key_registry"));
}
```

```javascript
const { ensureHistorySchema, createRunFromCurrent, deleteRun } = require('../src/data-history');
const Database = require('better-sqlite3');

test('does not reuse a deleted run_key while reusing the lowest numeric id', () => {
  const databasePath = createFixtureDatabase();
  const db = new Database(databasePath);
  ensureHistorySchema(db);
  const first = createRunFromCurrent(db, {
    completedAt: '2026-09-17T00:01:00+08:00',
    runKey: 'old-1',
  });
  deleteRun(db, first);
  const second = createRunFromCurrent(db, {
    completedAt: '2026-09-17T00:02:00+08:00',
    runKey: 'old-1',
  });
  assert.equal(second, 1);
  assert.notEqual(db.prepare('SELECT run_key FROM collection_runs WHERE id = 1').get().run_key, 'old-1');
});
```

The JavaScript fixture helper `createFixtureDatabase()` must create a temporary file database with `buildings`, `sub_areas`, `pages`, and `cards` containing one valid card, then return its absolute path; the test closes the database and removes the temporary directory in a `finally` block.

- [ ] **Step 2: Run the focused tests and verify the schema/identity assertions fail.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionRunRepositoryTests` and `node --test tests/run-id-allocation.test.js`

Expected: FAIL because the registry and operation table are absent and deleted keys are not retained.

- [ ] **Step 3: Add additive migrations and make both allocators register identities inside their existing transactions.**

`nextCollectionRunId` and `AllocateCollectionRunIdAsync` allocate `MAX(collection_runs.id, run_id_registry.technical_id) + 1` and record the technical ID in `run_id_registry` in the same transaction, so a deleted ID is never reused. `nextRunNumber` and `AllocateRunNumberAsync` continue selecting the lowest positive unused display number. `uniqueRunKey` must also consult `run_key_registry`; after deletion it marks `deleted_at` and never returns the same key. Native restore backups use the same registries transactionally.

- [ ] **Step 4: Run both focused suites and verify old run IDs and rows are unchanged.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionRunRepositoryTests` and `node --test tests/run-id-allocation.test.js`

Expected: PASS, with the old lowest-unused-ID assertion retained and the new run-key tombstone assertion passing.

- [ ] **Step 5: Commit the schema and allocator change.**

Commit: `git add native/src/EmsScout.Infrastructure/Sqlite/SqliteSchemaMigrator.cs scripts/schema.sql src/data-history.js tests/run-id-allocation.test.js native/tests/EmsScout.Tests/CollectionRunRepositoryTests.cs && git commit -m "feat: persist batch identity and governance operations"`

### Task 3: Implement repository-level safety, detail and delete preview

**Files:**
- Modify: `native/src/EmsScout.Application/Collection/CollectionRuns.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs`
- Modify: `native/src/EmsScout.Desktop/App.xaml.cs`
- Test: `native/tests/EmsScout.Tests/CollectionRunRepositoryTests.cs`

**Interfaces:**
- Change `ListAsync` to `Task<IReadOnlyList<CollectionRunRecord>> ListAsync(int? limit = null, CancellationToken cancellationToken = default)`; `null` means no SQL `LIMIT` and is used by governance, Home and Data pages so historical batches are not silently capped at 50/80/500.
- Add `GetGovernanceDetailAsync(long runId, CancellationToken)`, `GetDeleteImpactAsync(long runId, CancellationToken)`, and `DeleteManyAsync(IReadOnlyList<long> runIds, CancellationToken)` to `ICollectionRunRepository`.
- Keep `DeleteAsync(long, CancellationToken)` as a single-item adapter over the same preview/transaction path so existing callers cannot bypass protections.

- [ ] **Step 1: Add failing repository tests for all protection boundaries.**

Required cases:

```csharp
[Fact]
public async Task CannotDeleteCurrentRun()
{
    var databasePath = CreateDatabaseWithCurrentAndHistoricalRuns();
    var repository = new SqliteCollectionRunRepository(() => databasePath);
    var currentId = (await repository.ListAsync(null)).OrderByDescending(run => run.ImportedAt).First().Id;
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteAsync(currentId));
    Assert.Contains("当前数据", error.Message, StringComparison.Ordinal);
}

[Fact]
public async Task CannotDeleteRunReferencedByExistingRestoreBackup()
{
    var databasePath = CreateDatabaseWithRestoreBackupDependency();
    var repository = new SqliteCollectionRunRepository(() => databasePath);
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteAsync(1));
    Assert.Contains("恢复", error.Message, StringComparison.Ordinal);
}

[Fact]
public async Task ActivityGuardBlocksDeleteEvenWhenUiIsBypassed()
{
    var databasePath = CreateDatabase();
    var activity = new CollectionRunActivityRegistry();
    var repository = new SqliteCollectionRunRepository(() => databasePath, activity);
    using var lease = activity.Begin();
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteAsync(1));
    Assert.Contains("采集任务", error.Message, StringComparison.Ordinal);
}

[Fact]
public async Task DeletePreviewCountsRealtimeRowsAndArtifacts()
{
    var databasePath = CreateDatabaseWithRealtimeAndArtifacts();
    var repository = new SqliteCollectionRunRepository(() => databasePath);
    var impact = await repository.GetDeleteImpactAsync(1);
    Assert.Equal(1, impact.SnapshotCards);
    Assert.Equal(1, impact.RealtimeRows);
    Assert.Contains(impact.Artifacts, artifact => artifact.Kind == "quality-report");
}

[Fact]
public async Task BatchDeleteValidatesEveryRunBeforeChangingAnyRow()
{
    var databasePath = CreateDatabaseWithCurrentAndHistoricalRuns();
    var repository = new SqliteCollectionRunRepository(() => databasePath);
    await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteManyAsync([1, 2]));
    Assert.Equal(2, (await repository.ListAsync(null)).Count);
}

[Fact]
public async Task ListWithoutLimitReturnsAllRuns()
{
    var databasePath = CreateDatabaseWithRuns(501);
    var repository = new SqliteCollectionRunRepository(() => databasePath);
    Assert.Equal(501, (await repository.ListAsync(null)).Count);
}
```

The test fixture helpers above must create the named rows explicitly: `CreateDatabaseWithCurrentAndHistoricalRuns` inserts two eligible runs ordered by `imported_at`; `CreateDatabaseWithRestoreBackupDependency` inserts run 1 and a `backup` run whose `restored_from_run_id` is 1; `CreateDatabaseWithRealtimeAndArtifacts` adds one `run_realtime_details` row and one quality report file; `CreateDatabaseWithRuns` inserts the requested count with valid metadata and no device payload.

- [ ] **Step 2: Run the focused repository suite and verify the safety tests fail.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionRunRepositoryTests`

Expected: FAIL because current `DeleteAsync` has no current-run, restore-dependency or activity checks and `ListAsync` is capped by callers.

- [ ] **Step 3: Implement one repository governance path.**

Inside an immediate SQLite transaction, reload each target row, compute the current eligible run using `imported_at` then `completed_at` and `id`, reject current/active/dependent targets, and validate all IDs before issuing any delete. Delete `run_realtime_details`, `run_cards`, `run_pages`, `run_sub_areas`, and `run_buildings` before `collection_runs`; record a non-sensitive `run_operations` row in the same transaction. Use `changes()` or returned row counts for the preview/result so reported counts cannot diverge from actual deletes.

- [ ] **Step 4: Implement list-all and detail evidence projection.**

The detail projection must independently report SQLite snapshot count, realtime row count, quality report state, realtime audit state, and JSON/NDJSON/manifest state. File existence alone is not valid evidence; missing batch metadata, mismatched run identity, stale report timestamps, malformed JSON, and count mismatch must have separate reasons.

- [ ] **Step 5: Run repository tests and commit.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionRunRepositoryTests`

Expected: PASS, including all existing restore, annotation-preservation, rollback and dynamic-card-count tests.

Commit: `git add native/src/EmsScout.Application/Collection/CollectionRuns.cs native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs native/src/EmsScout.Desktop/App.xaml.cs native/tests/EmsScout.Tests/CollectionRunRepositoryTests.cs && git commit -m "feat: enforce safe batch deletion in repository"`

### Task 4: Make artifact cleanup explicit and privacy-safe

**Files:**
- Create: `native/src/EmsScout.Infrastructure/Sqlite/CollectionRunArtifactCleaner.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs`
- Modify: `native/src/EmsScout.Infrastructure/Quality/JsonQualityAuditService.cs`
- Modify: `native/src/EmsScout.Infrastructure/Quality/JsonRealtimeQualityAuditService.cs`
- Modify: `scripts/quality-report.js`
- Modify: `scripts/audit-realtime-data.js`
- Modify: `scripts/collect-realtime-all-batch.js`
- Test: `native/tests/EmsScout.Tests/CollectionRunRepositoryTests.cs`
- Test: `native/tests/EmsScout.Tests/QualityAuditServiceTests.cs`

**Interfaces:**
- Produces `ArtifactCleanupResult` with deleted and pending artifact counts/reasons. The repository returns it through `CollectionRunDeleteResult`.
- New generated reports and manifests include both numeric `runId` and immutable `runKey`; readers validate both when available.

- [ ] **Step 1: Add failing tests for complete artifact coverage and locked-file semantics.**

The fixture must create `quality_report_run41.json/.txt`, `realtime_all_batch_run41.log`, `realtime_all_buildings_batch_summary_run41.json`, `collection_manifest_41.json`, a referenced JSON path, a referenced NDJSON path, and a shared `quality_report.json`. Assert only target artifacts are candidates, the shared alias survives, and a locked artifact yields pending cleanup rather than a false success.

- [ ] **Step 2: Run the focused artifact tests and verify the current cleaner fails.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionRunRepositoryTests|FullyQualifiedName~QualityAuditServiceTests`

Expected: FAIL because current cleanup only attempts two quality files plus `json_path`/`db_snapshot_path`, silently swallows failures, and does not inspect realtime artifacts.

- [ ] **Step 3: Implement metadata-validated candidate discovery.**

Use only database-directory paths or configured data-directory paths that pass `PathSafety`. Never delete a shared alias. For generated files, parse metadata before accepting a candidate; legacy files without a verifiable identity are returned as pending and are never deleted by a reused numeric ID.

- [ ] **Step 4: Implement two-phase cleanup result handling.**

Commit SQLite deletion and operation metadata first. Attempt artifact deletion second. Return `Complete` only when every accepted candidate is deleted; return `DatabaseDeletedArtifactsPending` with exact residual file names and retryable reasons for `IOException`, `UnauthorizedAccessException`, malformed legacy evidence, or missing permissions. The retry path must revalidate the target operation and never expose payload content.

- [ ] **Step 5: Emit `runKey` in future quality/realtime/manifest outputs and test legacy readers.**

Keep `runId` for compatibility, add `runKey`, and ensure `LoadForRunAsync` rejects a report whose run identity does not match. Existing reports without `runKey` remain readable for display but are classified as legacy evidence for cleanup.

- [ ] **Step 6: Run focused tests and commit.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionRunRepositoryTests|FullyQualifiedName~QualityAuditServiceTests`

Expected: PASS with shared aliases preserved and locked-file results explicit.

Commit: `git add native/src/EmsScout.Infrastructure/Sqlite/CollectionRunArtifactCleaner.cs native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs native/src/EmsScout.Infrastructure/Quality scripts native/tests/EmsScout.Tests && git commit -m "feat: report pending local batch artifacts"`

### Task 5: Correct status, evidence and all-history projections

**Files:**
- Modify: `native/src/EmsScout.Application/Collection/CollectionRunFilter.cs`
- Modify: `native/src/EmsScout.Application/Collection/CollectionDataSourceCatalog.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionRunRow.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionRunDetail.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/AuditViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/DataViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/HomeViewModel.cs`
- Test: `native/tests/EmsScout.Tests/CollectionRunMetadataTests.cs`
- Test: `native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs`

**Interfaces:**
- `HistoryStatusOptions` must include `needs_review` and show it in the default unfiltered dataset.
- `CollectionRunRow` exposes separate labels for quality, realtime evidence, deletability, current state and legacy identity; `CollectionRunDetail` exposes historical snapshot count and current count independently.

- [ ] **Step 1: Add failing tests for needs-review visibility, summary semantics and evidence labels.**

Assert that a `needs_review` run is included by the default filter, `HistoryBatchCountText` counts eligible historical runs rather than only `completed`, recoverable count excludes quality-blocked runs, and a run with 6573 snapshot cards / 6471 current cards renders both values and a delta of 102.

- [ ] **Step 2: Run the focused tests and verify the current implementation fails.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionRunMetadataTests|FullyQualifiedName~HistoryDataUiContractTests`

Expected: FAIL because status options omit `needs_review`, summary counts only `completed`, and row quality is only “已记录/未记录”.

- [ ] **Step 3: Implement a single filter/catalog projection.**

Default status is “all eligible” (`completed` and `needs_review`); `backup`, `failed` and `stopped` remain opt-in. No-data and read-failure states remain distinct. All-history callers use `ListAsync(null)`, and current-run selection uses the same imported-time ordering as Data and Home.

- [ ] **Step 4: Implement evidence labels and detail counts without changing device data.**

Use “已通过 / 需复核 / 阻断 / 未生成 / 已过期” for quality; “存在且有效 / 缺失 / 数量不一致 / 批次不匹配” for realtime evidence. A `needs_review` run remains visible but cannot be restored until its blocking quality state is cleared by a valid report.

- [ ] **Step 5: Run the focused tests and commit.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionRunMetadataTests|FullyQualifiedName~HistoryDataUiContractTests`

Expected: PASS, with existing realtime binding and dynamic-count tests unchanged.

Commit: `git add native/src/EmsScout.Application/Collection native/src/EmsScout.Desktop/ViewModels native/tests/EmsScout.Tests/CollectionRunMetadataTests.cs native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs && git commit -m "fix: surface needs-review and evidence states"`

### Task 6: Rebuild the audit page as the governance workbench

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/AuditViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionRunRow.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionRunDetail.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/AuditPage.xaml`
- Modify: `native/src/EmsScout.Desktop/Pages/AuditPage.xaml.cs`
- Test: `native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs`

**Interfaces:**
- `AuditViewModel` exposes `TimelineRuns`, `CleanupRuns`, `SelectedCleanupRuns`, `IsCleanupMode`, `SelectedRunGovernanceDetail`, `SelectedDeleteImpact`, and commands for `EnterCleanupMode`, `ExitCleanupMode`, `PreviewDelete`, `DeleteSelectedRuns`, `RetryPendingArtifacts`, and `LoadSelectedComparison`.
- The page has one selection source and one detail panel; timeline and table are alternate presentations of the same filtered collection.

- [ ] **Step 1: Add failing UI contract tests for the workbench structure and destructive-action wording.**

Required assertions include `TimelineView`, `CleanupTableView`, `GovernanceDetailPanel`, `EnterCleanupModeCommand`, `DeleteHistoricalSnapshotCommand`, `SelectedDeleteImpact`, “当前数据” protection text, and a confirmation input for a single run ID or batch count.

- [ ] **Step 2: Run the focused UI contract tests and verify the old three-pivot layout fails the new contract.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~HistoryDataUiContractTests`

Expected: FAIL on the new workbench names and cleanup-mode assertions.

- [ ] **Step 3: Implement timeline default view and compact governance summary.**

The top summary contains current batch, needs-review count, missing realtime evidence count, recoverable count, deletable count and last audit time. Each metric applies a filter to the same run collection. Each timeline node shows time, `run_id`, `run_key`, scope, card count, quality state, realtime state, current/history state and available action state.

- [ ] **Step 4: Implement table cleanup mode and right-side detail panel.**

The table has fixed columns `选择 | 批次 | 完成时间 | 楼栋范围 | 卡片数 | 质量 | 实时证据 | 状态 | 操作`. The detail panel shows snapshot/current/realtime counts, evidence reasons, quality summary, compare result, restore/delete blockers and the files/records affected by deletion. Use explicit text and familiar icon buttons; do not rely on color alone.

- [ ] **Step 5: Implement confirmation and failure UX.**

Single deletion confirms the exact displayed batch number; bulk deletion confirms selected count and filter conditions. The dialog lists what is deleted and what remains. A pending-file result says “数据库记录已删除，仍有 N 个本地文件待清理” and exposes retry, without claiming full cleanup.

- [ ] **Step 6: Run UI contracts and commit.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~HistoryDataUiContractTests`

Expected: PASS with no nested dual-scroll audit layout and no direct SQL/UI deletion logic.

Commit: `git add native/src/EmsScout.Desktop/ViewModels/AuditViewModel.cs native/src/EmsScout.Desktop/ViewModels/CollectionRunRow.cs native/src/EmsScout.Desktop/ViewModels/CollectionRunDetail.cs native/src/EmsScout.Desktop/Pages/AuditPage.xaml native/src/EmsScout.Desktop/Pages/AuditPage.xaml.cs native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs && git commit -m "feat: add batch timeline governance workbench"`

### Task 7: Unify task-page lifecycle and deletion entry points

**Files:**
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/TasksPage.xaml.cs`
- Modify: `native/src/EmsScout.Desktop/App.xaml.cs`
- Modify: `native/tests/EmsScout.Tests/TasksPageUiContractTests.cs`
- Modify: `native/tests/EmsScout.Tests/DashboardOverviewServiceTests.cs`

**Interfaces:**
- `CollectionTaskViewModel` acquires an `ICollectionRunActivity` lease for the complete task lifetime, including enumeration, import, quality, realtime snapshot and audit stages; the lease is disposed in the existing terminal cleanup path.
- Both Audit and Tasks pages call the same repository/governance delete command. Their UI may differ, but neither can bypass `RunDeleteImpact` or the repository transaction.

- [ ] **Step 1: Add failing tests for activity registration and task-page reuse of governance rules.**

Assert that every task terminal path disposes the lease, `DeleteRunAsync` handles pending artifacts distinctly, and Tasks page no longer presents a delete action that bypasses preview or current-run protection.

- [ ] **Step 2: Run the focused task/UI tests and verify the current implementation fails.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~TasksPageUiContractTests|FullyQualifiedName~DashboardOverviewServiceTests`

Expected: FAIL because the task VM only checks its own `IsRunning` flag and calls `DeleteAsync` directly.

- [ ] **Step 3: Register the singleton activity guard and use a `try/finally` lease around the task.**

Set the lease before the first mutating stage and release it in the existing final cleanup, including cancellation, process failure and exceptions. The repository sees the guard even when a second page attempts deletion concurrently.

- [ ] **Step 4: Replace direct task-page deletion with preview-confirm-delete.**

Load `RunDeleteImpact`, show exact counts and blockers, then call the shared delete operation. Preserve task logs as local non-sensitive operation summaries only.

- [ ] **Step 5: Run focused tests and commit.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~TasksPageUiContractTests|FullyQualifiedName~DashboardOverviewServiceTests`

Expected: PASS, with all existing task progress and realtime identity contracts passing.

Commit: `git add native/src/EmsScout.Desktop/ViewModels/CollectionTaskViewModel.cs native/src/EmsScout.Desktop/Pages/TasksPage.xaml.cs native/src/EmsScout.Desktop/App.xaml.cs native/tests/EmsScout.Tests/TasksPageUiContractTests.cs native/tests/EmsScout.Tests/DashboardOverviewServiceTests.cs && git commit -m "fix: share batch governance across task entry points"`

### Task 8: Add operation history, retry and restore lineage projection

**Files:**
- Modify: `native/src/EmsScout.Application/Collection/CollectionRunGovernance.cs`
- Modify: `native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/AuditViewModel.cs`
- Modify: `native/src/EmsScout.Desktop/ViewModels/CollectionRunDetail.cs`
- Modify: `native/src/EmsScout.Desktop/Pages/AuditPage.xaml`
- Test: `native/tests/EmsScout.Tests/CollectionRunRepositoryTests.cs`
- Test: `native/tests/EmsScout.Tests/HistoryDataUiContractTests.cs`

**Interfaces:**
- `ListOperationsAsync` returns operation metadata ordered by `occurred_at` and `operation_id`, independent of deleted run rows.
- Restore and delete projections display `run_id + run_key + operation_id`; `restored_from_run_id` remains a compatibility field, while new operation rows carry the immutable target key.

- [ ] **Step 1: Add failing tests for restore/delete lineage and ID reuse.**

Create run 41, restore another run to create a backup, delete the backup, create a new numeric run 41, and assert the timeline/operation list still distinguishes the old and new `run_key` values. Assert deleted operation records contain counts and status but no raw device fields.

- [ ] **Step 2: Run focused repository/UI tests and verify current numeric-only lineage fails.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionRunRepositoryTests|FullyQualifiedName~HistoryDataUiContractTests`

Expected: FAIL because current notes and `restored_from_run_id` only identify numeric IDs.

- [ ] **Step 3: Record restore, backup, delete and pending-cleanup operations.**

Use a new GUID for every operation. For restore, store target and backup run keys; for delete, store the target key, row counts and artifact result. Never write payload JSON, device names, realtime field values or report body text to `run_operations`.

- [ ] **Step 4: Project lifecycle events in the detail panel.**

Show derived collection/import/quality/realtime events from batch metadata and stored reports, plus persisted restore/delete events. Deleted batch content disappears; only the minimal operation row remains.

- [ ] **Step 5: Run focused tests and commit.**

Run: `dotnet test native/tests/EmsScout.Tests/EmsScout.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionRunRepositoryTests|FullyQualifiedName~HistoryDataUiContractTests`

Expected: PASS, including the existing restore transaction tests.

Commit: `git add native/src/EmsScout.Application/Collection/CollectionRunGovernance.cs native/src/EmsScout.Infrastructure/Sqlite/SqliteCollectionRunRepository.cs native/src/EmsScout.Desktop/ViewModels native/src/EmsScout.Desktop/Pages/AuditPage.xaml native/tests/EmsScout.Tests && git commit -m "feat: preserve local batch governance lineage"`

### Task 9: Full verification and MSIX readiness check

**Files:**
- Modify only tests or release scripts if a verified failure requires it; do not modify production data during this task.
- Test: `native/tests/EmsScout.Tests/`
- Test: `tests/run-id-allocation.test.js`
- Test: `scripts/self-test.js`
- Test: `scripts/tests/native-install-lifecycle.tests.ps1`

- [ ] **Step 1: Run the complete native suite.**

Run: `dotnet test native/EmsScout.Native.slnx --no-restore --nologo`

Expected: all tests pass, including current realtime snapshot binding, manual override preservation, export contracts, restore rollback and UI contracts.

- [ ] **Step 2: Run Node regression checks.**

Run: `node --test tests/run-id-allocation.test.js; node scripts/self-test.js`

Expected: both commands exit 0; no command writes `out/ac.db` or field artifacts because tests use temporary databases/directories.

- [ ] **Step 3: Run a disposable governance integration test.**

Create a temporary SQLite database containing current data, two historical runs, one realtime snapshot and representative artifact files. Exercise preview, blocked current deletion, successful historical deletion, locked-file pending cleanup, retry, run ID reuse and run-key separation. Assert current `cards`, `device_notes`, `device_tags`, `manual_overrides`, `realtime_match_overrides`, `monitor_groups` and `monitor_group_items` are byte-for-byte or row-for-row unchanged.

- [ ] **Step 4: Build and install only after tests pass.**

Run: `dotnet build native/EmsScout.Native.slnx -c Release --no-restore /p:UseSharedCompilation=false` and the repository's existing MSIX packaging/install commands. Verify the installed app opens the timeline, shows `run37/run39/run41` as `需复核`, shows 6573 historical versus 6471 current cards, and refuses deletion of the current batch.

- [ ] **Step 5: Perform final privacy and worktree checks.**

Run: `git status --short`, inspect `git diff --stat`, and verify no `out/field-e2e-*`, JSON, NDJSON, SQLite database, log, report or export file is staged. Push only source, tests and documentation after explicit user approval; never push local evidence files.

## Plan Self-Review

- Spec coverage: status visibility, timeline/table/detail layout, evidence validity, current/dependent deletion protection, two-phase cleanup, immutable run identity, local operation history, task-page parity, privacy boundary and release verification are covered by Tasks 1-9.
- Placeholder scan: no implementation step depends on an unspecified file, command, type or future decision; legacy artifact behavior is explicitly “displayable but pending cleanup” when identity cannot be verified.
- Type consistency: repository list-all uses `int? limit`; all new governance result records are produced in Task 1 and consumed by Tasks 3-8; the activity guard is registered in Task 7 and injected into the repository/task VM.
- Scope check: the work is one coordinated governance feature with storage and UI dependencies. It is deliberately staged so each task has an independently testable deliverable; no EMS collection algorithm or Excel contract is included.
