# GC Dependency Query Performance (`LiveDependentBackupIdsAsync`)

Status: **reproduced + fixed + unit-tested; unit suite green; system suite 23/24 with one
race-shaped failure under attribution test**
Created: 2026-08-21
Branch: `master` (uncommitted working tree)

## Problem

Owner reported a slow query on a production machine, dictated partially:

> `select distinct b.Id from Backups AS b where b.Status not in (@nonBlockingStatuses1, ...,
> @nonBlockingStatuses10) and exists (select 1 from BackupTables AS b0 left join BackupTables
> AS b1 on b0.ParentFullBackupTableId = b1.Id where b.Id = b0.BackupId and b1.Id is not null
> and b1.BackupId = @fullBackupId) or exists ...`

Identified as EF-generated output of `LiveDependentBackupIdsAsync` in
`ChoboServer/Application/BackupGarbageCollectionEvaluationService.cs:163`.

## Findings

### 1. The query was identified exactly, including the "ten" parameters

`NonBlockingDependentStatuses` has **6** entries, but EF Core 10.0.9 emits **10** placeholders.
Verified by dumping parameter values in the repro:

```
@nonBlockingStatuses1=6, 2=7, 3=8, 4=9, 5=10, 6=11, 7=11, 8=11, 9=11, 10=11
```

The six real enum values followed by four repeats of the last — EF pads the `IN` list to a
bucket size for plan-cache reuse. An earlier note in this session guessed the owner had
miscounted; that was **wrong**, the dictation was literally accurate.

The local `var nonBlockingStatuses = NonBlockingDependentStatuses;` (`:165`) exists only so EF
can capture it as a closure variable, which is why the SQL parameters carry that name.

### 2. Root cause: the query is driven from the wrong end

It scans `Backups` and asks each row "do you descend from `@fullBackupId`?".

- `Status NOT IN (...)` is a negated low-cardinality predicate — `IX_Backups_Status` is useless,
  the outer table is fully scanned.
- The `OR` between two correlated `EXISTS` cannot be index-driven; rows failing the first branch
  pay the second.
- The second branch is three levels deep: `BackupTables` -> `BackupTableShards` -> parent shard
  -> **back to `BackupTables`** just to read `BackupId`. That last hop exists only because
  `BackupTableShards` carries no `BackupId` (the same structural gap documented in
  `backup-list-query-performance.md`).
- The selective fact — `@fullBackupId` owns ~300 tables — sits on the *inner* side, so nothing
  can use it to bound the outer scan.

### 3. Amplification: it runs once per backup

`RetentionManagementBackgroundService.MarkExpiredAsync` (`:95`) loops every retention candidate
of every policy through `EvaluateAsync`. Each call that reaches `containsFullWork == true` runs
this query. So the real cost is (number of full backups) x (per-call cost), serially.

### 4. Latent bug found: the `ORDER BY` was silently dropped

`.OrderBy(...).Select(x => x.Id).Distinct()` — EF discards the ordering when `Distinct` follows.
Confirmed in the captured SQL (no `ORDER BY` clause at all). So `RelatedBackupIds` and the
user-facing message *"still required by N active child backup(s): ..."* were in arbitrary order.
The rewrite restores the intended completion-time ordering. **This is a visible output change.**

### 5. Test-coverage gap found

`TestFixture.SeedDependentIncrementalAsync` (`BackupRestoreExecutionTests.cs:7244`) sets **both**
`ParentFullBackupTableId` and `ParentFullBackupTableShardId`, so no existing test covered either
linkage branch in isolation. The shard-only branch had zero coverage.

### 6. The system-test perf fixture cannot exercise this query at all

`TestHooksController.SeedLargeMetadataGraph` (`:474`) sets `ParentFullBackupId` but **never**
`ParentFullBackupTableId` / `ParentFullBackupTableShardId`. Both `EXISTS` branches therefore
match nothing in `LargeMetadataResponsiveness`. Not fixed — see open items.

## Measured reproduction

