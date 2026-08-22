# SQLite Performance Under GUI Load

Status: **4 measurement rounds; schema v3 index consolidation landed; `/data/export` still open**
Created: 2026-08-21
Branch: `master` (uncommitted working tree)

Follows on from `gc-dependency-query-performance.md` and `backup-list-query-performance.md`, which
each fixed one reported query. This workstream inverts the approach: instead of waiting for a slow
query to be reported from production, replay the entire GUI API surface against a loaded server with
the slow-query threshold lowered, and treat any query above the gate as a defect.

## Owner's requirement

> execute the system with simulated load, and with lowering the threshold for reporting long/slow
> sqlite query. then, check there are no sqlite query reported. use the same api call gui browsing
> use. check all flows. ENSURE WE HAVE 0 PERF PROBLEMS WITH SQLITE.
> Do not optimize blindly. after each optimization run CR-loop, and if needed also
> design-review-loop. When finished, run again the whole testing suite.

## Existing infrastructure (discovered, not built)

`ChoboServer/Data/SlowSqliteQueryLoggingInterceptor.cs` already exists and already does the job:

- Hooks all six EF `*Executed*` callbacks.
- Threshold from `Chobo:DatabaseLogging:SlowQueryThreshold`, default 2 s, read per command from
  `IOptionsMonitor` so it is **live-reloadable** without a restart.
- `threshold < 0` disables; `threshold == 0` logs every measurable command.
- Emits at Information with `SourceContext = ChoboServer.Data.SlowSqliteQueryLoggingInterceptor`,
  message `Slow SQLite query completed in {ElapsedMilliseconds} ms, ... CommandText={CommandText}`.
- Settable at runtime via `PUT /api/v1/settings/{key}` (`RuntimeSettingApplyMode.Live`).

**System tests already run at a 100 ms threshold** by default
(`TestingSuite/Infra/ComposeGenerator.psm1:509-511`), and **nothing has ever asserted on it**.

Baseline measured this session: **0 slow-query entries across all 24 functional system tests** at
100 ms. That is not reassuring — those tests carry almost no data. Hence the load fixture.

## Blind spots found in the detector itself (fixed)

The interceptor is an *EF* interceptor. Three read paths build commands directly off a
`SqliteConnection`, so EF's pipeline never saw them:

| Path | File | Status |
| --- | --- | --- |
| `/api/v1/audit` read + count + retention delete | `Repositories/AuditStore.cs` | **instrumented** |
| `/api/v1/logs` read + count + retention delete | `Repositories/ApplicationLogStore.cs` | **instrumented** |
| Serilog sink writes to `chobo-logs.db` | `Services/ApplicationLogSqliteSink.cs` | not instrumented (see open items) |
| SQLite self-backup | `BackgroundServices/SqliteSelfBackupBackgroundService.cs` | not instrumented, disabled by default |
| Bootstrap/pragma pre-checks | `Data/DatabasePerformanceMaintenance.cs`, `Services/DatabaseBootstrap.cs` | not instrumented, startup-only |

This mattered: `AuditEntries` and `ApplicationLogEntries` are the two tables that grow without
bound, and the GUI polls both at `limit=10000` every 3 s from RestoreDetail. A "0 slow queries"
result while those were unmeasured would have been a false guarantee.

Both now call `SlowSqliteQueryLoggingInterceptor.LogIfSlow` directly (it was already `public`), so
they share one threshold and one log format rather than growing a parallel mechanism.

**Deliberate semantic difference:** the raw-path timings span row *materialisation*, not just
`ExecuteReaderAsync`. EF's interceptor measures execution only. For a paged read over a growing
table the cost of a wide `OFFSET` lands while stepping rows, so measuring execution alone would
under-report exactly the case that matters.

## Fixture fidelity fix

