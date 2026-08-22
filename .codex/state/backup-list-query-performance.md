# Backup List Query Performance (shard -> backup denormalization)

Status: **implemented, reviewed, re-measured** - schema v3 NOT needed; final system gate running
Owner: agent-assisted, driven by @shahar
Created: 2026-08-20

## Problem

Reported from production (hundreds of tables, 8 shards, 3 replicas):

- The GUI **Backups** page is slow to open.
- The GUI **Dashboard** (home) is also slow.

### Measured cause (code reading, not yet profiled in production)

`GET /api/v1/backups?includeTables=false` (`BackupApplicationService.ListAsync`) issues 7
sequential queries. Working set at reported scale is ~200 backups x ~300 tables = ~60k
`BackupTables` rows and x8 shards = ~480k `BackupTableShards` rows.

| # | Query | Rows touched | Problem |
| --- | --- | --- | --- |
| 1 | `Backups ... LIMIT 200` | 200 | fine, uses `IX_Backups_CreatedAt` |
| 2 | `tableStats` count+sum | ~60k | full pass over `BackupTables` |
| 3 | `tableRelatedFullRows` | ~60k | second pass over `BackupTables` |
| 4 | `shardRelatedFullRows` | ~480k | **joins shards -> tables** to reach `BackupId` |
| 5 | `ChildBackupIds` (2 queries) | ~480k | same join, inverse direction |
| 6 | `passwordRows` | ~480k | same join again, for encryption state |
| 7 | `KeyAvailability` | tiny | fine |

Structural root cause: **`BackupTableShards` has no `BackupId`**. It stores `BackupTableId`
only, so every backup-level shard aggregate must join through `BackupTables`. Every existing
shard index is keyed on `BackupTableId`, so none can serve a "for these 200 backups" predicate.

Note the existing asymmetry: a shard *already* stores `ParentFullBackupId`, which is a
`Backups.Id`. It knows about the backup it descends from but not the one it belongs to.

The Dashboard is slow for two reasons: it calls the same list endpoint, and
`DashboardApplicationService.GetDashboardAsync` computes 5 correlated subqueries per running
backup over `x.Tables.SelectMany(t => t.Shards)`.

## Decision

Implement **Layer 1** of the three options presented to the owner: denormalize `BackupId`
onto `BackupTableShards`, add indexes that can serve backup-scoped predicates, and rewrite
every shard->table join site to use the new column.

### Options considered

| Option | Outcome | Decision |
| --- | --- | --- |
| **Layer 0** - drop unused list fields | Removes queries 3-5; no DDL; but changes API response content | **Rejected** - superseded. Once Layer 1 makes those queries index seeks, there is no reason to weaken the API contract. |
| **Layer 1** - `BackupId` on shards + indexes | Queries 4,5,6 become index-only seeks; dashboard subqueries collapse to one grouped scan | **Chosen** |
| **Layer 2** - rollup columns on `Backups` | Also removes queries 2,6; list becomes a single 200-row query | **Deferred** - the only piece introducing derived state that can drift. Revisit if production is still slow after Layer 1. |

### Rationale for choosing Layer 1 alone

`BackupTableShards.BackupId` is **write-once and immutable** for the life of the row. It is
set at shard creation and can never legitimately change, so unlike Layer 2's aggregates it
introduces no drift class. Layer 1 removes the pathological cost (~1.4M row touches per page
load down to index seeks) without adding state that needs reconciliation.

## Scope

### In scope

1. Schema v3: `BackupTableShards.BackupId` column + backfill.
2. New indexes; drop the now-redundant `IX_BackupTableShards_Encrypted_BackupTableId`.
3. `ChoboApi.SchemaVersion` 2 -> 3, migration `000000000003_*`, `SchemaUpgradeService` 2->3 arm.
4. Entity + `ChoboDbContext` model configuration.
5. All shard **write** sites must populate `BackupId`.
6. All shard **read** sites rewritten to drop the join.
7. `DashboardApplicationService` running-backup counts rewritten to one grouped query.
8. Unit tests, schema upgrade tests, `scripts/Test-UpgradeSamples.ps1`, system test.
9. Developer documentation for the schema version change.

### Out of scope (explicitly deferred)

- Layer 0 field removal and Layer 2 rollup columns.
- Server-side pagination for `/api/v1/backups` (currently a hard `Take(200)` with no offset).
- Response payload trimming (`ManualRequestJson` is sent on every list row and never read by
  the UI; `ClickHouseBackupSettingsJson` is deserialized 3x per row in `BackupRestoreMapping`).
- **Issue #2 from the same report** - backups failing when a SQLite metadata write hits a lock.
  Owner chose the conservative fix (classify `SQLITE_BUSY`/`locked` as transient in
  `IsTransientShardFailure`, strengthen backoff; leave audit writes fatal and leave the
  post-ClickHouse-success write path unchanged). Tracked separately as a small feature.

### Non-goals