Harness: `Chobo.Tests/GcDependencyQueryReproTests.cs` (**TEMPORARY — must be removed or
converted before commit**). Seeds a production-shaped graph via raw SQL recursive CTEs, runs
`PRAGMA analysis_limit=400; ANALYZE;`, then times the current LINQ against the proposed one on
the same `ChoboDbContext`, asserting identical results.

Scale: **200 backups / 60,000 tables / 480,000 shards**, 1 full backup per 10, real
`ParentFullBackupTableId` + `ParentFullBackupTableShardId` chains.

| Measurement | before | after | change |
| --- | --- | --- | --- |
| single `LiveDependentBackupIdsAsync` call | 429.0 ms | **22.5 ms** | **19x** |
| full retention sweep (20 full backups) | 8,583 ms | **494 ms** | **17x** |

Both return the same 9 dependents.

Cached DB at `%TEMP%\chobo-gc-repro\repro.db` (delete to force a reseed; seeding takes ~1 min).

Gotchas hit while building the repro, worth knowing:
- EF stores `Guid` as **uppercase** `D`-format TEXT; `Guid.ToString("D")` is lowercase. Raw-SQL
  seeding with lowercase ids fails FK checks.
- `Microsoft.Data.Sqlite` enables `PRAGMA foreign_keys` per connection by default, so raw-SQL
  seeds *are* FK-checked even though the product never issues the pragma itself.

## The fix (implemented)

`ChoboServer/Application/BackupGarbageCollectionEvaluationService.cs:163` — inverted to start
from the parent's own tables/shards and walk *down* to children:

```csharp
var parentTableIds  = db.BackupTables.Where(t => t.BackupId == fullBackupId).Select(t => t.Id);
var parentShardIds  = db.BackupTableShards.Where(s => parentTableIds.Contains(s.BackupTableId)).Select(s => s.Id);
var dependentBackupIds =
    db.BackupTables.Where(t => t.ParentFullBackupTableId != null && parentTableIds.Contains(t.ParentFullBackupTableId.Value))
      .Select(t => t.BackupId)
      .Concat(db.BackupTableShards
          .Where(s => s.ParentFullBackupTableShardId != null && parentShardIds.Contains(s.ParentFullBackupTableShardId.Value))
          .Select(s => s.BackupTable!.BackupId));
return await db.Backups.AsNoTracking()
    .Where(child => !nonBlockingStatuses.Contains(child.Status) && dependentBackupIds.Contains(child.Id))
    .OrderBy(x => x.CompletedAt ?? x.CreatedAt).Select(x => x.Id).ToListAsync(ct);
```

Generated SQL becomes `... WHERE Status NOT IN (...) AND b.Id IN (SELECT ... UNION ALL SELECT ...)`
— every step an index seek on a bounded set. **No schema change, no new index, no API change.**
All three indexes it relies on already exist (`IX_BackupTables_BackupId`,
`IX_BackupTables_ParentFullBackupTableId`, `IX_BackupTableShards_ParentFullBackupTableShardId`).

## Tests added

`Chobo.Tests/BackupRestoreExecutionTests.cs`, after the existing evaluation test (~`:4424`):

| Test | Covers |
| --- | --- |
| `Garbage_collection_finds_dependents_linked_only_through_shards` | shard branch in isolation (previously zero coverage) |
| `Garbage_collection_finds_dependents_linked_only_through_tables` | table branch in isolation |
| `Garbage_collection_ignores_dependents_already_committed_to_deletion` | `[Theory]`, all 6 non-blocking statuses |
| `Garbage_collection_orders_dependent_ids_by_completion_time` | the dropped-`ORDER BY` bug |

Verified meaningful: reverting only the service file and re-running makes
`..._orders_dependent_ids_by_completion_time` **fail** (returns insertion order). The other three
pass on both implementations — they guard the rewrite, not the old bug.

## Validation status