`TestHooksController.SeedLargeMetadataGraph` set `ParentFullBackupId` but never
`ParentFullBackupTableId` / `ParentFullBackupTableShardId`, so every query that joins through the
table- or shard-level parent chain matched nothing — realistic row counts, unrealistic join
selectivity. Carried over as an open item from `gc-dependency-query-performance.md`.

Changed:
- Table names are now per **chain** (`table_{chainIndex}_{tableIndex}`) rather than per backup, so an
  incremental backs up the same table names its parent full did. This is both more realistic and
  what makes the parent links resolvable at all.
- Incremental tables link to the parent full's table row; incremental shards link to the parent
  full's shard row for the same shard number.
- Response and log line now report `parentTableLinkCount` / `parentShardLinkCount`, and the harness
  fails if either is zero — so this fixture can never silently regress to not exercising the chain.

## Harness

`TestingSuite/Tests/SqlitePerformanceUnderLoad/Test.ps1` (scripted test, `ExcludeFromRunAll`).

Scale: 300 backups / 30,000 tables / 720,000 shards / 20 restores / 1,000 queue rows — same as
`LargeMetadataResponsiveness`.

Sequence:
1. Container starts with the threshold parked at `00:05:00` so the seed transaction stays silent.
2. Seed, assert size **and** parent-link counts.
3. Sleep past `QueryStatisticsRefreshInterval` so `PRAGMA optimize` has run.
4. **Diagnostic pass** at `00:00:00.001` — replays every GUI flow, captures the whole tail so the
   fixes that follow are chosen from data rather than from reading code.
5. **Gate pass** at the gate value — replays the same flows; any entry is a failure.
6. Threshold is raised back to quiet *before* reading results, so the log read cannot append to the
   window it reports on.

Results land in `sqlite-performance.json` and a ranked `sqlite-performance.txt`.

Coverage: every GUI-reachable endpoint from the API inventory, with the query parameters the GUI
actually sends (`includeTables=false` on list, `includeTables=true` only on the drawer, 14-day
backups window, 7-day schema window, `limit=10000` on RestoreDetail logs/audit, both exports).

Known coverage gaps, deliberate:
- Endpoints that require a live ClickHouse (`clusters/{id}/topology`,
  `clusters/{id}/clickhouse-cluster-names`, `policies/simulate`, `restores/plan`) are not replayed;
  the seeded cluster hosts do not exist. They are not SQLite-bound.
- Replay is serial. The GUI Refresh button fires ~8 concurrent requests; SQLite reader contention
  under that burst is not yet exercised.
- All seeded operations are `Succeeded`, so the 3 s polling loops that only engage during active
  work are replayed as individual calls rather than as sustained concurrent polling.

## Owner constraints on fixes (2026-08-21)

1. **Prefer non-invasive changes.** Adding an index is fine. Adding a nullable column is fine.
   Reshaping a table is a last resort, only when there is no other option.
   - Consequence: the long-standing structural gap - `BackupTableShards` carries no `BackupId`, so
     every shard-to-backup question joins back through `BackupTables` (flagged in both
     `backup-list-query-performance.md` and `gc-dependency-query-performance.md`) - is explicitly
     *not* the opening move. Try query rewrites and indexes first; a denormalised nullable
     `BackupId` is the fallback if nothing else reaches the gate.
2. **Index consolidation round.** After any index is added, run a dedicated review asking:
   is this index a prefix of one that already exists? Do several narrow indexes collapse into one
   broader index that serves all their queries? SQLite uses at most one index per table per query,
   so over-specific indexes cost write throughput and disk while buying nothing.

Both are gates, alongside the per-optimization CR-loop and (where the fix is structural rather than
local) a design-review loop.

## Baseline from `LargeMetadataResponsiveness` (same 720k-shard scale)

Passed with the seed change in place. Its budgets are loose enough to hide real problems:

| Endpoint | Elapsed | Budget |
| --- | --- | --- |
| `GET /backups?includeTables=false` | 514 ms | 5000 ms |
| `GET /backups/{id}` detailed (2.5 MB) | 111 ms | 5000 ms |
| `GET /dashboard` | 81 ms | 5000 ms |
| `GET /logs?last=50` | 20 ms | 3000 ms |
| seed | 176.7 s | 240 s |

`GET /metrics` - the unbounded `BackupTableShards` scan - is **not covered by that test at all**.

## Findings (run `sqlite-perf-1`, 2026-08-21, 720k shards)

Gate pass at 100 ms reported **8 breaching queries**. Diagnostic pass captured 20 above 1 ms.

**Caveat on absolute numbers:** this run shared the machine with an unrelated Docker workload
(`pusherllm-pa-*`) while host memory sat at 89%. Rankings are trustworthy; absolute milliseconds are
inflated. Any gate value must be re-validated on a quiet host before being committed as a threshold.

### Endpoint wall clock (gate pass)

| Endpoint | Wall clock | Response bytes |
| --- | --- | --- |
| `GET /data/export` | **9,989 ms** | **712,254,980** |
| `GET /metrics` | **3,412 ms** | **27,799,833** |
| `GET /backups/garbage-collector/queue` | **2,174 ms** | **2** |
| `GET /backups?includeTables=false` | 328 ms | 279,222 |
| `GET /backups?from=...&includeTables=false` | 313 ms | 279,222 |
| `GET /schema/backups/{id}/export` | 137 ms | 17,426 |
| `GET /schema/backups/{id}` | 129 ms | 58,408 |
| `GET /backups/{id}/garbage-collection-evaluation` | 115 ms | 2,020 |
| `GET /restores` | 98 ms | 2,950,682 |

Everything else was under 70 ms; most of the GUI surface is under 3 ms.

### F1. Orphan-incremental scan - 2,127 ms to return nothing  **[FIXED]**

`BackupsGarbageCollectorBackgroundService.FindOrphanIncrementalBackupIdsAsync`. Scanned every
incremental shard (~648k rows) running a correlated `NOT EXISTS` against `Backups` per row, then
joined back to `BackupTables`. Reached by the background GC sweep **and** by
`GET /backups/garbage-collector/queue`, which the GUI polls every 5 s whenever the GC queue is
non-empty. It cost 2.1 s to produce a **2-byte** empty response.

Rewritten to resolve dead *parents* once and then seek the rows pointing at them:
1. `SELECT DISTINCT ParentFullBackupId` - one row per full backup; SQLite answers it with a
   skip-ahead scan of the covering index (`SEARCH ... USING COVERING INDEX
   IX_BackupTableShards_ParentFullBackupId (ParentFullBackupId>?)`).
2. Subtract the live ones. A parent row that vanished entirely still counts as orphaning, so this
   subtracts from the referenced set rather than only reading deleted statuses.
3. Seek the rows referencing dead parents - skipped entirely when nothing is deleted.
4. Parentless incremental rows queried separately, never with an `OR`: one `OR` spanning two
   different columns stops SQLite using an index for either side.

Measured on a synthetic DB with the **exact shipped index set** at the same 720k scale:
**225.6 ms -> ~9.5 ms in-memory** (the container measured 2,127 ms for the old form).
**No schema change and no new index** - 9.5 ms is already inside any sane gate, so adding a
`(EffectiveBackupType, ParentFullBackupId, BackupTableId)` index would have been an over-specific
index bought for a marginal gain.

Equivalence argument: old = (A dead-parent tables) union (D parentless tables with no live
shard-parent) union (B dead-parent shards) union (C parentless incremental shards). New computes the
same four sets. Every non-null parent id is in the referenced set by construction, so no row falls
between the branches.

### F2. `GET /metrics` - 3,412 ms, 27.8 MB  **[NOT YET FIXED]**