- No change to the public API contract. `BackupDto` keeps `RelatedFullBackupIds`,
  `ChildBackupIds`, `TableCount`, `BackupSizeBytes`, and `EncryptionState` exactly as today.
- No behavioural change of any kind. This is a pure performance/representation change; all
  query results must be identical before and after.

## Versioning context

Verified against the schema-versioning skill (`.codex/skills/chobo-schema-versioning`):

- Latest published tag: **v1.1.3**. `ChoboApi.SchemaVersion = 2` is therefore **published**.
- The baseline migration must **not** be edited. A new migration is required.
- Per the skill's release rules: `SchemaVersion` increases on the same major line, so the next
  release minor must increase and patch resets -> **1.2.0**.
- `ExportVersion` stays at 1: `BackupId` is derivable from existing envelope data, so the
  serialized envelope shape does not change and import recomputes the column.
- **Note:** the skill file itself is stale - it states "Current development schema:
  `ChoboApi.SchemaVersion = 1`" and "Latest published release: v0.1.0". Should be refreshed.

## Known call sites (from grep; design must confirm completeness)

**Shard creation (must set `BackupId`):**
- `Application/BackupPreparationService.cs:233`
- `Application/BackupRunnerService.cs:527`
- `Application/BackupStorageManifestService.cs:457` (metadata recovery)
- `Services/ExportImportService.cs:181` (import)
- `Controllers/TestHooksController.cs:84,264,451,634`
- `Services/ClickHouseAdapter.cs:192,218` - **transient, never persisted** (used only for SQL
  building). Design should confirm these can be left alone.

**Shard->table join reads (rewrite to use `BackupId`):**
- `Application/BackupApplicationService.cs:279,280,292,293,372,644,664,665,688,689`
- `Application/BackupGarbageCollectionEvaluationService.cs:115,170`
- `Application/BackupPreparationService.cs:260,378`
- `Application/BackupRunnerService.cs:1011`
- `Application/RestoreApplicationService.cs:615`
- `Application/RestoreRunnerService.cs:478,507,530`
- `BackgroundServices/BackupsGarbageCollectorBackgroundService.cs:348,356,484,507`
- `BackgroundServices/DataRetentionBackgroundService.cs:107-119`
- `Chobo.Tests/BackupRestoreExecutionTests.cs` - ~15 assertion sites

## Risks

| Risk | Mitigation |
| --- | --- |
| A write path is missed and inserts an empty `BackupId` | Explicit assignment at all production creation sites **plus** a normalise-or-throw guard in `ChoboDbContext.SaveChanges*` (design 4). Guard soundness is under design review. |
| `SchemaUpgradeService` currently has a single hard-coded `1 -> 2` arm | **Bumping to v3 without restructuring breaks every v1 database.** Four published samples (`.release/db-samples/1.0.0`-`1.0.3`) are v1 and would throw "No upgrade path is registered from schema version 1 to 3". Design restructures it into a version ladder. |
| `DatabasePerformanceMaintenance.EnsureAsync` recreates indexes on every boot | Dropping a redundant index in the migration without also removing its `CREATE INDEX IF NOT EXISTS` line (`Data/DatabasePerformanceMaintenance.cs:42-43`) means the next startup resurrects it. Must be changed in the same commit. |
| Backfill cost on a large production DB at upgrade | One-time `UPDATE` during startup while the server is down; backfill runs **before** the new indexes are built so the 480k-row update pays no index maintenance. Logged with timing. Validated against `.release/db-samples`. |
| Schema v3 is a one-way door (server rejects newer schema) | Documented in release notes; standard for this product. |
| Index storage growth (Guid stored as 36-char TEXT) | ~17 MB for the column plus ~40-60 MB of indexes at 480k rows. Accepted; recorded here. |
| Behaviour drift in rewritten queries | Every rewrite must be result-identical. The ~16 existing test assertions that use `x.BackupTable!.BackupId` are deliberately **left unchanged** so they become free cross-checks that the denormalized column agrees with the join. |

## Corrections applied after the low-level design pass

The design pass verified every claim in this file against source. These were wrong and have
been corrected above:

1. ~~"SQLite cannot `ADD COLUMN NOT NULL` without a constant default"~~ - **wrong**. SQLite can,
   given a non-null constant default, and this repo already does it
   (`000000000002_PasswordProtectedBackups.cs:14` adds `PasswordMode INTEGER NOT NULL DEFAULT 0`).
   The column is therefore `NOT NULL DEFAULT '<empty guid>'`, not nullable. A nullable column
   would also have made the migration-built schema diverge from the `EnsureCreated`-built
   schema the unit tests run against.
2. ~~"Cascade delete already works via the DDL `ON DELETE CASCADE`"~~ - right conclusion, wrong
   mechanism. `PRAGMA foreign_keys` is never issued anywhere in the repo, so SQLite FK
   enforcement is **off** and the baseline's cascade clauses are inert. The real guarantees are
   EF's `DeleteBehavior.Cascade` and the ordered `ExecuteDeleteAsync` chain in
   `DataRetentionBackgroundService.cs:169-176`.