| Gate | Status |
| --- | --- |
| `dotnet build Chobo.sln -c Release` | **PASS** — 0 warnings |
| New GC tests (filter `~Garbage_collection`) | **PASS** — 11/11 |
| Repro harness (identical results assertion) | **PASS** |
| Full unit suite `dotnet test Chobo.Tests` | **PASS** - 316/316 (307 pre-existing + 9 new), 1 m 14 s |
| `TestingSuite/TestManager.ps1` full system suite | **23/24** - `IncrementalMultipleFullParents` failed, see below |
| `ChoboWeb` `npm run typecheck` / `npm test` | **NOT RUN** (no web change, contract unchanged) |

## Plan / remaining work

1. ~~**Delete or convert** `Chobo.Tests/GcDependencyQueryReproTests.cs`.~~ **DONE** - deleted from
   the test project (it was never committed). A copy is parked outside the repo at
   `%TEMP%\claude\C--Projects-Chobo\8d0ca2e7-8656-44a5-8a1b-84faa9ccd5d7\scratchpad\GcDependencyQueryReproTests.cs.bak`
   if the measurement ever needs re-running; that scratchpad is session-scoped, so treat this
   document (not the file) as the durable record.
2. ~~Run the full unit suite.~~ **DONE** - 316/316 pass, no pre-existing test asserted on
   `RelatedBackupIds` order, so the restored `ORDER BY` broke nothing.
3. ~~Run the **full** system suite.~~ **DONE** - 23/24 passed
   (`-TestId gc-dep-fullsuite-20260821 -RunAllConcurrency 1`). One failure, under investigation:

   ### `IncrementalMultipleFullParents` :: `unrelated-parent-still-live`

   Expected `invoicesFull` to be `Succeeded`, got `BackupExpiredDeleted`.

   Server log timeline (`logs-choboserver.stdout.log`):
   ```
   20:28:35.513  cleanup of incremental 9aed893a (tableCount=2)  reason=manual
   20:28:35.552  invoicesFull c94e3a5a -> BackupExpiredDeleted   reason=retention
   20:28:35.86   test asserts invoicesFull is Succeeded          -> fails
   ```

   **The rewrite is not returning wrong results.** The two preceding steps
   `retention-keeps-orders-parent` and `retention-keeps-invoices-parent` both passed, proving the
   query correctly protected both parents while the incremental was live. invoicesFull was expired
   only 39 ms *after* the incremental became non-blocking - the same input the old query saw. The
   rewrite is membership-equivalent (removing `Distinct` cannot change membership; both branches map
   one-to-one onto the old `EXISTS` clauses).

   **Suspected pre-existing race in the test.** Retention interval is 1 s in system tests
   (`ComposeGenerator.psm1:513`), full retention was shortened to 1 minute and `min-backups-to-keep`
   is 0, so invoicesFull becomes legitimately expirable the instant the incremental stops blocking.
   The test's next CLI call has only a few hundred ms to win, and the preceding step *polls* until
   the cascade lands, handing retention a head start. Attribution experiment in flight: 3 runs with
   the change, 3 with only the service file reverted. Do not commit until that resolves.

   Note also: `LargeMetadataResponsiveness` **is** running in run-all despite declaring
   `ExcludeFromRunAll = $true`; the flag appears to be honoured for declarative `.psd1` tests but not
   for scripted `Test.ps1` ones. 24 tests ran, not the 25 that would follow from the directory count.
4. Decide on release positioning — patch on the 1.1.x line (**1.1.5**); no `SchemaVersion`,
   `ExportVersion`, or `ApiVersion` change.
5. Commit, including the two untracked `backup-list-query-performance*.md` state files from
   PR #99 that were never committed.

## Open items (found, deliberately not fixed)

- **`containsFullWork` (`:113-115`) has the same shape** — `x.BackupTable.BackupId == backup.Id`
  joins shards through tables, and it runs for *every* backup in the retention loop, not just
  full ones. Not measured, not changed; out of the reported scope.
- **`SeedLargeMetadataGraph` seeds no parent-table/parent-shard links**, so no system test
  exercises this query's real path. Extending it would make `LargeMetadataResponsiveness` a
  genuine regression gate for this fix.
- The `ORDER BY`-dropped-by-`Distinct` pattern may exist elsewhere; only this call site was
  checked.