`DashboardApplicationService.GetTableShardMetricRowsAsync` materialises **every** `BackupTableShards`
row joined to tables and backups - no filter, no date bound, no `Take` - then applies the status
filter and four conditional `MAX`es client-side. All of it is expressible as one SQL `GROUP BY` with
conditional aggregates, cutting transfer from 720k rows to ~72k groups.

Beyond query cost there is a design problem: the endpoint emits up to four metrics per
`(database, table, shard)`, roughly 288k metric keys / 27.8 MB at this scale. No query optimisation
fixes that; it is a Prometheus cardinality question and needs a design-review loop.

### F3. `GET /data/export` - 9,989 ms, **712 MB** response  **[NOT YET FIXED]**

`ExportImportService.ExportAsync` issues seven unfiltered `ToListAsync()` over the largest tables.
This is the Import/Export page's "Download data" button. A 712 MB JSON body is not usable by a
browser regardless of how fast the queries are - design-level, not just query-level.

### F4. Slow-query entries are logged twice  **[NOT YET FIXED]**

Every entry in the report appears exactly twice with identical durations to 0.1 ms - one execution
logged twice, not two executions. `AddDbContext<ChoboDbContext>` and
`AddDbContextFactory<ChoboDbContext>` are both registered and both call `AddInterceptors`, so the
interceptor lands on the resolved options twice. Doubles slow-query log volume.

### F5. Five pre-existing redundant indexes  **[NOT YET FIXED]**

Found while doing the index-consolidation round the owner asked for. Verified with
`EXPLAIN QUERY PLAN` on a populated synthetic DB, not just by the prefix rule:

| Redundant index | Covered by | Evidence |
| --- | --- | --- |
| `IX_BackupTableShards_BackupTableId` | `(BackupTableId, SourceShardNumber)`, `(BackupTableId, ParentFullBackupId)` | planner never chose it even when present |
| `IX_AuditEntries_Timestamp` | `(Timestamp, Id)` | after dropping, planner uses `(Timestamp, Id)`, which also satisfies the `Id DESC` tiebreak `AuditStore` issues |
| `IX_AuditEntries_OperationId_Timestamp` | `(OperationId, Timestamp, Id)` | same - surviving index covers the full ordering |
| `IX_BackupTables_BackupId` | `(BackupId, ParentFullBackupId, BackupSizeBytes)` | strict prefix |
| `IX_BackupTableShards_Encrypted_BackupTableId` | `IX_BackupTableShards_Encrypted_BackupTableId_KeyId` | strict prefix, identical partial filter |

Five indexes maintained on every insert - across 720k shard rows - for zero read benefit, and in the
two audit cases the surviving index is a *better* match for the real query. Dropping them is a
schema change, so it must go through the `chobo-schema-versioning` skill.

### F6. `EffectiveBackupType` composite indexes may be unusable

`IX_BackupTables_EffectiveBackupType_ParentFullBackupTableId` and
`IX_BackupTableShards_EffectiveBackupType_ParentFullBackupTableShardId` lead with a two-value
column. Standalone indexes on both trailing columns also exist, so the composites only earn their
keep where a query constrains `EffectiveBackupType` *and* the parent column. Not yet audited against
the real query set - candidate for the same consolidation round as F5.

## Round 2 (run `sqlite-perf-2`) - results, including two of my own mistakes

Re-measured after the F1 orphan rewrite and the F2 metrics change, with
`Chobo__Metrics__IncludeTableShardMetrics=true` forced on so the harness measures the heaviest
supported configuration rather than the cheap default.

| Endpoint | Round 1 | Round 2 | |
| --- | --- | --- | --- |
| `data export` | 9,989 ms | 9,640 ms | unchanged |
| `monitoring: metrics` | 3,412 ms | **9,196 ms** | **regressed - my change** |
| `gc: queue` | 2,174 ms | 1,399 ms | improved, far less than predicted |

### R2-A. The metrics `GroupBy` was a 2.7x regression - REVERTED