3. The **write-site inventory below is incomplete** - it omits seven test creation sites
   (`ChoboFoundationTests.cs:664,665,979,1944,2439`, `BackupRestoreExecutionTests.cs:7116`).
4. The **read-site inventory below is over-broad**. `BackupPreparationService.cs:378` is not a
   rewrite site (the query genuinely needs the join for `Database`, `Table`, `Backup.Status`);
   `RestoreApplicationService.cs:615` and `RestoreRunnerService.cs:478,507,530` are in-memory
   navigation over `Include`d graphs, not SQL joins.
5. `ExportVersion` staying at 1 is **confirmed correct** - and bumping it would actively break
   import of all eight published db-sample envelopes, because `ExportImportService.cs:77`
   compares with `!=` rather than a range check.

## Plan / checklist

- [x] Discovery and option analysis; owner selected Layer 1
- [x] State file created
- [x] Low-level design -> `.codex/state/backup-list-query-performance_design.md`
- [x] Independent design review - **6 HIGH findings; not safe to build as written**
- [x] "Do I really need this?" review - **recommends measure-first, index-only patch**
- [x] Baseline measured at 720k shard rows (see below)
- [ ] Owner decision on the tradeoffs below
- [ ] Re-design / re-review after the decision
- [ ] Ordered implementation steps recorded + owner plan confirmation
- [ ] Implementation (per step, each followed by a code review loop)
- [ ] Edge-case / test-gap pass
- [ ] Final completeness gate

## Validation approach

- `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s`
- `scripts/Test-UpgradeSamples.ps1` - replays the upgrade across every published db sample
- `scripts/Test-ReleaseVersionPolicy.ps1 -Version 1.2.0` - schema advisory
- `TestingSuite/TestManager.ps1` - **the FULL system test suite** (owner requirement, 2026-08-20).
  Invoke with no `-TestName` to run all 27 tests; raise `-GlobalTimeoutSeconds` well above the
  1800s default. Do not run concurrent `TestManager.ps1` invocations.
- `ChoboWeb`: `npm run typecheck`, `npm test` (expected no-op; contract is unchanged)

## Unresolved questions

- ~~Whether to add a startup consistency check for orphan shard rows.~~ **Answered by design:**
  run the check once inside the 2->3 upgrade arm (an indexed equality seek), not on every boot.
  A per-boot scan would cost seconds forever to detect a condition the `SaveChanges` guard
  makes unreachable.
- Open: is the `SaveChanges` guard in `ChoboDbContext` the right home, given the onion
  architecture rules in `AGENTS.md`? Referred to design review.

## Implementation notes

(to be filled in during implementation)

## Validation evidence

(to be filled in as steps complete)


## Baseline measurement (2026-08-20)

`TestManager.ps1 -TestId perf-baseline-20260820-01 -TestName LargeMetadataResponsiveness`
Seeded 300 backups / 30k backup tables / **720k shard rows** (1.5x reported production scale).
Result: passed in 109s. Artifacts: `.artifacts/TestResults/perf-baseline-20260820-01`.

| Endpoint | elapsed ms | budget ms | payload |
| --- | --- | --- | --- |
| `GET backups?includeTables=false` | **3067** | 5000 | 279 KB |
| `GET backups/{id}` (detail drawer) | **2995** | 5000 | 2.5 MB |
| `POST backups/garbage-collector/run` | 372 | 5000 | - |
| `GET backups/{id}?includeTables=false` | 138 | 2000 | - |
| `GET dashboard?nextHours=24` | **70** | 5000 | 1.2 KB |
| `GET queue?status=all&limit=1000` | 22 | 5000 | - |
| `GET audit?last=50` | 17 | 3000 | - |
| `GET logs?last=50` | 15 | 3000 | - |

### What this changes

1. **The problem is exactly one endpoint.** `GET /api/v1/backups?includeTables=false` at 3.07s.
   Both slow pages consume it, so fixing it alone fixes both.
2. **The Dashboard endpoint is not slow - it is 70 ms.** The entire proposed
   `DashboardApplicationService` rewrite (design 5.6, the five correlated subqueries) targets a
   70 ms endpoint and should be **cut**. The Dashboard page is slow only because
   `ChoboWeb/src/pages/DashboardPage.tsx:20` also calls the 3s backups list. This also settles
   the disagreement between the two reviews: the necessity review claimed the Dashboard never
   calls the list (wrong), and this file originally claimed the dashboard subqueries were a
   cause (also wrong - they are 70 ms).
   Caveat: the fixture seeds no `Running` backup, so the subqueries themselves are measured
   against an empty set. They remain bounded by the number of concurrently running backups.
3. **The detail drawer is a second, separate problem** at 2995 ms / 2.5 MB. Not reported by the
   owner, not in scope, but it is within 2s of its budget and should get its own ticket.