EF does not translate a `GroupBy` carrying several conditional aggregates into one grouped scan. It
emits **one correlated scalar subquery per aggregate**:

```sql
SELECT b0.Database, b0.Table, b.SourceShardNumber,
  (SELECT MAX(CASE WHEN b2.EffectiveBackupType = 0 THEN COALESCE(...) END) FROM ...),
  ... three more ...
```

So each group is re-scanned four times. Reverted to client-side grouping; the SQL-side **status
filter was kept**, since that genuinely avoids transferring rows for incomplete backups.
A single-pass SQL `GROUP BY` would still beat both, but it needs hand-written SQL rather than LINQ.
**Follow-up, not done.**

The real win for `/metrics` is the opt-in gate: with `IncludeTableShardMetrics` off (the default) the
endpoint issues no `BackupTableShards` query at all.

### R2-B. My orphan prediction was wrong: ~9.5 ms predicted, 1,399 ms measured

The synthetic benchmark selected `BackupTableId`, which the
`(ParentFullBackupId, BackupTableId)` index covers. The real query selects `BackupTable.BackupId`,
which forces a join to `BackupTables` for every row in the NULL-parent range - about 72,000
full-backup shards, all of which then fail an `EffectiveBackupType` filter that is not in the index.
**I benchmarked a query that was not the one that runs.** Lesson: benchmark the generated SQL, not a
hand-written approximation of it.

Remaining fix, which *reduces* index count rather than adding one:
`IX_BackupTableShards_ParentFullBackupId` and `IX_BackupTableShards_ParentFullBackupId_BackupTableId`
both exist today. Replacing both with a single
`(ParentFullBackupId, EffectiveBackupType, BackupTableId)` makes the parentless seek fully covering
and leaves one fewer index on the write path. Schema change - must go through
`chobo-schema-versioning`. **Not done.**

### R2-C. Two defects in my own orphan rewrite, found by adversarial review - FIXED

Both were introduced by the first version of the rewrite; neither existed in the original code.

1. **Data loss.** The first version read `liveParentIds` into memory and tested shard rows against
   that snapshot. The snapshot only contains parents *already referenced when the pass began*, so a
   full backup with no children yet was absent from it. `BackupPreparationService` commits an
   incremental with `ParentFullBackupId = null` on the table while the shards carry it
   (`BackupPreparationService.cs:186,192,236`), so an incremental committed mid-pass made the
   parentless-table branch see no live parent and flag a **healthy new backup** as an orphan, which
   `MarkOrphanIncrementalBackupsAsync` then queues for deletion. The original was immune because its
   liveness check sat in the same statement as the shard read.
   **Fix:** the parentless-table branch is back to a single correlated statement, and there is no
   client-side live set anywhere.

2. **Permanent GC failure.** EF Core 10 does **not** use `json_each` for a captured-collection
   `Contains`; it emits one bind parameter per element. Verified to throw
   `SQLite Error 1: 'too many SQL variables'` at 32,766. `referencedParentIds` grows monotonically
   with retained history, so past that point every GC pass would throw forever and the GC queue
   endpoint would return 500. **This assumption is recorded below as an open question - the answer
   turned out to be the bad one.**
   **Fix:** the dead-parent set is now computed entirely in SQL
   (`.Distinct().Where(parentId => !db.Backups.Any(...))`), and the one remaining captured-collection
   `Contains`, over *dead* parents only, is chunked at 500.

The same review confirmed set-equivalence with the original across 2,900 randomised database states,
and confirmed the skip-ahead covering-index scan is real
(`SEARCH ... USING COVERING INDEX IX_BackupTableShards_ParentFullBackupId (ParentFullBackupId>?)`).

## Round 3 - second adversarial review of the orphan fixes