4. **Scope collapses** from ~25 files to: the six `BackupApplicationService.ListAsync` queries.
   Every background-service and restore-path rewrite in the design is now unjustifiable - none
   is on the measured path.

## Review findings (2026-08-20)

Both passes ran against `_design.md`. Findings verified against source where load-bearing.

### Blocking correctness defects in the design

| # | Finding | Verified |
| --- | --- | --- |
| H1 | **The migration would ship with zero test coverage.** `scripts/Test-UpgradeSamples.ps1` upgrades exactly one sample - `Get-LatestPriorMinorSample` ends in `Select-Object -First 1`, so for a 1.2.0 target only `1.1.3` runs; the four v1 samples never start. And `MigrateAsync` appears nowhere in `Chobo.Tests` - both fixtures use `EnsureCreatedAsync`. The v1->v3 ladder, the design's own headline fix, would be untested. | **Yes** - script read; all 8 samples opened: `1.0.x` = v1, `1.1.x` = v2 |
| H2 | `DependentBackupIdsAsync` (`BackupApplicationService.cs:678-692`) has no `OrderBy`, unlike its two siblings. The rewrite changes which index satisfies `DISTINCT`, so `BackupDto.ChildBackupIds` array order changes for identical database state - violating the zero-behavioural-change constraint. | Yes |
| H3 | Re-keying the retention purge (`DataRetentionBackgroundService.cs:158-174`) onto the denormalized column means a wrong `BackupId` leaves shard rows alive after their parent table is deleted - **permanently unrepairable orphans**. `grep "Shards.Remove"` returns nothing, so that `ExecuteDelete` chain is the *only* physical shard cleanup in the product; the "EF cascade" mitigation is vacuous. | Yes |
| H4 | The `SaveChanges` guard is **not** free. `ChangeTracker.Entries<T>()` forces a full `DetectChanges()` over all tracked entities *before* the early return. It lands on `BackupRunnerService`'s large `Include(Tables).ThenInclude(Shards)` graphs and per-shard scoped contexts - **the exact write path implicated in issue #2's SQLite lock contention**. | Yes |
| H5 | The guard throws on `db.BackupTableShards.Add(new { BackupTableId = <existing id> })`, a legal EF insert. No site does it today, but it would fail at runtime in a background worker mid-backup. | Yes |
| H6 | The guard only checks `BackupId == Guid.Empty`, so a site setting the **wrong non-empty** value passes silently. `BackupPreparationService.cs:186-233` has four plausible Guids in scope. With H3, a wrong value becomes a permanent orphan. | Yes |

Medium findings recorded: the dashboard two-query split trades atomicity for nothing (M1 - now
moot, see baseline); the design's claim that SQLite stores Guids as lowercase TEXT is
**backwards**, the published samples store uppercase (M2); no free-space precondition for the
migration (M3); `TestManager.ps1` takes `-TestName`, not `-Tests` (M4);
`BackupStorageManifestService.cs:582` stamps `SchemaVersion` into every storage manifest and was
omitted from the consumer list (M5).

### Scope and necessity findings

1. **Layer 1 does not make the list endpoint scale.** `ListAsync` does `Take(200)` and production
   has ~200 backups, so the 200 index ranges cover the whole 480k-entry index. The endpoint stays
   `O(total shard rows)`. The win is ~480k *wide* row fetches (~120 MB table) becoming ~480k
   *narrow* index entries (~25-30 MB), three times over - a 3-5x I/O reduction, **not**
   asymptotic. This invalidates the reason recorded earlier for rejecting Layer 0.
2. **A no-schema-change alternative gets most of the win.** Widen the existing shard indexes to
   be covering: `(BackupTableId, ParentFullBackupId)`, `(ParentFullBackupId, BackupTableId)`, and
   `(BackupTableId, EncryptedBackupPasswordKeyId) WHERE EncryptedBackupPassword IS NOT NULL`.
   Net index count **unchanged** - `IX_BackupTableShards_BackupTableId` is a strict prefix of
   `IX_BackupTableShards_BackupTableId_SourceShardNumber` and is dead weight today. Ships via
   `DatabasePerformanceMaintenance` as a **1.1.4 patch**. It keeps the `x.BackupTable != null`
   guards, so SQL semantics are provably unchanged - which deletes H2-H6 and the entire
   invariant-protection design.
3. **`ANALYZE` / `PRAGMA optimize` are never issued anywhere in the repo.** SQLite is planning
   every one of these joins with no `sqlite_stat1`. One line; may move the number alone.
4. **Two blind spots in the perf fixture** (verified in `TestHooksController.cs`): every seeded
   backup is `Succeeded`, so there is no `Running` backup; and no seeded shard sets
   `EncryptedBackupPassword`, so `passwordRows` hits an empty partial index. Both understate the
   measured 3067 ms.

## Recommended revised sequencing

| Increment | Content | Release |
| --- | --- | --- |
| **0. Fixture fidelity** | Seed `Running` backups and encrypted shards so the two blind spots are covered. Re-measure. | none |
| **1. Cheap wins** | `PRAGMA optimize` + the covering-index swap in `DatabasePerformanceMaintenance` / `OnModelCreating`. Re-measure. | 1.1.4 patch |
| **2. Decide** | If increment 1 closes it, stop. Otherwise choose Layer 0 (`?includeRelations=false`, makes the endpoint `O(page)`) or Layer 1 (schema v3, re-designed against H1-H6). | - |
| **3. Schema v3** | Only if the numbers demand it. | 1.2.0 |

Cut from any increment, on the baseline evidence: the `DashboardApplicationService` rewrite, and
all background-service / restore-path rewrites.

## Tradeoffs needing an explicit owner decision

1. Ship the measured index-only patch first (1.1.4), or proceed straight to schema v3 (1.2.0)?
2. Is a startup-blocking orphan check acceptable? Both reviews say no - a production backup
   server that will not boot is worse than the condition it detects.
3. `relatedFullBackupIds` is never read from a list row by the GUI; `childBackupIds` is read once
   for a delete-confirmation count. An opt-out query parameter is the only option that makes the
   endpoint `O(page size)`, but it touches `ChoboCli/Commands/BackupCommands.cs:191-199`.


## Increment 1 result (2026-08-20) - owner chose "cheap wins first, re-measure"

Owner decisions: (1) ship the index/query-only work first and re-measure; (2) fold the detail
drawer in, since the baseline exposed it at 2995 ms.

### Changes made (no schema version bump, targets release 1.1.4)

`ChoboServer/Data/ChoboDbContext.cs` + `ChoboServer/Data/DatabasePerformanceMaintenance.cs`:

| Action | Index |
| --- | --- |
| widened | `IX_BackupTables_BackupId` -> `IX_BackupTables_BackupId_ParentFullBackupId_BackupSizeBytes` |
| added | `IX_BackupTableShards_BackupTableId_ParentFullBackupId` |
| widened | `IX_BackupTableShards_ParentFullBackupId` -> `..._ParentFullBackupId_BackupTableId` |
| widened | `IX_BackupTableShards_Encrypted_BackupTableId` -> `..._Encrypted_BackupTableId_KeyId` (carries `EncryptedBackupPasswordKeyId`) |
| dropped | `IX_BackupTableShards_BackupTableId` - strict prefix of `..._BackupTableId_SourceShardNumber`, dead weight since the baseline |

Net index count on `BackupTableShards`: unchanged. Plus `PRAGMA analysis_limit=400; ANALYZE;` at
startup - the repo previously issued no `ANALYZE`/`PRAGMA optimize` anywhere, so SQLite was
planning every one of these joins with no `sqlite_stat1` at all.

`ChoboServer/Application/BackupApplicationService.cs`:
- new `LoadForReadAsync` (`AsNoTracking` + `AsSplitQuery`) used **only** by the read path of
  `GetAsync`; the mutating callers of `LoadAsync` (pin/unpin/cancel/post-commit reload) keep
  tracking. Shard and table ordering is explicit in `BackupRestoreMapping` (`:44`, `:157`), so
  split query cannot reorder output.
- `AsNoTracking` on the `includeTables: true` branch of `ListAsync` (the API default, so the CLI
  hits it). **Not** `AsSplitQuery` there: that branch has `Take(200)` with a non-unique
  `OrderByDescending(CreatedAt)`, and split queries with `Take` over a non-unique ordering can
  return incorrect results.

### Measured result - identical fixture, 300 backups / 30k tables / 720k shard rows

| Endpoint | before | after | change |
| --- | --- | --- | --- |
| `GET backups?includeTables=false` | 3067.1 ms | **461.7 ms** | **6.6x faster** |
| `GET backups/{id}` (detail drawer) | 2995.1 ms | **112.5 ms** | **26.6x faster** |
| `GET backups/{id}?includeTables=false` | 138.2 ms | 48.2 ms | 2.9x faster |
| `GET dashboard?nextHours=24` | 70.1 ms | 68.6 ms | unchanged |
| `POST backups/garbage-collector/run` | 372.3 ms | 407.8 ms | ~10% slower |
| `cli dashboard show` | 212.1 ms | 284.6 ms | ~34% slower |

Artifacts: `.artifacts/TestResults/perf-baseline-20260820-01` and `...perf-after-idx-20260820-01`.

The last two rows are regressions well inside budget and are most likely run-to-run noise
(separate container runs, and the seed step itself varied 97s -> 106s). No index capability was
removed - both dropped/widened indexes retain the original leading column as a prefix. Flagged
rather than dismissed; worth confirming on a repeat run.

### Consequence: schema v3 is not needed

The 3067 ms endpoint that motivated the whole Layer 1 design is now 462 ms with no schema change,
no migration, no backfill, no upgrade ladder, no write-site changes, and no `SaveChanges` guard.
Every one of the six HIGH design defects (H1-H6) is moot because none of that code exists.