Both original defects confirmed fixed: the data-loss interleaving no longer reproduces, the
40,000-dead-parent case succeeds, set-equivalence holds across 2,900 randomised states, and dangling
parents are proven correct (the SQL uses `NOT EXISTS`, not `NOT IN`, so a missing row is not
NULL-poisoned). Whole-method warm at 80k shards: **NEW 41.8 ms vs OLD 441.0 ms** in steady state -
the state on every GC pass and every `/backups/garbage-collector/queue` poll.

Three further issues came out of it.

### R3-A. Chunk size 500 sat on SQLite's index/scan crossover - FIXED (now 100)

Measured on 80,000 shards, varying only the IN-list size on the chunk query:

| IN-list | Time | Plan |
| --- | --- | --- |
| 1 | 0.1 ms | `SEARCH ... IX_BackupTableShards_ParentFullBackupId (ParentFullBackupId=?)` |
| 100 | 22.3 ms | index seek |
| 200 | 84.0 ms | index seek |
| **500** | 91.6 ms | **`SEARCH ... IX_BackupTableShards_EffectiveBackupType_... (EffectiveBackupType=?)` - a scan** |

At 500 SQLite abandons the parent index, so total cost becomes `chunks x full-scan` instead of
`ids x log(rows)`. That made the 40,000-parent case **1,785 ms for the new code vs 286 ms for the
old** - the one shape where the rewrite was *slower* than what it replaced. Chunk size is now 100,
which keeps the seek and is still ~300x below the variable limit.

Note the plan also flips to a scan at IN-list size 10 when `sqlite_stat1` is absent - i.e. on a
database that has not been analysed yet. `PRAGMA optimize` runs at startup and periodically, so this
only affects a fresh install before the first refresh.

### R3-B. Residual staleness in the chunk queries - FIXED

The chunk queries tested freshly-read rows against `orphanParentIds` captured two statements earlier.
If a parent *leaves* `DeletedStatuses` mid-pass, its children were flagged for deletion. This is
reachable: `BackupRunnerService.cs:109` and `:136` overwrite `backup.Status` unconditionally, so a
full backup marked `ManualDeleteRequested` or `BackupExpiredDeleteStarted` while its run was still
going reverts to `Succeeded`/`PartiallySucceeded`/`Failed` when the run finishes.

Narrower than the original defect (1-2 statement window, and it needs a status resurrection rather
than the routine "first incremental after a new full"), but the same class and the same outcome:
deletion of a live backup.

**Fix:** the chunk list now only *narrows the seek*; liveness is re-checked inside the same statement
that reads the rows. The opposite direction - a new incremental committed against an
already-dead, previously childless parent - is a false negative that self-corrects on the next pass,
and is left alone.

### R3-C. Unbounded `Contains` at both consumers - NOT FIXED, pre-existing

`FindOrphanIncrementalBackupIdsAsync` returns a `List<Guid>` that both consumers feed into an
unchunked `Contains`: `MarkOrphanIncrementalBackupsAsync` (`:381`) and `BuildQueueItemsAsync`
(`:434`). Verified to throw:

```
FindOrphanIncrementalBackupIdsAsync returned 40000 ids
*** consumer query THREW: SqliteException: SQLite Error 1: 'too many SQL variables'
```

Triggering state: one deleted full backup with more than ~32,766 incremental children - a mass
deletion, not decades of history. Once hit, every GC pass throws and the GC queue endpoint returns
500 permanently, so storage is never reclaimed.

**Pre-existing** - the previous implementation returned the same list - so not a regression, but the
"variable limit is gone" claim holds only for the parent set inside the method. Spun off as a
separate task.

## Round 4 - schema v3 index consolidation

Owner decision: take the full consolidation and accept a **1.2.0** minor release rather than add a
third index leading with `ParentFullBackupId` as a 1.1.5 patch.

`ChoboApi.SchemaVersion` 2 -> 3, migration `000000000003_IndexConsolidation`. **Net -5 indexes:**