**Layer 1 / schema v3 is hereby deferred indefinitely.** `_design.md` is retained for reference; if
it is ever revived, H1-H6 must be designed out first. `ChoboApi.SchemaVersion` stays at **2** and
the target release stays on the **1.1.x** patch line.

## Issue #2 - SQLite lock resilience (owner chose "conservative: classify + retry only")

Implemented alongside, in `ChoboServer/Data/SqliteTransientErrors.cs`:

- `IsTransientLock(Exception?)` walks the `InnerException` chain for `SqliteException` with
  `SqliteErrorCode` 5 (`SQLITE_BUSY`) or 6 (`SQLITE_LOCKED`).
- `RetryDelay(attempt)` - exponential backoff with jitter, capped, replacing the previous fixed
  1 s delay. Jitter matters because backup shard workers run on parallel scoped DbContexts.
- `ChoboDbContext` retry count 3 -> 5, now catching `Exception` with an `IsTransientLock` filter.
- `IsTransientShardFailure` in **both** `BackupRunnerService` and `RestoreRunnerService` now
  classifies a transient SQLite lock as retryable. The restore runner had the identical defect;
  it is fixed for symmetry.

### Root cause found: the pre-existing retry was dead code

`ChoboDbContext` previously caught `catch (SqliteException ex)`. EF Core wraps provider exceptions
from `SaveChanges` in `DbUpdateException`, so **that catch never fired** - the busy/locked retry
has never worked. Proven by the new test
`SqliteTransientErrorsTests.A_contended_save_surfaces_the_lock_wrapped_in_DbUpdateException`,
which asserts `Assert.IsNotType<SqliteException>(thrown)` against a real locked database.
This is the most likely direct cause of the reported production symptom.

### Secondary finding: PRAGMA busy_timeout may not be what is in effect

`Microsoft.Data.Sqlite` applies its own `Default Timeout` (default **30 s**) per command, which
overrides the `PRAGMA busy_timeout={15s}` set by `SqlitePragmaConnectionInterceptor`. Discovered
while building the test: with the pragma set to 0 the command still blocked past 20 s, and only
setting `Default Timeout=1` in the connection string made it deterministic. Implication:
`ChoboSqliteOptions.BusyTimeout` may be largely inert, and real contention waits ~30 s per command
before surfacing. **Not fixed here** - out of the agreed conservative scope. Worth its own ticket.

### Tests added

`Chobo.Tests/SqliteTransientErrorsTests.cs` - 6 tests: classifier for bare/wrapped/unrelated/null
errors, backoff growth and bound, plus two real-contention integration tests (one asserting the
wrapped exception type, one asserting recovery once the lock clears). All bounded by an explicit
20 s guard so a regression fails fast instead of hanging.

## Validation evidence

- `dotnet build Chobo.sln -c Release` - clean, 0 warnings.
  (Note: `dotnet build` in **Debug** fails on this machine - `ChoboServer.csproj:51` runs
  `npm run build` for ChoboWeb and Node is not installed. Release skips that target. Consequently
  `npm run typecheck` / `npm test` could not be run locally; no ChoboWeb source was changed and
  the API contract is unchanged, so no regeneration is required.)
- `dotnet test Chobo.Tests -c Release --blame-hang --blame-hang-timeout 180s` - **307/307 passed**.
- `LargeMetadataResponsiveness` before and after - see table above.
- Full system suite - running as `all-sys-20260820-01` (owner requirement: execute ALL system
  tests). Note `LargeMetadataResponsiveness`, `LargeOnTimeBackupGc`, and `FailingBasicTest` carry
  `ExcludeFromRunAll = $true`; the first two are run explicitly, and `FailingBasicTest` is
  intentionally-failing infrastructure and is excluded by design.
- Independent code review of the diff - in progress.


## Code review outcome and the statistics discovery (2026-08-20)

An independent code review of the diff raised four HIGH findings. All were fixed.

| # | Finding | Fix |
| --- | --- | --- |
| 1 | Dropping indexes that migration `000000000001` still creates makes **rollback silently lossy** - a downgrade to v1.1.3 never recreates them, because that migration is already in `__EFMigrationsHistory` and will not re-run. The old binary would then run permanently without them. | Change made **purely additive**: all four original indexes retained, four new covering ones added. Net +4 indexes; slightly more write amplification per shard insert, accepted as the price of staying on a patch release. |
| 2 | Bare `ANALYZE` at startup is a **write**, ran unguarded, and raw SQL bypasses the retry path - a momentarily locked database would have failed server startup. | `PRAGMA optimize` in try/catch with timing logs. Stale statistics are never fatal. |
| 3 | Retrying `SaveChanges` under a caller-managed transaction can re-apply partial work. Five `BeginTransactionAsync` sites exist. | Guarded with `Database.CurrentTransaction is null`; `SQLITE_BUSY_SNAPSHOT` (517) excluded as non-retryable in place. |
| 4 | **The issue-#2 fix could have caused the stall it was meant to prevent.** Classifying SQLite locks as transient routes the shard into a `catch` block that then performs four more writes to the same locked database. If the lock was still held, that threw *from inside the catch*, escaped `RunShardAsync`, and left the shard `Running` with the queue claim never released - the exact outcome `AGENTS.md` forbids. | Bookkeeping in both runners' catch blocks wrapped so the worker always reaches a terminal result. |

Also: `SqliteBusyRetryCount` reduced 5 -> 3 (the DB-layer and shard-layer retries were compounding
to ~24 minutes per shard); dead `Include(x => x.SourceCluster)` removed from the `includeTables`
list path; `RetryDelay` clamped against negative input; the contention test strengthened after the
reviewer showed it passed even with retries disabled.

### Additional fix found while tracing the failure path

`ReloadShardAndStopIfCanceledAsync` treated only `Skipped` and `Failed` as terminal, omitting
`Succeeded` - even though the `IsShardTerminalStatus` helper directly above it includes all three.
Consequence: a shard that had persisted `Succeeded` and then hit a lock on its **audit** write was
rolled back to `Queued` and re-run. Self-healing but pointless. Now uses the existing helper.

### The statistics discovery - root cause of an apparent regression

After the additive revert, the list endpoint measured **1558 ms** standalone, not the 462 ms seen
with the drop-based build. A controlled experiment (perf environment kept alive, ChoboServer
restarted so `PRAGMA optimize` ran with the 720k rows present) isolated it:

| statistics state | list endpoint |
| --- | --- |
| gathered at boot, on an empty database | 1610 / 1368 / 1403 ms |
| refreshed with the data present | **487 / 316 / 349 ms** |

The additive index layout was never the problem. **The startup-only placement of the statistics
refresh was.** With no `sqlite_stat1`, SQLite chooses between the narrow index and the covering
composite arbitrarily, and chose wrong.

This also retroactively explains the 462 ms drop-based result: not that dropping indexes is fast,
but that with no competing index the planner *could not* choose wrong. Shipping that would have
been a solution that worked only by removing the planner's options, and would have degraded again
as data distribution shifted.

**Fix:** `BackgroundServices/SqliteQueryStatisticsBackgroundService` runs
`PRAGMA analysis_limit=400; PRAGMA optimize;` on `ChoboSqliteOptions.QueryStatisticsRefreshInterval`
(default 1 h). `PRAGMA optimize` only re-analyzes tables whose distribution actually shifted, so it
is near-free on most cycles. The startup refresh is retained - correct for a real server whose data
is already present at boot.

`LargeMetadataResponsiveness` now sets the interval to 5 s and waits 10 s after seeding. Rationale,
stated plainly: the test creates 720k rows in one artificial burst via a test hook, which no real
server does. Configuring the interval down makes it measure a steady-state server, which is what
its 5000 ms thresholds were always meant to represent.

## Final measured result - identical fixture, 720k shard rows, standalone runs

| Endpoint | before | after | change |
| --- | --- | --- | --- |
| `GET backups?includeTables=false` | 3067.1 ms | **440.4 ms** | **7.0x faster** |
| `GET backups/{id}` (detail drawer) | 2995.1 ms | **111.0 ms** | **27x faster** |
| `GET backups/{id}?includeTables=false` | 138.2 ms | 47.5 ms | 2.9x faster |
| `POST backups/garbage-collector/run` | 372.3 ms | 365.7 ms | unchanged |
| `GET dashboard?nextHours=24` | 70.1 ms | 78.6 ms | unchanged (never slow) |
| `cli dashboard show` | 212.1 ms | 211.1 ms | unchanged |

Artifacts: `perf-baseline-20260820-01` (before), `perf-stats-20260820` (after). The GC and CLI
regressions noted in the earlier interim result were statistics/noise artifacts and are gone.

## Answer to "what happens on a SQLite failure now?"

1. **`SaveChanges` retries silently** - 4 attempts, each up to ~15 s at the command level, with
   jittered backoff (~61 s worst case per write). Most real lock blips end here with **no
   user-visible effect at all**.
2. **If it still fails, the shard is re-queued, not failed** - `IsTransientShardFailure` now
   returns true, so the shard returns to `Queued` with a `shard-retry-scheduled` audit record and
   retries after `TransientShardRetryDelay` (1 min), up to `TransientShardMaxRetries` (3).
3. **The retry recovers the ClickHouse backup rather than redoing it** - `ClickHouseOperationId` is
   persisted immediately after submission, so the resume path finds the operation already
   `BACKUP_CREATED`, measures the storage path, and marks the shard `Succeeded`.

Net: **full `Succeeded`** in the overwhelming case, at the cost of ~1 minute of latency.
**`PartiallySucceeded`** only if a shard exhausts all retries (a lock sustained ~4+ minutes), and
even then only that shard's table degrades - `AggregateBackupStatus` returns `Failed` only if
*every* table failed. Worst case ~7-8 minutes for one shard to exhaust everything.