| Action | Index |
| --- | --- |
| dropped | `IX_BackupTableShards_ParentFullBackupId` |
| dropped | `IX_BackupTableShards_ParentFullBackupId_BackupTableId` |
| **added** | `IX_BackupTableShards_ParentFullBackupId_EffectiveBackupType_BackupTableId` |
| dropped | `IX_BackupTableShards_BackupTableId` |
| dropped | `IX_BackupTables_BackupId` |
| dropped | `IX_AuditEntries_Timestamp` |
| dropped | `IX_AuditEntries_OperationId_Timestamp` |

`EffectiveBackupType` sits in the middle of the new index deliberately: it turns the garbage
collector's "incremental shards with no parent" lookup into a covering seek, instead of a seek over
the whole NULL range followed by a row lookup per entry just to test the backup type. That NULL range
holds every full backup's shards - 72,000 rows at fixture scale - which is where the time went.

### Result

| Endpoint | R1 | R2 | R3 | **R4** |
| --- | --- | --- | --- | --- |
| `gc: queue` | 2,174 ms | 1,399 ms | 1,518 ms | **310 ms** |
| `monitoring: metrics` (opt-in on) | 3,412 ms | 9,196 ms | 3,313 ms | 3,101 ms |
| `data export` | 9,989 ms | 9,640 ms | 9,167 ms | 9,318 ms |
| queries over the 100 ms gate | 8 | 12 | 12 | **8** |

`gc: queue` is **7x faster** than where it started, and it is polled every 5 s by the GUI whenever the
GC queue is non-empty.

### Migration gotcha worth remembering

`MigrationBuilder.DropIndex` fails with `no such index` on a **fresh** database, because several of
these indexes are created by `DatabasePerformanceMaintenance`, which runs *after* `MigrateAsync`.
Which of them exist therefore depends on whether the database is fresh or upgraded. The migration
uses raw `DROP INDEX IF EXISTS` / `CREATE INDEX IF NOT EXISTS`, which is correct for both. A unit
test asserts the migration is index-only and that every `DROP` is conditional.

### Upgrade coverage

AGENTS.md notes no unit test previously executed an EF migration. Added:
- `Version_two_database_schema_is_upgraded_to_version_three`
- `Version_one_database_schema_is_carried_through_every_intermediate_version` - the upgrade arms now
  chain, so a v1 database steps through v2 to v3 rather than being rejected for lacking a direct arm
- `Ef_schema_v3_migration_only_touches_indexes`

`.\scripts\Test-UpgradeSamples.ps1 -Version 1.2.0` **passes** against the real published 1.1.4
sample: config import and data import both succeed on the upgraded database.

## Remaining, not fixed

| # | Item | Cost | Why not done |
| --- | --- | --- | --- |
| F3 | `GET /data/export` | 9,318 ms, **712 MB** | Seven unfiltered `ToListAsync`. A 712 MB JSON body is unusable by a browser however fast the queries are - needs a design decision (streaming, pagination, or a job-based export), not a query fix. |
| F4 | Slow-query entries logged twice | - | `AddDbContext` and `AddDbContextFactory` are both registered for `ChoboDbContext` and both call `AddInterceptors`. Doubles slow-query log volume. |
| R3-C | Unbounded `Contains` at the two orphan consumers | - | Pre-existing; throws above 32,766 ids. Spun off as its own task. |
| F6 | `EffectiveBackupType`-leading composites | - | Not audited against the real query set. |
| - | `/metrics` single-pass SQL aggregation | 3,101 ms when opted in | Needs hand-written SQL; LINQ `GroupBy` was measured *worse*. Default path is already free. |
| - | Gate value | - | Still 100 ms, chosen arbitrarily. Should be set from measured data on a **quiet** machine; every run here shared the host with an unrelated Docker workload. |

## Open question: parameter-list translation - ANSWERED, BADLY