### Remaining gaps (deliberately out of the agreed conservative scope)

- **Backup-level writes are still fatal.** If the final status write at `BackupRunnerService.cs:115`
  exhausts its retries, the outer catch marks the whole backup `Failed`, and that catch performs
  its own `SaveChanges` which can fail again. Much narrower window than the per-shard path, but it
  is the one place a lock can still fail an otherwise-good backup.
- **Aged-out ClickHouse operations.** If the operation record leaves `system.backups` before the
  1-minute retry, recovery raises `MissingOperationException`, which is not classified transient.

### Correction to an earlier claim in this file

An interim note asserted `Microsoft.Data.Sqlite`'s `Default Timeout` was 30 s and *overrode*
`PRAGMA busy_timeout`. Both halves were wrong: `ServiceCollectionExtensions.cs:196` derives it from
`ChoboSqliteOptions.BusyTimeout` (15 s), and it composes with the pragma rather than overriding it.
`ChoboSqliteOptions.BusyTimeout` is **not** inert. No follow-up ticket is needed there.

## Final validation evidence (2026-08-20)

| Gate | Result |
| --- | --- |
| `dotnet build Chobo.sln -c Release` | clean, 0 warnings |
| `dotnet test Chobo.Tests -c Release --blame-hang --blame-hang-timeout 180s` | **307/307 passed** |
| System suite `gate-all-20260820` (run-all set, concurrency 2) | **24/24 passed**, 931 s |
| System suite `gate-gc-20260820` (`LargeOnTimeBackupGc`, excluded from run-all) | **1/1 passed**, 915 s |
| `LargeMetadataResponsiveness` standalone (`perf-stats-20260820`) | passed; list 440 ms, drawer 111 ms |

Every system test in the repository has now been executed against the shipping build except
`FailingBasicTest`, which is intentionally-failing infrastructure used to verify failure reporting.

Note on `ExcludeFromRunAll`: it is not honoured uniformly. `LargeMetadataResponsiveness` sets it
inside `Get-ChoboTestDefinition` in a custom `Test.ps1` and **still runs** in the run-all set, while
`LargeOnTimeBackupGc` and `FailingBasicTest` set it in a declarative `TestDefinition.psd1` and are
correctly excluded. Worth a separate look; it means the run-all set silently includes a 110 s
seeding perf test.

### ChoboWeb verification (completed after Node was installed)

- `npm run typecheck` - **clean**. Confirms the API contract is unchanged and
  `src/api/generated.ts` needs no regeneration.
- `npm test` - **48/48 passed, 11 files**.
- `dotnet build Chobo.sln` (Debug, which runs `npm run build`) - **succeeds**, 0 warnings.

The first `npm test` run surfaced one failure in
`src/pages/restores/RestoreWizard.test.tsx`, independently confirmed as **pre-existing and
unrelated** to this work (`git status` showed ChoboWeb byte-identical to HEAD; failed 3/3 runs).

Root cause: a **time bomb**, not a product bug. The fixture hardcoded
`createdAt: "2026-07-10T00:00:00Z"` while `RestoreWizard.tsx:23` defaults its filter to
`backupDateFromHours(72)`. Once wall-clock time moved past that 72h window the fixture backup was
filtered out of the list, so the `input[aria-label="Use backup backup-id"]` row never rendered.
Introduced 2026-07-10 in `79978f4`; would have started failing ~2026-07-13.

Fixed by deriving all fixture timestamps from `Date.now()` so they always fall inside the default
window. Also corrected an inconsistency where `startedAt` (2026-07-01) preceded `createdAt`
(2026-07-10) by nine days.

A separate contributing gap was ruled out, not fixed: `vitest.config.ts` has no setup file and
never sets `IS_REACT_ACT_ENVIRONMENT`, producing "not configured to support act(...)" warnings.
Verified with a throwaway config that setting it does **not** fix the failure, so it is unrelated -
but it is still worth addressing.

### Not verified

- `scripts/Test-UpgradeSamples.ps1` was not run. It is the gate for schema changes, and this change
  makes none - `ChoboApi.SchemaVersion` remains 2.
- **CI does not run the ChoboWeb suite at all.** Grepping `.github/workflows/*.yml` for `npm test`,
  `vitest`, or `typecheck` returns nothing, which is why the above test could rot for five weeks
  unnoticed. Adding both to CI is recommended and **not** done here.
- `scripts/Test-UpgradeSamples.ps1` was not run. It is the gate for schema changes, and this change
  makes none - `ChoboApi.SchemaVersion` remains 2.

### Release positioning

Patch on the 1.1.x line (**1.1.4**). No `SchemaVersion`, `ExportVersion`, or `ApiVersion` change.
Downgrade to v1.1.3 remains safe: the index change is additive, so no index the old binary depends
on has been removed.