The rewrite passes id sets into EF `Contains`. I assumed EF Core 10 would translate these via
`json_each` rather than one parameter per element. **It does not** - it emits one bind parameter per
element, and SQLite throws `too many SQL variables` at 32,766. See R2-C above. Any future change that
puts a runtime-sized collection into an EF `Contains` must either keep the set bounded by
construction or chunk it.

## Validation status

| Gate | Status |
| --- | --- |
| `dotnet build Chobo.sln -c Release` | **PASS** - 0 warnings |
| Full unit suite | **PASS** - 323/323 |
| `SqlitePerformanceUnderLoad` | **PASS** - three final 720k-shard runs, zero queries over 100 ms |
| Full system suite | **PASS** - 25/25 in the release workflow configuration |

## Open items

- `ApplicationLogSqliteSink` writes one row per log event on its own connection, synchronously,
  opening and closing per event. Uninstrumented, and it is on the path of every log line the server
  emits. Instrumenting it naively would recurse (logging a slow write emits a log line); it needs a
  guard, so it was left alone.
- The slow-query log emits full untruncated `CommandText` with no length cap, unlike ClickHouse SQL
  which goes through `SqlLogRedactor.Preview` (400-char cap). A large generated `IN (...)` goes
  verbatim into `chobo-logs.db`, the console, and the file sink.
- `SlowSqliteQueryLoggingInterceptor` hooks only `*Executed*`, not `CommandFailed`. A query that
  blocks and then throws `SQLITE_BUSY` is never reported as slow.

## 1.2.0 release-gate follow-up (2026-08-22)

The first `v1.2.0` workflow candidate failed only `SqlitePerformanceUnderLoad`; publish was skipped,
and no GitHub Release or Docker tags were created. A local replay reproduced the failure at the same
720,000-shard scale. The release fix must preserve the feature's intent: keep the full GUI request
inventory, keep the 100 ms per-query gate, and require zero raw slow-query entries.

Confirmed causes and bounded fixes:

- Configure the context/interceptors once. `AddDbContextFactory` already makes the context itself
  available as scoped; the extra `AddDbContext` registration attaches the same singleton slow-query
  interceptor twice and duplicates every event.
- Make the load fixture deterministic by awaiting one statistics refresh immediately after seeding,
  while the quiet five-minute threshold is active, and park the periodic refresh for 12 hours. This
  retains maintained SQLite statistics without allowing `PRAGMA optimize` to collide with the gate.
- Preserve garbage-collection semantics while replacing large correlated queries with indexed,
  bounded lookups: discover dependent backup ids from both table and shard parent links, including
  legacy exact-row-only links, and resolve distinct orphan parent candidates in chunks.
- Project only the backup header and schema-bearing table fields in the schema browser instead of
  tracking the complete backup/table/schema graph.
- Do not change the already-indexed dashboard policy aggregate unless it remains slow in a
  collision-free replay.

Acceptance: build and unit tests pass, then two consecutive `SqlitePerformanceUnderLoad` runs pass
with zero raw entries above 100 ms before regenerating the release sample and retagging the
unpublished candidate.

### Final release evidence

- Release build: passed with 0 warnings and 0 errors.
- Unit suite: 323/323 passed with a 30-second hang dump threshold.
- `SqlitePerformanceUnderLoad`: two consecutive final standalone runs passed at 720,000 shards with
  zero raw entries above 100 ms; the exact release-workflow full suite supplied a third pass.
- Final full system suite: 25/25 passed serially with the release workflow's 120-second per-test
  timeout. This includes retention cleanup, sharded cleanup, multiple full parents, large metadata,
  1,000-table schema-only, SQLite performance, and SQLite self-backup.
- Upgrade sample: the real published 1.1.4 database upgraded to schema v3; config import and full
  data import both passed.
- The load, request inventory, and threshold were not reduced. No query was excluded from logging.

The unpublished first `v1.2.0` tag may now be replaced with the corrected commit. The failed workflow
created no GitHub Release and no Docker tags, so replacing that tag cannot overwrite published
artifacts.
