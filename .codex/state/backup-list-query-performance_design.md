# Backup List Query Performance — Low-Level Design

**Feature:** denormalize `BackupId` onto `BackupTableShards` (schema v3)
**State file:** `C:\Projects\Chobo\.codex\state\backup-list-query-performance.md`
**Status:** design complete, not reviewed
**Hard constraint:** zero behavioural change. Every query result and API response byte-identical for any fixed database state.

## 0. Verification of state-file claims

Every claim was re-checked against source. Results:

| State-file claim | Verdict | Evidence |
| --- | --- | --- |
| Latest published tag `v1.1.3`; `SchemaVersion = 2` is published | **Correct** | `git tag --sort=-creatordate` → `v1.1.3`; `Chobo.Contracts/ChoboApi.cs:11` `SchemaVersion = 2`; `.release/db-samples/1.1.3/sample-manifest.json` `"schemaVersion": 2` |
| Baseline must not be edited; new migration required | **Correct** | `.codex/skills/chobo-schema-versioning/SKILL.md` step 4 |
| Next release is `1.2.0` | **Correct** | skill "Rules Of Thumb"; `Test-UpgradeSamples.ps1` `Get-LatestPriorMinorSample` will select `1.1.3` for target `1.2.0` |
| `ExportVersion` stays at 1 | **Correct** — see §7 for the proof | `ExportImportService.cs:77` uses `!=` (exact match), so bumping would break import of every published sample |
| "SQLite cannot `ADD COLUMN NOT NULL` without a constant default" | **Misleading as written.** SQLite *can* add a `NOT NULL` column when a non-null constant default is supplied. The repo already does this: `000000000002_PasswordProtectedBackups.cs:14` adds `PasswordMode INTEGER NOT NULL DEFAULT 0`. The design therefore uses `NOT NULL DEFAULT '<empty guid>'`, **not** the nullable-DDL/non-nullable-model split the state file proposes. See §1.4. |
| "Cascade delete already works via `Backups -> BackupTables -> BackupTableShards`" | **Right conclusion, wrong mechanism.** `PRAGMA foreign_keys` is never set anywhere in the repo (verified by grep across `*.cs`/`*.json`), so SQLite FK enforcement is **off** and the `ON DELETE CASCADE` clauses in `000000000001_Baseline.cs:252` are inert. Cascade is enforced by EF (`ChoboDbContext.cs:171` `OnDelete(DeleteBehavior.Cascade)`) for tracked graphs, and by the explicit ordered `ExecuteDeleteAsync` chain in `DataRetentionBackgroundService.cs:158-176` for bulk purges. Conclusion stands: no FK needed. |
| `ClickHouseAdapter.cs:192,218` are transient, never persisted | **Correct.** Both are locals passed to `StartBackupShardAsync` / `StartRestoreShardAsync` and only `StoragePath`, `SourceShardNumber`, `EncryptedBackupPassword*` are read. Never added to a `DbSet`. Same for the test fakes at `Chobo.Tests/BackupRestoreExecutionTests.cs:6558,6636`. **No change needed.** |
| Write-site inventory is complete | **Incomplete.** It omits seven test-project creation sites: `Chobo.Tests/ChoboFoundationTests.cs:664,665,979,1944,2439` and `Chobo.Tests/BackupRestoreExecutionTests.cs:7116`. See §4. |
| Read-site inventory | **Over-broad and conflated.** It lists three different categories under one heading. `BackupPreparationService.cs:378` in particular is *not* a rewrite site — the query legitimately needs the join for `BackupTable.Database`, `BackupTable.Table` and `BackupTable.Backup.Status`. Several entries (`RestoreApplicationService.cs:615`, `RestoreRunnerService.cs:478,507,530`) are in-memory navigation on already-`Include`d graphs, not SQL joins. See §5. |
| "~15 assertion sites in `Chobo.Tests/BackupRestoreExecutionTests.cs`" to rewrite | **Correct count (16), wrong recommendation.** These should be **left unchanged** — they become free cross-checks that the denormalized column agrees with the join. See §8.2. |
| Existing index `IX_BackupTableShards_Encrypted_BackupTableId` becomes redundant | **Correct.** Grep confirms no query filters `BackupTableId = ? AND EncryptedBackupPassword IS NOT NULL`. Its only consumers are `BackupApplicationService.cs:292` and `:372`, both of which this design rewrites. |
| The skill file `.codex/skills/chobo-schema-versioning/SKILL.md` is stale | **Correct** — it says "Current development schema: `SchemaVersion = 1`" and "Latest published release: `v0.1.0`". Refreshing it is included as implementation step 12. |

---

## 1. Schema change

### 1.1 Column

EF entity — `ChoboServer/Data/BackupTableShardEntity.cs`, insert immediately after `Id` (line 7) so it mirrors `BackupTableEntity.BackupId` at `BackupTableEntity.cs:8`:

```csharp
public Guid BackupId { get; set; }
```

Non-nullable `Guid`. **Do not add a `BackupEntity? Backup` navigation property, and do not add a `Shards` collection to `BackupEntity`.** EF only infers a foreign key when it discovers a relationship, which requires a navigation on at least one side. `BackupTableShardEntity.ParentFullBackupId` (line 11) already proves this: it is a bare `Guid?` with no navigation and the baseline DDL (`000000000001_Baseline.cs:232-254`) generates no FK for it.

SQLite DDL added by migration 3:

```sql
ALTER TABLE BackupTableShards
  ADD COLUMN BackupId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
```

Expressed in the migration as `migrationBuilder.AddColumn<Guid>(name: "BackupId", table: "BackupTableShards", type: "TEXT", nullable: false, defaultValue: Guid.Empty)` so the EF SQLite provider generates the literal. Do **not** hand-write the sentinel string: `Microsoft.Data.Sqlite` persists `Guid` as lowercase `"D"`-format TEXT, and a case mismatch would make the sentinel probe in §3 silently miss.

### 1.2 Indexes to add

| Index | DDL | Serves (exact call sites) |
| --- | --- | --- |
| **A** `IX_BackupTableShards_BackupId_ParentFullBackupId` | `CREATE INDEX IF NOT EXISTS IX_BackupTableShards_BackupId_ParentFullBackupId ON BackupTableShards (BackupId, ParentFullBackupId);` | Primary target: `BackupApplicationService.cs:275-282` (`shardRelatedFullRows`, query #4 in the state file) — `WHERE BackupId IN (200 ids) AND ParentFullBackupId IS NOT NULL` projecting exactly `(BackupId, ParentFullBackupId)` `DISTINCT`. Both predicate columns and both projected columns are in the index → 200 index-range seeks, index-only, no table access. Same for `RelatedFullBackupIdsAsync` (`:641-646`). By prefix it also serves every bare `BackupId` equality: `BackupPreparationService.cs:260` (`CountAsync`), `BackupGarbageCollectionEvaluationService.cs:115`, `BackupsGarbageCollectorBackgroundService.cs:348` and `:507`, `DataRetentionBackgroundService.cs:118` and `:162/:169`, the new `DashboardApplicationService` grouped query (§5.6), and the Guid.Empty sentinel probe (§3). |
| **B** `IX_BackupTableShards_ParentFullBackupId_BackupId` | `CREATE INDEX IF NOT EXISTS IX_BackupTableShards_ParentFullBackupId_BackupId ON BackupTableShards (ParentFullBackupId, BackupId);` | `ChildBackupIdsByBackupIdAsync` shard leg (`BackupApplicationService.cs:661-667`, query #5) — `WHERE ParentFullBackupId IN (200 ids)` projecting `(ParentFullBackupId, BackupId)` `DISTINCT`: covering. `DependentBackupIdsAsync` (`:685-690`) — `WHERE ParentFullBackupId = ?` projecting `BackupId` `DISTINCT`: covering. `BackupsGarbageCollectorBackgroundService.cs:479-486` (`shardOrphanIds`) — needs `BackupId` per matching row. |
| **C** `IX_BackupTableShards_Encrypted_BackupId` | `CREATE INDEX IF NOT EXISTS IX_BackupTableShards_Encrypted_BackupId ON BackupTableShards (BackupId, EncryptedBackupPasswordKeyId) WHERE EncryptedBackupPassword IS NOT NULL;` | `BackupApplicationService.cs:289-295` (`passwordRows`, query #6) and `:372` (`GetAsync` key ids). Both are `WHERE BackupId IN/= … AND EncryptedBackupPassword IS NOT NULL` projecting `(BackupId, EncryptedBackupPasswordKeyId)`. Partial + covering. On a deployment with no password-protected backups the index holds zero rows and costs nothing. |

Status is deliberately **not** added to index A. The only `Status`-grouped consumer is the dashboard, which is scoped to `Status == Running` backups — typically one, ~2 400 shard rows at reported scale. A range seek on `BackupId` plus 2 400 row fetches is negligible; adding a third key column to a 480 k-row index is not worth it.

### 1.3 Indexes to drop

| Index | Why safe |
| --- | --- |
| `IX_BackupTableShards_ParentFullBackupId` (created at `000000000001_Baseline.cs:400` **and** `DatabasePerformanceMaintenance.cs:42`) | Index **B** has it as a strict leading prefix. Any plan that could use the old index can use B. Saves ~17 MB and one index maintenance write per shard insert. |
| `IX_BackupTableShards_Encrypted_BackupTableId` (created **only** at `DatabasePerformanceMaintenance.cs:43`; declared at `ChoboDbContext.cs:143-145`; **not present in any migration**) | After §5.2 and §5.3 no query filters on `BackupTableId` together with `EncryptedBackupPassword IS NOT NULL`. Verified by grep of every `EncryptedBackupPassword != null` / `is not null` site: `BackupApplicationService.cs:292,372` (rewritten), `BackupPreparationService.cs:422,456`, `BackupRestoreMapping.cs:180,186`, `BackupRunnerService.cs:837`, `BackupStorageManifestService.cs:129`, `RestoreApplicationService.cs:235`, `RequestValidators.cs:573` — all of the latter operate on in-memory objects, not `IQueryable`. |

**Critical sequencing trap:** `DatabasePerformanceMaintenance.EnsureAsync` runs on **every** startup (`ServiceCollectionExtensions.cs:221`) **after** `MigrateAsync`. If lines 42 and 43 of `DatabasePerformanceMaintenance.cs` are left in place, the very next startup resurrects both dropped indexes with `CREATE INDEX IF NOT EXISTS`. Both lines **must** be deleted in the same change.

### 1.4 Why `NOT NULL` and not the nullable-DDL split

The state file proposes "nullable in DDL, non-nullable in the EF model". Rejected:

- SQLite permits `ADD COLUMN … NOT NULL DEFAULT <constant>`; the only prohibition is a `NULL` or expression default. Repo precedent: `000000000002_PasswordProtectedBackups.cs:14`.
- Nullable DDL diverges from `EnsureCreatedAsync`, which is what the unit tests use (`BackupRestoreExecutionTests.cs:7032`, `SchemaUpgradeServiceTests.cs:122`). `EnsureCreated` builds DDL from `OnModelCreating` and would emit `BackupId TEXT NOT NULL`. Tests would then exercise a schema shape production never has.
- Nullability gives **no** write-site protection either way: EF always sends the CLR value, so a missed write site persists `Guid.Empty`, never `NULL`, regardless of the column's nullability. Write-site protection comes from §6, not from DDL.

Residual divergence: migrated databases carry a `DEFAULT '00000000-…'` clause, `EnsureCreated` databases do not. Immaterial — every `INSERT` is EF-generated and always supplies the column. Do **not** add `.HasDefaultValue(Guid.Empty)` to the model to close the gap; EF would then treat `Guid.Empty` as "unset" for insert-value generation, which is confusing and buys nothing.

### 1.5 Why no foreign key

Two independent reasons:

1. `ALTER TABLE` in SQLite cannot add a foreign key at all.
2. It would be inert anyway. `PRAGMA foreign_keys` is never issued — `SqlitePragmaConnectionInterceptor.BuildConnectionPragmaSql` (line 49) sets only `busy_timeout`, `synchronous`, `wal_autocheckpoint`, and `BuildDatabasePragmaSql` (line 55) adds only `journal_mode`. SQLite defaults FK enforcement to off, so even the baseline's existing `FK_BackupTableShards_BackupTables_BackupTableId` (`000000000001_Baseline.cs:252`) is not enforced by the engine.

Deletion integrity is already guaranteed by the two real paths:
- EF cascade for tracked graphs: `ChoboDbContext.cs:168` (`Backups → Tables`) and `:171` (`Tables → Shards`).
- The only bulk physical delete: `DataRetentionBackgroundService.cs:169-176`, which deletes `BackupTableShards` **before** `BackupTables` **before** `Backups`.

Adding `BackupId` does not create a new dangling-reference class: the value is a copy of `BackupTables.BackupId`, and the shard row is destroyed by the same paths that destroy its table.

---

## 2. Migration and versioning

### 2.1 `Chobo.Contracts/ChoboApi.cs`

```csharp
public const int SchemaVersion = 3;
```

`ExportVersion` stays `1` (§7). `ApiVersion` stays `1`. Release version becomes **1.2.0** per the skill's rule "SchemaVersion increases on the same major line ⇒ minor increases, patch resets".

### 2.2 Migration file

`ChoboServer/Data/Migrations/000000000003_ShardBackupDenormalization.cs`

Note there is **no** `ChoboDbContextModelSnapshot.cs` in this repo — migrations are hand-authored with explicit `[Migration]` attributes (confirmed: `Migrations/` contains exactly two files). Do **not** run `dotnet ef migrations add`; write the file by hand following the shape of `000000000002_PasswordProtectedBackups.cs`.

```csharp
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChoboServer.Data.Migrations;

[DbContext(typeof(ChoboDbContext))]
[Migration("000000000003_ShardBackupDenormalization")]
public sealed class ShardBackupDenormalization : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "BackupId",
            table: "BackupTableShards",
            type: "TEXT",
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.Sql("""
            UPDATE BackupTableShards
            SET BackupId = (
                SELECT t.BackupId FROM BackupTables t WHERE t.Id = BackupTableShards.BackupTableId)
            WHERE BackupId = '00000000-0000-0000-0000-000000000000'
              AND EXISTS (
                SELECT 1 FROM BackupTables t WHERE t.Id = BackupTableShards.BackupTableId);
            """);

        migrationBuilder.Sql("""
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_BackupId_ParentFullBackupId ON BackupTableShards (BackupId, ParentFullBackupId);
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_ParentFullBackupId_BackupId ON BackupTableShards (ParentFullBackupId, BackupId);
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_Encrypted_BackupId ON BackupTableShards (BackupId, EncryptedBackupPasswordKeyId) WHERE EncryptedBackupPassword IS NOT NULL;
            DROP INDEX IF EXISTS IX_BackupTableShards_ParentFullBackupId;
            DROP INDEX IF EXISTS IX_BackupTableShards_Encrypted_BackupTableId;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS IX_BackupTableShards_BackupId_ParentFullBackupId;
            DROP INDEX IF EXISTS IX_BackupTableShards_ParentFullBackupId_BackupId;
            DROP INDEX IF EXISTS IX_BackupTableShards_Encrypted_BackupId;
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_ParentFullBackupId ON BackupTableShards (ParentFullBackupId);
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_Encrypted_BackupTableId ON BackupTableShards (BackupTableId) WHERE EncryptedBackupPassword IS NOT NULL;
            """);

        migrationBuilder.DropColumn(name: "BackupId", table: "BackupTableShards");
    }
}
```

Operation ordering rationale:

1. `AddColumn` first — the sentinel default makes the statement `O(1)` metadata-only in modern SQLite.
2. Backfill **before** index creation. At this point no index references `BackupId`, so the 480 k-row `UPDATE` does not pay index maintenance. This is the single biggest cost lever in the whole migration.
3. Indexes created on the already-populated column: one sort-build each instead of 480 k incremental inserts.
4. Drops last so that if step 3 fails, the old indexes are still present when the transaction rolls back (belt and braces; the transaction makes this moot).

`Down` **must** drop the three new indexes before `DropColumn` — SQLite refuses `ALTER TABLE … DROP COLUMN` on an indexed column. `DropColumn` on SQLite requires SQLite ≥ 3.35, which `Microsoft.Data.Sqlite` bundles; precedent at `000000000002_PasswordProtectedBackups.cs:30-31`.

### 2.3 Where the index DDL lives, and why

Three DDL paths exist in this codebase:

| Path | When | Source of truth |
| --- | --- | --- |
| EF migrations | production upgrades and fresh file DBs | `Migrations/*.cs` |
| `db.Database.EnsureCreatedAsync()` | unit tests only (`BackupRestoreExecutionTests.cs:7032`, `SchemaUpgradeServiceTests.cs:122`) | `ChoboDbContext.OnModelCreating` |
| `DatabasePerformanceMaintenance.EnsureAsync` | every startup, after migrations | `DatabasePerformanceMaintenance.cs:16-44` |

The repo's *de facto* rule, inferred from the fact that `IX_BackupTableShards_Encrypted_BackupTableId` exists in `DatabasePerformanceMaintenance.cs:43` and `ChoboDbContext.cs:143` but in **no** migration: index-only changes are shipped through `DatabasePerformanceMaintenance` because `CREATE INDEX IF NOT EXISTS` is idempotent and version-agnostic, which avoided a schema bump when the published baseline could not be edited.

**Decision: all three indexes go in the migration AND in `DatabasePerformanceMaintenance`, and both drop-targets are removed from `DatabasePerformanceMaintenance` and `OnModelCreating`.**

Justification:
- **Migration** is mandatory. The upgrade arm in §3 probes the sentinel via index A; if index A did not exist until after `EnsureSchemaStateAsync`, that probe would be a 480 k-row full scan on every startup.
- **`DatabasePerformanceMaintenance`** mirror is consistent with existing practice and is the recovery path if an operator ever restores a database whose `__EFMigrationsHistory` claims 3 but whose indexes were lost (e.g. a hand-edited DB). Cost is three `CREATE INDEX IF NOT EXISTS` no-ops per startup.
- **Removal from `DatabasePerformanceMaintenance` lines 42-43 is not optional** — see §1.3.

Changes to `ChoboServer/Data/DatabasePerformanceMaintenance.cs`, replacing lines 42-43:

```
CREATE INDEX IF NOT EXISTS IX_BackupTableShards_BackupId_ParentFullBackupId ON BackupTableShards (BackupId, ParentFullBackupId);
CREATE INDEX IF NOT EXISTS IX_BackupTableShards_ParentFullBackupId_BackupId ON BackupTableShards (ParentFullBackupId, BackupId);
CREATE INDEX IF NOT EXISTS IX_BackupTableShards_Encrypted_BackupId ON BackupTableShards (BackupId, EncryptedBackupPasswordKeyId) WHERE EncryptedBackupPassword IS NOT NULL;
```

Changes to `ChoboServer/Data/ChoboDbContext.cs`:
- Delete lines 143-145 (`IX_BackupTableShards_Encrypted_BackupTableId`).
- Delete line 148 (`HasIndex(x => x.ParentFullBackupId)` on the shard entity — line 138's `BackupTableEntity` index stays).
- Add, adjacent to line 142:

```csharp
modelBuilder.Entity<BackupTableShardEntity>().HasIndex(x => new { x.BackupId, x.ParentFullBackupId });
modelBuilder.Entity<BackupTableShardEntity>().HasIndex(x => new { x.ParentFullBackupId, x.BackupId });
modelBuilder.Entity<BackupTableShardEntity>()
    .HasIndex(x => new { x.BackupId, x.EncryptedBackupPasswordKeyId }, "IX_BackupTableShards_Encrypted_BackupId")
    .HasFilter("EncryptedBackupPassword IS NOT NULL");
```

EF's default name for the first two is exactly `IX_BackupTableShards_BackupId_ParentFullBackupId` and `IX_BackupTableShards_ParentFullBackupId_BackupId`, matching the migration.

### 2.4 `SchemaUpgradeService`

Current implementation (`ChoboServer/Services/SchemaUpgradeService.cs:20-29`) is a single hard-coded arm gated on `ChoboApi.SchemaVersion == 2`. Bumping to 3 makes it **dead for v1 databases**, which would then fall into the `else` and throw "No upgrade path is registered from schema version 1 to 3". `.release/db-samples/1.0.0` … `1.0.3` are v1 databases, so this is a real regression, not a theoretical one.

Restructure into a sequential ladder:

```csharp
using Chobo.Contracts;
using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;

namespace ChoboServer.Services;

public interface ISchemaUpgradeService
{
    Task UpgradeAsync(SchemaStateEntity schema, CancellationToken cancellationToken = default);
}

public sealed class SchemaUpgradeService(ChoboDbContext db, Serilog.ILogger logger) : ISchemaUpgradeService
{
    private const string EmptyGuidLiteral = "00000000-0000-0000-0000-000000000000";
    private readonly Serilog.ILogger _logger = logger.ForContext<SchemaUpgradeService>();

    public async Task UpgradeAsync(SchemaStateEntity schema, CancellationToken cancellationToken = default)
    {
        if (schema.SchemaVersion > ChoboApi.SchemaVersion)
        {
            throw new InvalidOperationException($"Database schema version {schema.SchemaVersion} is newer than server-supported schema version {ChoboApi.SchemaVersion}.");
        }

        while (schema.SchemaVersion < ChoboApi.SchemaVersion)
        {
            var from = schema.SchemaVersion;
            switch (from)
            {
                case 1:
                    schema.AppliedMigrationId = "000000000002_PasswordProtectedBackups";
                    break;
                case 2:
                    await UpgradeToShardBackupIdsAsync(cancellationToken);
                    schema.AppliedMigrationId = "000000000003_ShardBackupDenormalization";
                    break;
                default:
                    throw new InvalidOperationException($"No upgrade path is registered from schema version {from} to {ChoboApi.SchemaVersion}.");
            }

            schema.SchemaVersion = from + 1;
            schema.AppliedAt = DateTimeOffset.UtcNow;
            _logger.Information("Schema upgraded from version {FromVersion} to {ToVersion} using {AppliedMigrationId}.", from, schema.SchemaVersion, schema.AppliedMigrationId);
        }

        schema.ProductVersion = ChoboApi.ProductVersion;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpgradeToShardBackupIdsAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var repaired = await db.Database.ExecuteSqlRawAsync($"""
            UPDATE BackupTableShards
            SET BackupId = (
                SELECT t.BackupId FROM BackupTables t WHERE t.Id = BackupTableShards.BackupTableId)
            WHERE BackupId = '{EmptyGuidLiteral}'
              AND EXISTS (
                SELECT 1 FROM BackupTables t WHERE t.Id = BackupTableShards.BackupTableId);
            """, cancellationToken);
        var orphaned = await db.BackupTableShards.CountAsync(x => x.BackupId == Guid.Empty, cancellationToken);

        if (repaired > 0)
        {
            _logger.Warning("Schema upgrade 2 -> 3 repaired {RepairedRows} BackupTableShards row(s) that the migration backfill did not cover.", repaired);
        }
        if (orphaned > 0)
        {
            _logger.Error("Schema upgrade 2 -> 3 found {OrphanedRows} BackupTableShards row(s) with no matching BackupTables row.", orphaned);
            throw new InvalidOperationException($"Schema upgrade to version 3 cannot complete: {orphaned} BackupTableShards row(s) have no matching BackupTables row and no BackupId could be derived.");
        }

        db.AuditEntries.Add(new AuditEntryEntity
        {
            ActorName = "system",
            Action = "schema-upgraded",
            EntityType = AuditEntityTypes.ToStorageValue(AuditEntityType.Server),
            Details = JsonSerializer.Serialize(new { fromVersion = 2, toVersion = 3, repairedShardRows = repaired, elapsedMs = timer.ElapsedMilliseconds })
        });

        _logger.Information("Schema upgrade 2 -> 3 completed in {ElapsedMilliseconds} ms; repaired {RepairedRows} shard row(s).", timer.ElapsedMilliseconds, repaired);
    }
}
```

Notes:
- The `while` loop is safe: `schema.SchemaVersion` strictly increases and the `default` arm throws, so it terminates.
- The audit record satisfies the AGENTS.md audit invariant ("the reserved `system` actor for server startup … actions"), which the current v1→v2 arm does **not**. It is written directly to `db.AuditEntries` rather than through `IAuditService`, exactly as `DatabaseBootstrap.InitializeAsync` does at `DatabaseBootstrap.cs:206-213`, because no `IActorContext` exists at startup.
- `db.SaveChangesAsync` at the end persists both the schema state and the audit entry in one call.
- `ExecuteSqlRawAsync` returns rows affected, giving the repair count for free.

### 2.5 `DatabaseBootstrap`

`ChoboServer/Services/DatabaseBootstrap.cs:137` hardcodes the applied-migration id for a fresh database:

```csharp
AppliedMigrationId = "000000000003_ShardBackupDenormalization",
```

No other change to `DatabaseBootstrap`. Its existing per-migration logging (`:52`, `:62`, `:64`) already reports "Schema migration step completed: applied migration 000000000003_ShardBackupDenormalization" and the total apply elapsed ms, which is the migration-side timing requirement of §3.

---

## 3. Backfill

### 3.1 SQL

```sql
UPDATE BackupTableShards
SET BackupId = (
    SELECT t.BackupId FROM BackupTables t WHERE t.Id = BackupTableShards.BackupTableId)
WHERE BackupId = '00000000-0000-0000-0000-000000000000'
  AND EXISTS (
    SELECT 1 FROM BackupTables t WHERE t.Id = BackupTableShards.BackupTableId);
```

Design points:

- **Direct text copy.** `BackupTables.BackupId` and `BackupTableShards.BackupId` are both TEXT holding EF's lowercase `"D"`-format GUID. Copying the column verbatim — no `lower()`, no `replace()`, no reformatting — guarantees the stored bytes match what EF parameterises for `WHERE BackupId = @p0`. This is the single most important correctness detail in the backfill.
- **The `EXISTS` guard is load-bearing.** Without it, an orphan shard would set `BackupId = NULL`, which violates `NOT NULL` and aborts the entire migration transaction. With it, an orphan retains the sentinel and is caught by the explicit check in §2.4, which produces a diagnosable error instead of an opaque constraint failure.
- **The same statement is both the bulk backfill and the idempotent repair.** The `WHERE BackupId = '<sentinel>'` predicate makes it a no-op once complete.

### 3.2 Cost at ~480 k shard rows

| Component | Estimate |
| --- | --- |
| Correlated lookups | 480 k × 2 B-tree probes (`sqlite_autoindex_BackupTables_1` on the TEXT PK, then the row) over a ~60 k-row table that fits in page cache → ~1–3 s |
| Row rewrite | 480 k rows × ~37 bytes added ≈ 17 MB of new payload, but SQLite rewrites whole pages: at ~250 B/row the table is ~120 MB ≈ 30 k pages of 4 KB, all touched → ~120 MB of WAL writes with `synchronous=NORMAL` |
| Index builds (three, after backfill) | three sort-builds over 480 k keys ≈ 3–8 s each |
| **Total** | **~15–90 s on SSD.** Budget one to two minutes of extra startup time on the first boot after upgrade. |

The `LargeMetadataResponsiveness` system test seeds 720 k shards, so the real number is measurable in CI (see §8.4).

### 3.3 Where it runs in the startup sequence

`ChoboServer/Program.cs:54` calls `InitializeChoboDatabaseAsync` **before** `app.MapControllers()` (line 94) and `app.Run()` (line 102). No HTTP listener is bound, no background service is running. Within `ServiceCollectionExtensions.InitializeChoboDatabaseAsync` (lines 213-228):

1. `bootstrap.EnsureDatabaseObjectsAsync()` → `MigrateAsync` → **migration 3 runs here: column, backfill, indexes, drops** — all inside one transaction.
2. `bootstrap.EnsureSchemaStateAsync()` → `SchemaUpgradeService.UpgradeAsync` → **sentinel probe / repair / audit** — index A already exists, so the probe is an index seek.
3. `DatabasePerformanceMaintenance.EnsureAsync(db, sqliteOptions)` → pragmas and idempotent `CREATE INDEX IF NOT EXISTS`.

The server is the only writer for the whole window.

### 3.4 Logging and timing

Required log lines, in order:
- `DatabaseBootstrap.cs:52` — `Schema migration step pending: 000000000003_ShardBackupDenormalization.` (existing, no change)
- `DatabaseBootstrap.cs:62` — `Schema migration step completed: applied migration 000000000003_ShardBackupDenormalization.` (existing)
- `DatabaseBootstrap.cs:64` — `Schema migration apply completed in {ElapsedMilliseconds} ms.` (existing; this is the backfill timing)
- New, from `SchemaUpgradeService.UpgradeToShardBackupIdsAsync` — `Schema upgrade 2 -> 3 completed in {ElapsedMilliseconds} ms; repaired {RepairedRows} shard row(s).` at `Information`; `Warning` when `repaired > 0`; `Error` + throw when orphans remain.
- New, from the ladder — `Schema upgraded from version {FromVersion} to {ToVersion} using {AppliedMigrationId}.`

Also verify the EF-generated `UPDATE` is not misclassified as a slow query by `SlowSqliteQueryLoggingInterceptor` in a way that spams logs; a single `Warning` from it is acceptable and arguably desirable.

### 3.5 Idempotency and resumability

- EF wraps each migration's operations in a transaction and writes the `__EFMigrationsHistory` row inside that transaction. If the process dies mid-`UPDATE`, SQLite rolls back the column, the backfill, and the index creation together, and `__EFMigrationsHistory` has no row for `000000000003`. The next startup re-runs the whole migration from a clean state. **There is no partial state to resume from** — resumability is achieved by atomicity, not by chunking.
- If the process dies **between** the migration commit and `EnsureSchemaStateAsync` committing, the database has the column, backfill and indexes but `SchemaStates.SchemaVersion` is still 2. The next startup finds migration 3 already applied (skipped), then runs the 2→3 upgrade arm, whose `UPDATE … WHERE BackupId = '<sentinel>'` matches zero rows via index A. Idempotent no-op.
- The upgrade arm is safe to re-enter any number of times.

Do **not** chunk the backfill into batches. Chunking would trade atomicity for a marginal reduction in peak transaction size, and would introduce exactly the partial state that the current design eliminates.

---

## 4. Write sites

### 4.1 Complete inventory (verified by `grep -rn "new BackupTableShardEntity"`)

| # | Site | Persisted? | How `BackupId` is populated |
| --- | --- | --- | --- |
| 1 | `ChoboServer/Application/BackupPreparationService.cs:233` | Yes, via `backupTable.Shards.Add` at `:246` then `db.BackupTables.Add(backupTable)` at `:250` | Explicit `BackupId = backup.Id`. `backup.Id` is in scope; `backupTable.BackupId = backup.Id` is set at `:189`. |
| 2 | `ChoboServer/Application/BackupRunnerService.cs:527` | Yes, `table.Shards.Add` on a tracked `BackupTableEntity` loaded at `:501-504` | Explicit `BackupId = backup.Id`. `backup` is the loaded aggregate root; `table.BackupId` is equally available. |
| 3 | `ChoboServer/Application/BackupStorageManifestService.cs:457` (metadata recovery) | Yes, `table.Shards.Add` | Explicit `BackupId = backup.Id`. `backup` is in scope (used at `:430` `BackupId = backup.Id` for the table). |
| 4 | `ChoboServer/Services/ExportImportService.cs:181` (data import) | Yes, `db.BackupTableShards.AddRange` | Explicit, via a lookup built from the import plan — see §7.2. |
| 5 | `ChoboServer/Controllers/TestHooksController.cs:84` | Yes, `table.Shards.Add`, table attached to `backup` at `:99` | Explicit `BackupId = backup.Id`. |
| 6 | `ChoboServer/Controllers/TestHooksController.cs:264` | Yes, `db.BackupTableShards.Add` | Explicit `BackupId = backupId` (local at the top of the seed method). |
| 7 | `ChoboServer/Controllers/TestHooksController.cs:451` | Yes, `db.BackupTableShards.Add` | Explicit `BackupId = backupId`. |
| 8 | `ChoboServer/Controllers/TestHooksController.cs:634` (large-metadata seed) | Yes, collected into `backupShards`, added at `:748` | Explicit `BackupId = backupId` (loop variable). |
| 9 | `ChoboServer/Services/ClickHouseAdapter.cs:192` | **No** — local passed to `StartBackupShardAsync`, which reads only `StoragePath`, `SourceShardNumber`, `EncryptedBackupPassword`, `EncryptedBackupPasswordKeyId` | **Nothing.** Leave unchanged. |
| 10 | `ChoboServer/Services/ClickHouseAdapter.cs:218` | **No** — same, for `StartRestoreShardAsync` | **Nothing.** Leave unchanged. |
| 11 | `Chobo.Tests/ChoboFoundationTests.cs:664,665` | Yes | Covered by the §4.3 guard; no test edit required. |
| 12 | `Chobo.Tests/ChoboFoundationTests.cs:979` | Yes | Covered by the guard. |
| 13 | `Chobo.Tests/ChoboFoundationTests.cs:1944` | Yes | Covered by the guard. |
| 14 | `Chobo.Tests/ChoboFoundationTests.cs:2439` | Yes | Covered by the guard. |
| 15 | `Chobo.Tests/BackupRestoreExecutionTests.cs:7116` | Yes, `backupTable.Shards.Add` | Covered by the guard. |
| 16 | `Chobo.Tests/BackupRestoreExecutionTests.cs:6558,6636` | **No** — fake `IClickHouseAdapter` locals | **Nothing.** |

Sites 11-15 are the ones the state file's inventory misses.

### 4.2 Can EF populate it automatically?

No — EF Core has no concept of a "denormalized copy of a principal's column". Three candidate mechanisms were considered:

| Mechanism | Verdict |
| --- | --- |
| Computed/shadow property fed from the `BackupTable` navigation | Not supported. EF cannot project a principal's scalar into a dependent's column at save time. |
| SQLite `AFTER INSERT` trigger | Rejected. It would be invisible to `EnsureCreated`-built test databases, would not update EF's in-memory entity (so a just-inserted entity would report `Guid.Empty` until reloaded), and would be a new maintenance surface with no test coverage. |
| `SaveChanges` interception in `ChoboDbContext` | **Adopted as a backstop**, see §4.3. |

### 4.3 Recommendation: explicit assignment at every write site, plus a `SaveChanges` guard

**Primary contract: every persisted creation site sets `BackupId` explicitly.** This is what a reader of `BackupPreparationService` expects, and it is what the code review will check.

**Backstop: `ChoboDbContext` normalises and enforces.** Add one private method, called at the top of all four `SaveChanges*` overrides (`ChoboDbContext.cs:32,47,62,65`):

```csharp
private void NormalizeShardBackupIds()
{
    var pending = ChangeTracker.Entries<BackupTableShardEntity>()
        .Where(x => x.State is EntityState.Added && x.Entity.BackupId == Guid.Empty)
        .ToList();
    if (pending.Count == 0)
    {
        return;
    }

    var tablesById = ChangeTracker.Entries<BackupTableEntity>()
        .ToDictionary(x => x.Entity.Id, x => x.Entity);
    foreach (var entry in pending)
    {
        var shard = entry.Entity;
        var table = shard.BackupTable ?? tablesById.GetValueOrDefault(shard.BackupTableId);
        if (table is null || table.BackupId == Guid.Empty)
        {
            throw new InvalidOperationException($"BackupTableShard {shard.Id} cannot be persisted without a BackupId; its BackupTable ({shard.BackupTableId}) is not resolvable from the change tracker.");
        }

        shard.BackupId = table.BackupId;
    }
}
```

Properties of this design:

- **In-memory only.** No database round-trip, so it works identically in the sync and async overloads and cannot deadlock or add latency.
- **Handles the FK-not-yet-fixed-up case.** In `BackupPreparationService` the shard is added via `backupTable.Shards.Add(...)` before EF assigns `BackupTableId`; the `shard.BackupTable` navigation is populated by EF's relationship fixup on `Add`, so the navigation branch resolves it. In `ExportImportService` and `TestHooksController:264/451` only `BackupTableId` is set, but the corresponding `BackupTableEntity` is `Added` in the same context first, so the dictionary branch resolves it.
- **Never overwrites.** It only touches entries whose `BackupId` is already `Guid.Empty`.
- **Fails loud.** An unresolvable case throws with the shard id instead of persisting corrupt data.
- **Zero test churn.** All seven test creation sites (§4.1 rows 11-15) resolve through the dictionary branch, so no test file needs to set `BackupId`.

Cost: `O(changed entries)`, and the early `return` makes it free when no shards are being inserted.

---

## 5. Read sites

Sites are classified into three tiers. The state file's inventory conflates them.

- **Tier 1 — SQL join elimination.** The join exists *only* to reach `BackupId`. These are the performance wins.
- **Tier 2 — SQL predicate improvement.** The join is still needed for other columns, but the `BackupId` predicate becomes an index seek.
- **Tier 3 — in-memory navigation.** Already-`Include`d graphs; the rewrite removes a null-forgiveness operator and nothing else. Cosmetic, zero risk, zero benefit.

### 5.1 Tier 1 — `BackupApplicationService.cs:275-282` — `shardRelatedFullRows`

Before:
```csharp
: await db.BackupTableShards
    .AsNoTracking()
    .Where(x => x.BackupTable != null && summaryIds.Contains(x.BackupTable.BackupId) && x.ParentFullBackupId != null)
    .Select(x => new BackupRelatedFullRow(x.BackupTable!.BackupId, x.ParentFullBackupId!.Value))
    .Distinct()
    .ToListAsync(cancellationToken);
```
After:
```csharp
: await db.BackupTableShards
    .AsNoTracking()
    .Where(x => summaryIds.Contains(x.BackupId) && x.ParentFullBackupId != null)
    .Select(x => new BackupRelatedFullRow(x.BackupId, x.ParentFullBackupId!.Value))
    .Distinct()
    .ToListAsync(cancellationToken);
```

SQL shape, before:
```sql
SELECT DISTINCT t0."BackupId", s."ParentFullBackupId"
FROM "BackupTableShards" AS s
LEFT JOIN "BackupTables" AS t0 ON s."BackupTableId" = t0."Id"
WHERE t0."Id" IS NOT NULL AND t0."BackupId" IN (…200 params…) AND s."ParentFullBackupId" IS NOT NULL;
```
→ full scan of 480 k shard rows, 480 k probes into `BackupTables`.

After:
```sql
SELECT DISTINCT s."BackupId", s."ParentFullBackupId"
FROM "BackupTableShards" AS s
WHERE s."BackupId" IN (…200 params…) AND s."ParentFullBackupId" IS NOT NULL;
```
→ 200 range seeks on index **A**, covering, no table access.

**Equivalence flag (null handling).** Dropping `x.BackupTable != null` removes the `t0."Id" IS NOT NULL` filter, which excluded shards whose `BackupTableId` points at a missing `BackupTables` row. See §5.9.

### 5.2 Tier 1 — `BackupApplicationService.cs:289-295` — `passwordRows`

Before:
```csharp
.Where(x => x.BackupTable != null && summaryIds.Contains(x.BackupTable.BackupId) && x.EncryptedBackupPassword != null)
.Select(x => new BackupPasswordRow(x.BackupTable!.BackupId, x.EncryptedBackupPasswordKeyId))
```
After:
```csharp
.Where(x => summaryIds.Contains(x.BackupId) && x.EncryptedBackupPassword != null)
.Select(x => new BackupPasswordRow(x.BackupId, x.EncryptedBackupPasswordKeyId))
```
Served by index **C** (partial, covering). The downstream `.Distinct()`, `GroupBy`, and `.All(...)` aggregation at `:296-302` are order-insensitive, so the result is byte-identical.

### 5.3 Tier 1 — `BackupApplicationService.cs:372` — `GetAsync` key ids

Before:
```csharp
var keyIds = await db.BackupTableShards.AsNoTracking().Where(x => x.BackupTable != null && x.BackupTable.BackupId == id && x.EncryptedBackupPassword != null).Select(x => x.EncryptedBackupPasswordKeyId).ToListAsync(cancellationToken);
```
After:
```csharp
var keyIds = await db.BackupTableShards.AsNoTracking().Where(x => x.BackupId == id && x.EncryptedBackupPassword != null).Select(x => x.EncryptedBackupPasswordKeyId).ToListAsync(cancellationToken);
```

**Equivalence flag (ordering).** There is no `.Distinct()` and no `OrderBy` here; `keyIds.Count == 0` and `keyIds.All(...)` at `:374` are the only consumers, both order- and duplicate-insensitive. Byte-identical.

### 5.4 Tier 1 — `BackupApplicationService.cs:641-646` — `RelatedFullBackupIdsAsync`

```csharp
var shardParents = await db.BackupTableShards
    .AsNoTracking()
    .Where(x => x.BackupId == backupId && x.ParentFullBackupId != null)
    .Select(x => x.ParentFullBackupId!.Value)
    .ToListAsync(cancellationToken);
```
Index **A** range seek. The caller at `:647` applies `.Distinct().OrderBy(x => x)`, so ordering is pinned.

### 5.5 Tier 1 — `BackupApplicationService.cs:661-667` and `:685-690`

`ChildBackupIdsByBackupIdAsync`:
```csharp
var shardChildren = await db.BackupTableShards
    .AsNoTracking()
    .Where(x => x.ParentFullBackupId != null && backupIds.Contains(x.ParentFullBackupId.Value))
    .Select(x => new BackupChildRow(x.ParentFullBackupId!.Value, x.BackupId))
    .Distinct()
    .ToListAsync(cancellationToken);
```
`DependentBackupIdsAsync`:
```csharp
var shardDependents = await db.BackupTableShards
    .AsNoTracking()
    .Where(x => x.ParentFullBackupId == fullBackupId)
    .Select(x => x.BackupId)
    .Distinct()
    .ToListAsync(cancellationToken);
```
Both served by index **B**, covering. `ChildBackupIdsByBackupIdAsync`'s consumer at `:670-672` applies `.Distinct().OrderBy(id => id)`.

### 5.6 Tier 1 — `DashboardApplicationService.cs:16-38` — five correlated subqueries → one grouped query

Before (`:34-38`), four correlated `SelectMany` subqueries per running backup, each fully expanding `Tables ⋈ Shards`:
```sql
(SELECT COUNT(*) FROM "BackupTables" t INNER JOIN "BackupTableShards" s ON t."Id" = s."BackupTableId" WHERE b."Id" = t."BackupId")
```
…repeated four times with different `Status` predicates. Plus `x.Tables.Count`. At 200 backups' worth of metadata this is four full `BackupTables ⋈ BackupTableShards` traversals per running backup.

After:
```csharp
var runningBackups = await db.Backups
    .AsNoTracking()
    .Where(x => x.Status == BackupRunStatus.Running)
    .OrderBy(x => x.StartedAt ?? x.CreatedAt)
    .Select(x => new RunningBackupRow(
        x.Id, x.Status, x.TriggerType, x.PolicyId,
        x.Policy == null ? null : x.Policy.Name,
        x.ScheduleId,
        x.Schedule == null ? null : x.Schedule.Name,
        x.CreatedAt, x.StartedAt, x.FailureReason, x.IsPinned,
        x.DeletionRequestedAt, x.DeletionReason, x.Tables.Count))
    .ToListAsync(cancellationToken);

var runningBackupIds = runningBackups.Select(x => x.Id).ToList();
var shardStatusCounts = runningBackupIds.Count == 0
    ? []
    : await db.BackupTableShards
        .AsNoTracking()
        .Where(x => runningBackupIds.Contains(x.BackupId))
        .GroupBy(x => new { x.BackupId, x.Status })
        .Select(x => new RunningBackupShardCountRow(x.Key.BackupId, x.Key.Status, x.Count()))
        .ToListAsync(cancellationToken);

var shardCountsByBackupId = shardStatusCounts
    .GroupBy(x => x.BackupId)
    .ToDictionary(x => x.Key, x => x.ToList());

var runningBackupDtos = runningBackups
    .Select(backup =>
    {
        var counts = shardCountsByBackupId.GetValueOrDefault(backup.Id) ?? [];
        return new DashboardRunningBackupDto(
            backup.Id, backup.Status, backup.TriggerType, backup.PolicyId, backup.PolicyName,
            backup.ScheduleId, backup.ScheduleName, backup.CreatedAt, backup.StartedAt,
            backup.FailureReason, backup.IsPinned, backup.DeletionRequestedAt, backup.DeletionReason,
            backup.TableCount,
            counts.Sum(x => x.Count),
            counts.Where(x => x.Status == BackupTableStatus.Succeeded).Sum(x => x.Count),
            counts.Where(x => x.Status == BackupTableStatus.Failed).Sum(x => x.Count),
            counts.Where(x => x.Status == BackupTableStatus.Running).Sum(x => x.Count));
    })
    .ToList();
```

New SQL for the second statement:
```sql
SELECT s."BackupId", s."Status", COUNT(*)
FROM "BackupTableShards" AS s
WHERE s."BackupId" IN (…)
GROUP BY s."BackupId", s."Status";
```
Index **A** range seek per running backup; ~2 400 row fetches for `Status` at reported scale.

Two supporting record types are needed alongside the existing private rows in this file:
```csharp
private sealed record RunningBackupRow(Guid Id, BackupRunStatus Status, BackupTriggerType TriggerType, Guid? PolicyId, string? PolicyName, Guid? ScheduleId, string? ScheduleName, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, string? FailureReason, bool IsPinned, DateTimeOffset? DeletionRequestedAt, string? DeletionReason, int TableCount);
private sealed record RunningBackupShardCountRow(Guid BackupId, BackupTableStatus Status, int Count);
```

**Equivalence flag (atomicity, not results).** For any fixed database state the output is byte-identical, including DTO field order and the `OrderBy(x => x.StartedAt ?? x.CreatedAt)` sequence. What changes is that `TableCount` and the four shard counts now come from two statements instead of one, opening a sub-millisecond window in which a concurrently-running backup could insert a shard between them. This is acceptable: the dashboard reports counts for *actively running* backups, which are already stale by the time they reach the browser. If a reviewer objects, the alternative that preserves single-statement atomicity **and** gets index seeks is to keep the projection shape and replace the navigation with a `DbSet` subquery — `db.BackupTableShards.Count(s => s.BackupId == x.Id && s.Status == BackupTableStatus.Succeeded)` — which yields five correlated index seeks instead of five correlated join scans. Record whichever is chosen in the state file.

### 5.7 Tier 1 — background services

| Site | Before | After |
| --- | --- | --- |
| `BackupGarbageCollectionEvaluationService.cs:115` | `AnyAsync(x => x.BackupTable != null && x.BackupTable.BackupId == backup.Id && x.EffectiveBackupType == BackupType.Full)` | `AnyAsync(x => x.BackupId == backup.Id && x.EffectiveBackupType == BackupType.Full)` |
| `BackupGarbageCollectionEvaluationService.cs:170` | `shard.ParentFullBackupTableShard.BackupTable!.BackupId == fullBackupId` | `shard.ParentFullBackupTableShard.BackupId == fullBackupId` — removes one join hop from the self-referencing subquery |
| `BackupPreparationService.cs:260` | `CountAsync(x => x.BackupTable!.BackupId == backup.Id)` | `CountAsync(x => x.BackupId == backup.Id)` — index **A** count |
| `BackupsGarbageCollectorBackgroundService.cs:347-349` | `.Where(x => x.BackupTable != null && fullBackupIds.Contains(x.BackupTable.BackupId))` | `.Where(x => fullBackupIds.Contains(x.BackupId))` |
| `BackupsGarbageCollectorBackgroundService.cs:356` | `.Select(x => x.BackupTable!.BackupId)` | `.Select(x => x.BackupId)` |
| `BackupsGarbageCollectorBackgroundService.cs:484` | `.Select(x => x.BackupTable!.BackupId)` | `.Select(x => x.BackupId)` |
| `BackupsGarbageCollectorBackgroundService.cs:506-510` | `AnyAsync(x => x.BackupTable!.BackupId == backupId && …)` | `AnyAsync(x => x.BackupId == backupId && …)` |
| `DataRetentionBackgroundService.cs:113-121` | `x.ParentFullBackupTableShard.BackupTable.BackupId` / `x.BackupTable.BackupId` with three null guards | `x.ParentFullBackupTableShard.BackupId` / `x.BackupId`, guards reduced to `x.ParentFullBackupTableShard != null` — removes two join hops |
| `DataRetentionBackgroundService.cs:158-172` | Loads `backupTableIds` (a separate query at `:158-161`) then filters shards by `backupTableIds.Contains(x.BackupTableId)` at `:163` and `:170` | Filter directly on `purgeBackupIds.Contains(x.BackupId)`; delete the `backupTableIds` query entirely. `backupTableIds` has no other consumer — verified. |

### 5.8 Tier 2 and Tier 3 — no join removal

**Tier 2 (predicate becomes a seek, join stays):**

- `BackupRunnerService.cs:1010-1015` — change only the predicate `x.BackupTable!.BackupId == backupId` → `x.BackupId == backupId`. Lines `:1012-1015` still need `BackupTable.Database` and `BackupTable.Table` for ordering and projection, so the join stays. The win is that the driving predicate is now an index seek instead of a scan.

**Tier 3 (in-memory, cosmetic — recommend doing them for consistency, they carry no risk):**

- `RestoreApplicationService.cs:615` — `candidate.BackupTable!.BackupId` → `candidate.BackupId`. `candidates` is materialised at `:733-747` with `.Include(x => x.BackupTable)…`.
- `RestoreRunnerService.cs:478`, `:507`, `:530` — `backupShard.BackupTable!.BackupId` → `backupShard.BackupId`. `backupShard` is loaded at `:417` with `.Include(x => x.BackupTable).ThenInclude(…)`.
- `RestoreApplicationService.cs:770` — `candidate.BackupTable?.BackupId == anchorTable.BackupId` → `candidate.BackupId == anchorTable.BackupId`. Note the semantic tightening: `?.` on a null navigation yields `null`, which never equals a `Guid`; the rewrite compares real values. Given §5.9 this is equivalent, but flag it in review.

**Explicit non-sites (the state file lists these; do not change them):**

- `BackupPreparationService.cs:374-388` (`FindParentFullShardsAsync`) — the query filters on `BackupTable.Backup.PolicyId`, `BackupTable.Backup.Status`, `BackupTable.Database`, `BackupTable.Table` and orders by `BackupTable.Backup.CompletedAt`. The join is intrinsic. No `BackupId` is read.
- `BackupPreparationService.cs:585` — in-memory `OrderByDescending` on an already-loaded candidate list.
- `RestoreApplicationService.cs:733-747` (`LoadShardCandidatesAsync`) — same reasoning as above.
- `DashboardApplicationService.cs:344-357` (`GetTableShardMetricRowsAsync`) — needs `BackupTable.Database`, `BackupTable.Table`, `BackupTable.CompletedAt`, `BackupTable.Backup.Status`, `BackupTable.Backup.CompletedAt`. Join is intrinsic.
- `BackupRestoreQueueApplicationService.cs:220-233`, `:434`, `:991-1004` — these join shard → table → backup to reach `Backups.TargetId`, not `BackupId`. They *could* become shard → backup via the new column, saving one hop in a hot claim path, but the rewrite requires reasoning about inner-vs-left join semantics in three places. **Deferred**; recorded as optional step 13.
- `BackupApplicationService.cs:275` and `:289` were the only correlated-subquery consumers in `ListAsync`; queries 1, 2, 3 and 7 in the state file's table touch `Backups`/`BackupTables` only and are untouched by this change.

### 5.9 Null-handling: the one place the rewrite is not a pure algebraic equivalence

Every Tier 1 rewrite drops an `x.BackupTable != null` guard. EF translates that guard to `LEFT JOIN … WHERE t0."Id" IS NOT NULL`, i.e. "exclude shards whose `BackupTableId` does not resolve". After the rewrite the shard is included on the strength of its own `BackupId` value.

The difference is observable **only** if an orphan `BackupTableShards` row exists — a row whose `BackupTableId` has no matching `BackupTables.Id`.

Why an orphan is unreachable in practice:
1. SQLite FK enforcement is off, so nothing prevents one at the engine level — this is why the analysis matters.
2. The only physical shard-delete path is `DataRetentionBackgroundService.cs:169-176`, which deletes `BackupTableShards` at `:169-171` **strictly before** `BackupTables` at `:172-174`. An intermediate observer can see orphan *tables*, never orphan *shards*.
3. `ExportImportService.ImportAsync:104-113` deletes in the same order (`BackupTableShards` at `:106`, `BackupTables` at `:107`) and re-inserts in dependency order, and `BuildImportPlan:383-392` filters imported shards to those whose `BackupTableId` is in the accepted table set.
4. EF cascade (`ChoboDbContext.cs:171`) removes shards with their table for tracked graphs.

How exact semantics are preserved:
- The `EXISTS` guard in the §3.1 backfill means an orphan retains the `Guid.Empty` sentinel rather than acquiring a plausible-looking `BackupId`.
- The §2.4 upgrade arm hard-fails on any remaining sentinel row, so a database containing an orphan cannot even reach schema 3 — the divergence is unreachable by construction.
- The §4.3 `SaveChanges` guard throws rather than persisting a shard with an unresolvable table.
- A regression test asserts the retention purge leaves no orphan (§8.1, test 8).

Second, smaller flag: `BackupApplicationService.cs:372` (§5.3) has no `Distinct()`/`OrderBy`. Confirmed order-insensitive downstream. Third: `RestoreApplicationService.cs:770`'s `?.` comparison, §5.8.

---

## 6. Invariant protection

The invariant: **no `BackupTableShards` row ever holds `BackupId = Guid.Empty`, and every row's `BackupId` equals its `BackupTables.BackupId`.**

Four layers, ordered by when they act:

| Layer | Where | Cost | Catches |
| --- | --- | --- | --- |
| 1. Backfill inside the migration transaction | §3.1 | one-time | pre-existing rows; all-or-nothing |
| 2. Sentinel probe in the 2→3 upgrade arm | §2.4, `CountAsync(x => x.BackupId == Guid.Empty)` | index-**A** equality seek, `O(log n)` | migration backfill misses, orphans; fails startup with a diagnosable message |
| 3. `SaveChanges` normalise-or-throw | §4.3 | `O(changed entries)`, early-returns to zero | any current or future write site that forgets |
| 4. Unit tests | §8.1 tests 5, 6, 7 | CI | regressions in every backup/import/recovery path |

### Recommendation on a startup consistency check: **against a per-startup check; in favour of the one-time upgrade-arm check.**

Reasoning:

- The naïve formulation the state file implies — a scan for `BackupId IS NULL` or `= ''` — would be a **full 480 k-row table scan on every boot**, adding seconds to every restart of a production server, forever, to detect a condition that layer 3 makes impossible.
- Once index **A** exists, `WHERE BackupId = '00000000-…'` is an equality seek, not a scan, so the *cost* objection disappears. But the *value* objection does not: after the upgrade completes, the only way a sentinel row can appear is through a write path, and layer 3 throws before such a row is ever written. A recurring check would be verifying an invariant that is already enforced at the boundary.
- The upgrade arm is the one moment where the invariant genuinely might not hold (a database whose contents predate the column), and that is exactly where layer 2 runs. Running it once, at the transition, is the correct placement.

If the reviewer wants belt-and-braces beyond this, the cheapest acceptable addition is an `EXISTS(SELECT 1 FROM BackupTableShards WHERE BackupId = '<sentinel>' LIMIT 1)` probe in `DatabasePerformanceMaintenance.EnsureAsync` that logs at `Error` and does **not** throw. It is an index seek, so effectively free. This is optional; the design does not include it.

---

## 7. Export / import

### 7.1 `ExportVersion` must not change — verified, not assumed

The state file asserts `ExportVersion` stays at 1. Confirmed, with three independent supporting facts:

1. **`BackupId` is derivable from the existing envelope.** `BackupTableShardExport` (`Chobo.Contracts/ExportContracts.cs:51`) carries `BackupTableId`; `BackupTableExport` carries `BackupId`; both collections are in every data envelope (`ExportContracts.cs:23`). Import can reconstruct the column exactly. Therefore the serialized shape need not change and `BackupTableShardExport` must **not** gain a `BackupId` member.
2. **Bumping it would be actively harmful.** `ExportImportService.cs:77` rejects on *exact inequality*: `if (envelope.ExportVersion != ChoboApi.ExportVersion) throw`. Bumping to 2 would make every published envelope — including all eight `.release/db-samples/*/data-export.json` and `config-export.json` files, each carrying `"exportVersion": 1` — un-importable, breaking `scripts/Test-UpgradeSamples.ps1` outright.
3. **Schema version is handled separately and correctly.** `ExportImportService.cs:81` uses `if (envelope.SchemaVersion > ChoboApi.SchemaVersion) throw` — a `>` comparison. A v2-schema envelope therefore imports cleanly into a v3 server. `ExportAsync:72` stamps `ChoboApi.SchemaVersion` into new envelopes, so `1.2.0` exports will declare `schemaVersion: 3` and be correctly rejected by older servers. Both behaviours are already right; no change.

**No change to `Chobo.Contracts/ExportContracts.cs`. No change to `ChoboApi.ExportVersion`.**

### 7.2 What `ExportImportService` must do

Only one edit, at `ChoboServer/Services/ExportImportService.cs:181`. Immediately before it, build the lookup:

```csharp
var backupIdByTableId = import.Payload.BackupTables.ToDictionary(x => x.Id, x => x.BackupId);
```

and add `BackupId = backupIdByTableId[x.BackupTableId],` to the shard object initialiser.

The indexer is safe, not a latent `KeyNotFoundException`: `BuildImportPlan` (`:382-392`) filters `backupShards` to `.Where(x => backupTableIds.Contains(x.BackupTableId))` where `backupTableIds` is derived from the same `backupTables` list at `:382`, **before** the plan is returned at `:428-429`. Every shard reaching line 181 therefore has its table in `import.Payload.BackupTables`.

The §4.3 `SaveChanges` guard would also resolve these (the `BackupTables` are `AddRange`d at `:180`, one line earlier, in the same context), but explicit assignment is preferred so the import code states its own invariant.

`ExportAsync:67` is unchanged — it must **not** project `x.BackupId` into `BackupTableShardExport`.

Also verify `ExportImportService.cs:106` (`db.BackupTableShards.ExecuteDeleteAsync()`) needs no change: it is an unconditional delete-all, unaffected by a new column.

---

## 8. Test plan

### 8.1 New and modified unit tests — `Chobo.Tests`

`SchemaUpgradeServiceTests.cs`:

1. **Modify** `Version_one_database_schema_is_upgraded_to_version_two` → rename to `Version_two_database_schema_is_upgraded_to_version_three`. It currently seeds `ChoboApi.SchemaVersion - 1` and asserts `AppliedMigrationId == "000000000002_PasswordProtectedBackups"`. With `SchemaVersion = 3` the seed becomes 2 and the assertion must become `"000000000003_ShardBackupDenormalization"`. **This test will fail if not updated — it is the canary for the version bump.**
2. **Add** `Version_one_database_schema_is_upgraded_through_every_arm_to_current`. Seed `SchemaVersion = 1`; assert after upgrade `stored.SchemaVersion == ChoboApi.SchemaVersion` and `stored.AppliedMigrationId == "000000000003_ShardBackupDenormalization"`. This is the regression test for the ladder restructure; without it the v1→v3 gap in the current single-arm implementation ships silently and breaks the `1.0.x` db-samples.
3. **Add** `Schema_upgrade_two_to_three_backfills_shard_backup_ids`. On the `SchemaFixture` in-memory database (which is built by `EnsureCreatedAsync`, so the column is present and `NOT NULL`), insert a `BackupEntity`, a `BackupTableEntity`, and a `BackupTableShardEntity`; then force the shard's `BackupId` to the sentinel with `ExecuteSqlRaw("UPDATE BackupTableShards SET BackupId = '00000000-0000-0000-0000-000000000000'")` to bypass the §4.3 guard. Run the upgrade from version 2. Assert the shard's `BackupId` now equals the backup's `Id`, and that an `AuditEntryEntity` with `Action == "schema-upgraded"` and `ActorName == "system"` exists.
4. **Add** `Schema_upgrade_two_to_three_fails_when_a_shard_has_no_backup_table`. Insert an orphan shard with the sentinel and no matching table; assert `InvalidOperationException` whose message contains `"no matching BackupTables row"`.
5. **Modify** `Ef_schema_v2_migration_is_additive_and_defaults_existing_policies_to_unprotected` — unchanged in substance, but confirm it still passes (it reflects over `PasswordProtectedBackups` specifically, so it should).
6. **Add** `Ef_schema_v3_migration_adds_one_non_nullable_column_with_an_empty_guid_default`. Mirror the existing reflection pattern: instantiate `ShardBackupDenormalization`, invoke `Up` over a `MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite")`, then assert `Assert.Single(builder.Operations.OfType<AddColumnOperation>())` with `Table == "BackupTableShards"`, `Name == "BackupId"`, `IsNullable == false`, `DefaultValue!.Equals(Guid.Empty)`, and `Assert.DoesNotContain(builder.Operations, o => o is DropColumnOperation or DropTableOperation)`.

`ChoboFoundationTests.cs` (or a new `ShardBackupIdInvariantTests.cs`):

7. **Add** `Shard_backup_id_is_populated_when_only_the_backup_table_id_is_set`. Add a `BackupTableEntity` and a `BackupTableShardEntity` that sets only `BackupTableId`; `SaveChangesAsync`; assert the persisted shard's `BackupId` equals the table's `BackupId`. Covers the §4.3 dictionary branch.
8. **Add** `Shard_backup_id_is_populated_when_added_through_the_table_navigation`. Same, via `table.Shards.Add(...)` with no `BackupTableId` set. Covers the navigation branch.
9. **Add** `Saving_a_shard_whose_backup_table_is_unresolvable_throws`. Add a shard with a random `BackupTableId` and no corresponding tracked table; assert `InvalidOperationException` containing `"cannot be persisted without a BackupId"`.
10. **Add** `Data_import_populates_shard_backup_ids`. Build an `ExportEnvelope` containing one backup, one table and one shard; import; assert the persisted shard's `BackupId` equals the backup's id and that `BackupTableShardExport` still has no `BackupId` member (compile-time, by not referencing one).
11. **Add** `Data_retention_purge_leaves_no_orphan_shard_rows`. Drive `DataRetentionBackgroundService` through a purge and assert `db.BackupTableShards.CountAsync(x => !db.BackupTables.Any(t => t.Id == x.BackupTableId)) == 0`. This is the test that underwrites the §5.9 null-handling argument.

Equivalence tests (the ones that discharge the zero-behavioural-change requirement):

12. **Add** `Backup_list_summary_is_identical_with_and_without_the_denormalized_column`. Seed a graph covering: a full backup, an incremental with `ParentFullBackupId` on both tables and shards, a password-protected backup with an available key, one with an unavailable key, and an unencrypted backup. Then, within the test, compute the expected `RelatedFullBackupIds` / `ChildBackupIds` / `EncryptionState` using the **old join-based LINQ inline** and assert they equal what `BackupApplicationService.ListAsync(includeTables: false)` returns. This makes the equivalence explicit and durable rather than relying on hand-checked fixtures.
13. **Add** `Dashboard_running_backup_shard_counts_match_the_navigation_based_counts`. Same technique for `DashboardApplicationService.GetDashboardAsync`: assert the four counts equal `backup.Tables.SelectMany(t => t.Shards)` computed in memory.

Constructor churn: `SchemaUpgradeServiceTests` constructs the service directly (`new SchemaUpgradeService(fixture.Db)` at three sites). All become `new SchemaUpgradeService(fixture.Db, Serilog.Core.Logger.None)`.

### 8.2 Existing assertion sites — leave unchanged

The 16 sites at `BackupRestoreExecutionTests.cs:348, 380, 405, 698, 724, 748, 1238, 1710, 1738, 1784, 2482, 2684, 2685, 2730, 2731, 4235` all use `x.BackupTable!.BackupId == …`. **Do not rewrite them**, contrary to the state file. Left as-is they continue to exercise the join path and, because the production code now writes and reads the denormalized column, every one of them becomes a free cross-check that the two representations agree. Rewriting them would delete that coverage precisely where it is most valuable.

### 8.3 `scripts/Test-UpgradeSamples.ps1` and `.release/db-samples`

No script change is required. `Get-LatestPriorMinorSample` selects the newest sample whose `(major, minor)` is strictly below the target's, so `-Version 1.2.0` selects `1.1.3` — the v2-schema sample. The script then:

- copies `1.1.3/chobo.db` into a temp data directory, starts the server, and asserts `databaseSchemaVersion -eq schemaVersion` at `/api/v1/server/version`. **This is the end-to-end v2 → v3 upgrade gate, including the real backfill.**
- imports `1.1.3/config-export.json` and `1.1.3/data-export.json` (both `"exportVersion": 1`) into fresh v3 servers and asserts the representative cluster/target/policy/schedule/backup/restore/audit rows are present. **This is the gate for §7.2** — if the import forgot to populate `BackupId`, `backups show --id` would still succeed but the encryption-state and related-full-backup fields would be wrong; test 10 above covers the precise assertion, this covers the wiring.

Run it as:
```powershell
.\scripts\Test-UpgradeSamples.ps1 -Version 1.2.0
```

Additionally, `.\scripts\Test-ReleaseVersionPolicy.ps1 -Version 1.2.0` must be run and its schema advisory reviewed. It reads `SchemaVersion` from `Chobo.Contracts/ChoboApi.cs` (line 47 of the script) and flags `ChoboServer/Services/SchemaUpgradeService.cs` changes as `Ambiguous` requiring human review (line 112) — expected for this change.

At release time, `scripts/New-ReleaseDbSample.ps1 -Version 1.2.0` produces `.release/db-samples/1.2.0/` with `"schemaVersion": 3`, which must be committed (`docs/developer/Releasing.md` step 3).

### 8.4 System test — extend `LargeMetadataResponsiveness`

`TestingSuite/Tests/LargeMetadataResponsiveness/Test.ps1` is the right and only place. It already seeds 300 backups × 100 tables × 24 shards = **720 k shard rows** via `test-hooks/seed-large-metadata-graph` and asserts:

| Measurement | Current `MaxMs` |
| --- | --- |
| `GET backups?includeTables=false` | 5000 |
| `GET backups/{id}?includeTables=false` | 2000 |
| `GET dashboard?nextHours=24` | 5000 |
| `POST backups/garbage-collector/run` | 5000 |

Changes:

1. **Tighten the thresholds** for the three endpoints this design targets. Propose `backups?includeTables=false` 5000 → **1500**, `backups/{id}?includeTables=false` 2000 → **750**, `dashboard` 5000 → **1500**. Set the final numbers from the measured post-change run rather than guessing; the point is that the test must fail if the optimisation regresses.
2. **Add one shard-integrity assertion** to `Assert-LargeMetadataResponsiveness`: after seeding, call `GET backups/{sampleBackupId}` and assert its `tables[].shards[]` count matches `shardsPerTable`, plus a new `test-hooks` read-only endpoint (or an existing one) that reports `COUNT(*) FROM BackupTableShards WHERE BackupId = '00000000-…'` and assert it is zero. If a new endpoint is added it must be read-only and confined to `TestHooksController`.
3. Note that this test carries `ExcludeFromRunAll = $true`, so it must be invoked explicitly through `TestingSuite/TestManager.ps1`.

Also run at least one functional backup/restore suite to prove the write paths: `IncrementalBackupSharded` (exercises `BackupPreparationService` parent-shard selection and `ParentFullBackupId`), `ImportExportRoundTrip` (exercises `ExportImportService.cs:181`), and `BackupMetadataRecovery` (exercises `BackupStorageManifestService.cs:457`). These three cover the three non-trivial write sites end to end.

`SchemaVersionRejection` needs no change — its `TestDefinition.psd1` drives `test-hooks set-future-schema-version-and-crash`, which is version-agnostic.

### 8.5 `ChoboWeb`

Expected no-op. `npm run typecheck` and `npm test` must still pass; `ChoboWeb/openapi/chobo.v1.json` and `ChoboWeb/src/api/generated.ts` must show **no diff** — a diff would mean the API contract leaked, violating the non-goal in the state file. Do not run `npm run update:api` unless a diff is suspected.

### 8.6 Command list

```powershell
dotnet build Chobo.sln -v minimal
dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s
.\scripts\Test-UpgradeSamples.ps1 -Version 1.2.0
.\scripts\Test-ReleaseVersionPolicy.ps1 -Version 1.2.0
.\TestingSuite\TestManager.ps1 -Tests LargeMetadataResponsiveness
.\TestingSuite\TestManager.ps1 -Tests IncrementalBackupSharded,ImportExportRoundTrip,BackupMetadataRecovery
cd ChoboWeb; npm run typecheck; npm test
```

---

## 9. Public member delta

### Added

| Member | Kind | Location |
| --- | --- | --- |
| `BackupTableShardEntity.BackupId` | `public Guid { get; set; }` | `ChoboServer/Data/BackupTableShardEntity.cs` |
| `ChoboServer.Data.Migrations.ShardBackupDenormalization` | `public sealed class : Migration` | `ChoboServer/Data/Migrations/000000000003_ShardBackupDenormalization.cs` |
| `ShardBackupDenormalization.Up(MigrationBuilder)` / `.Down(MigrationBuilder)` | `protected override` | same |

### Modified

| Member | Change |
| --- | --- |
| `ChoboApi.SchemaVersion` | `2` → `3`. Public const value change; consumed by `DatabaseBootstrap.cs:34,111,114,136`, `SchemaUpgradeService.cs:15-17`, `ExportImportService.cs:72,81`, `ServerVersionDto`, `Test-ReleaseVersionPolicy.ps1`. |
| `SchemaUpgradeService..ctor` | `(ChoboDbContext db)` → `(ChoboDbContext db, Serilog.ILogger logger)`. **Breaking for direct construction** — three call sites in `SchemaUpgradeServiceTests.cs`. DI resolution is unaffected: `Serilog.ILogger` is already registered (`DatabaseBootstrap` injects it at `DatabaseBootstrap.cs:27`), so `AddScoped<ISchemaUpgradeService, SchemaUpgradeService>()` continues to resolve. Per `.codex/AGENTS.md`, `Serilog.ILogger` is the required logger abstraction — not `ILogger<T>`. |

### Removed

None public. Removed non-public/DDL artefacts:
- `IX_BackupTableShards_ParentFullBackupId` index (model declaration at `ChoboDbContext.cs:148`, DDL at `DatabasePerformanceMaintenance.cs:42`).
- `IX_BackupTableShards_Encrypted_BackupTableId` index (model declaration at `ChoboDbContext.cs:143-145`, DDL at `DatabasePerformanceMaintenance.cs:43`).
- Local `backupTableIds` query in `DataRetentionBackgroundService.cs:158-161`.

### Unchanged — explicitly confirmed

`BackupDto`, `BackupSummaryRow`, `DashboardRunningBackupDto`, `BackupTableShardDto`, `BackupTableShardExport`, `ExportPayload`, `ExportEnvelope`, `ChoboApi.ExportVersion`, `ChoboApi.ApiVersion`, every controller signature, every CLI command, `ChoboWeb/src/api/generated.ts`. The CLI needs **no** new command: this feature adds no user-visible capability, so the "CLI must stay complete" rule in `.codex/AGENTS.md` is satisfied vacuously.

### DI dependencies of changed classes

| Class | Dependencies |
| --- | --- |
| `SchemaUpgradeService` | `ChoboDbContext` (scoped, existing) + **`Serilog.ILogger` (singleton, existing registration)** |
| `ChoboDbContext` | unchanged — the §4.3 guard uses only `ChangeTracker` |
| `DatabaseBootstrap` | unchanged |
| `DashboardApplicationService` | unchanged (`ChoboDbContext`, `TimeProvider`) |
| `BackupApplicationService`, `ExportImportService`, `DataRetentionBackgroundService`, `BackupsGarbageCollectorBackgroundService`, `BackupPreparationService`, `BackupRunnerService`, `BackupStorageManifestService`, `BackupGarbageCollectionEvaluationService`, `RestoreApplicationService`, `RestoreRunnerService`, `TestHooksController` | unchanged |

No new DI registrations. No new production dependencies (which would require owner confirmation per `.codex/AGENTS.md`).

---

## 10. Ordered implementation steps

Each step is independently buildable, testable, and small enough for a single coding subagent. Steps 1-3 must land together in one commit (the schema is not valid in between); the rest are individually shippable.

| # | Step | Files | Validation |
| --- | --- | --- | --- |
| **1** | Add `BackupId` to `BackupTableShardEntity`; update `OnModelCreating` (add three index declarations, delete lines 143-145 and 148); no navigation, no FK. | `Data/BackupTableShardEntity.cs`, `Data/ChoboDbContext.cs` | `dotnet build Chobo.sln -v minimal` |
| **2** | Add the `SaveChanges` normalise-or-throw guard (§4.3) to all four overrides. | `Data/ChoboDbContext.cs` | `dotnet build`; new tests 7-9 from §8.1 pass |
| **3** | Write migration `000000000003_ShardBackupDenormalization.cs`; bump `ChoboApi.SchemaVersion` to 3; update `DatabaseBootstrap.cs:137`; swap the two index lines in `DatabasePerformanceMaintenance.cs:42-43` for the three new ones. | `Data/Migrations/000000000003_*.cs`, `Chobo.Contracts/ChoboApi.cs`, `Services/DatabaseBootstrap.cs`, `Data/DatabasePerformanceMaintenance.cs` | `dotnet build`; new test 6 from §8.1 |
| **4** | Restructure `SchemaUpgradeService` into the version ladder; add the 2→3 arm with backfill repair, orphan check, audit record, logging; add the `Serilog.ILogger` dependency; update the three constructor call sites in tests. | `Services/SchemaUpgradeService.cs`, `Chobo.Tests/SchemaUpgradeServiceTests.cs` | `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s --filter SchemaUpgradeServiceTests` |
| **5** | Populate `BackupId` at the four production write sites. | `Application/BackupPreparationService.cs:233`, `Application/BackupRunnerService.cs:527`, `Application/BackupStorageManifestService.cs:457`, `Controllers/TestHooksController.cs:84,264,451,634` | `dotnet test … --blame-hang --blame-hang-timeout 30s` (full run; existing backup-path tests must stay green) |
| **6** | Populate `BackupId` on import via the `backupIdByTableId` lookup. Confirm `ExportAsync` and `BackupTableShardExport` are untouched. | `Services/ExportImportService.cs:181` | `dotnet test … --filter ExportImport`; new test 10 |
| **7** | Rewrite the six Tier-1 sites in `BackupApplicationService` (§5.1-§5.5). | `Application/BackupApplicationService.cs:275-282, 289-295, 372, 641-646, 661-667, 685-690` | `dotnet test … --blame-hang --blame-hang-timeout 30s`; new test 12 |
| **8** | Rewrite `DashboardApplicationService` running-backup counts into the grouped query (§5.6); add the two row records. | `Application/DashboardApplicationService.cs:16-38` | `dotnet test …`; new test 13 |
| **9** | Rewrite the Tier-1 background-service sites (§5.7), including deleting the `backupTableIds` query in `DataRetentionBackgroundService`. | `Application/BackupGarbageCollectionEvaluationService.cs:115,170`, `Application/BackupPreparationService.cs:260`, `BackgroundServices/BackupsGarbageCollectorBackgroundService.cs:347-356,484,506-510`, `BackgroundServices/DataRetentionBackgroundService.cs:113-121,158-172` | `dotnet test …`; new test 11 |
| **10** | Apply the Tier-2 and Tier-3 rewrites (§5.8). | `Application/BackupRunnerService.cs:1011`, `Application/RestoreApplicationService.cs:615,770`, `Application/RestoreRunnerService.cs:478,507,530` | `dotnet test …` |
| **11** | Add the remaining unit tests (§8.1 tests 3, 4, 11, 12, 13). | `Chobo.Tests/SchemaUpgradeServiceTests.cs`, `Chobo.Tests/ChoboFoundationTests.cs` | `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s` |
| **12** | Extend `LargeMetadataResponsiveness` (tighter thresholds + shard-integrity assertion); refresh the stale `chobo-schema-versioning` SKILL (current schema 3, latest published `v1.1.3`); note the v3 one-way-door in developer docs. | `TestingSuite/Tests/LargeMetadataResponsiveness/Test.ps1`, `.codex/skills/chobo-schema-versioning/SKILL.md`, `docs/developer/Releasing.md` | `.\TestingSuite\TestManager.ps1 -Tests LargeMetadataResponsiveness` |
| **13** | Full gate. | — | `dotnet build Chobo.sln -v minimal`; `dotnet test … --blame-hang --blame-hang-timeout 30s`; `.\scripts\Test-UpgradeSamples.ps1 -Version 1.2.0`; `.\scripts\Test-ReleaseVersionPolicy.ps1 -Version 1.2.0`; `.\TestingSuite\TestManager.ps1 -Tests IncrementalBackupSharded,ImportExportRoundTrip,BackupMetadataRecovery`; `cd ChoboWeb; npm run typecheck; npm test` |
| **14** (optional, deferred) | Collapse `BackupRestoreQueueApplicationService` shard→table→backup joins to shard→backup (§5.8). | `Application/BackupRestoreQueueApplicationService.cs:220-233,434,991-1004` | `dotnet test …`; `.\TestingSuite\TestManager.ps1 -Tests BackupRestoreSharded` |

---

## Summary and corrections to the state file

**The design.** Add a write-once `Guid BackupId` to `BackupTableShardEntity`, declared `NOT NULL DEFAULT '<empty guid>'` in SQLite (the sentinel default is what makes `ALTER TABLE ADD COLUMN NOT NULL` legal). Migration `000000000003_ShardBackupDenormalization` adds the column, backfills it with a single `UPDATE … SET BackupId = (SELECT t.BackupId FROM BackupTables t …)` guarded by `EXISTS`, then builds three new indexes and drops two now-redundant ones — in that order, so the 480 k-row update pays no index maintenance. `SchemaUpgradeService` is restructured from a single hard-coded arm into a version ladder (the current shape would break v1→v3 upgrades, and four published db-samples are v1), with a 2→3 arm that runs the same statement idempotently as a repair, hard-fails on any remaining sentinel row, logs timing, and writes a `system`-actor audit record. Write-site correctness is guaranteed by explicit assignment at all four production sites plus an in-memory normalise-or-throw guard in `ChoboDbContext.SaveChanges*`, which also means zero churn in the seven test seeding sites. Reads are rewritten in three tiers; only Tier 1 (six sites in `BackupApplicationService`, the dashboard, and seven background-service sites) removes joins.

**State-file claims I found to be wrong or imprecise:**

1. **"SQLite cannot `ADD COLUMN NOT NULL` without a constant default" ⇒ therefore nullable in DDL, non-nullable in the model.** SQLite *can*, given a non-null constant default, and the repo already does it (`000000000002_PasswordProtectedBackups.cs:14`, `PasswordMode INTEGER NOT NULL DEFAULT 0`). The nullable split would also make the migration-built schema differ from the `EnsureCreated`-built schema the unit tests run on, and gives no write-site protection anyway.
2. **"Cascade delete already works via `Backups → BackupTables → BackupTableShards`."** Right conclusion, wrong mechanism. `PRAGMA foreign_keys` is never issued anywhere in the repo, so SQLite FK enforcement is off and the baseline's `ON DELETE CASCADE` clauses are inert. The real guarantees are EF's `DeleteBehavior.Cascade` and the ordered `ExecuteDeleteAsync` chain in `DataRetentionBackgroundService.cs:169-176`.
3. **The write-site inventory is incomplete** — it omits seven test creation sites (`ChoboFoundationTests.cs:664,665,979,1944,2439`, `BackupRestoreExecutionTests.cs:7116`). The `SaveChanges` guard makes them free, but they must be enumerated.
4. **The read-site inventory is over-broad and conflated.** `BackupPreparationService.cs:378` is not a rewrite site at all (the query needs the join for `Database`, `Table`, and `Backup.Status`); `RestoreApplicationService.cs:615` and `RestoreRunnerService.cs:478/507/530` are in-memory navigation on `Include`d graphs, not SQL joins.
5. **"~15 assertion sites to rewrite" is the wrong recommendation.** All 16 should stay on `x.BackupTable!.BackupId` — they then become free cross-checks that the denormalized column agrees with the join.
6. **The single-arm `SchemaUpgradeService` regression is not mentioned anywhere.** Bumping `SchemaVersion` to 3 without restructuring makes `if (schema.SchemaVersion == 1 && ChoboApi.SchemaVersion == 2)` dead, so every v1 database — including `.release/db-samples/1.0.0` through `1.0.3` — would throw "No upgrade path is registered from schema version 1 to 3".
7. **The `DatabasePerformanceMaintenance` resurrection trap is not mentioned.** Dropping the two indexes in the migration without deleting lines 42-43 of `DatabasePerformanceMaintenance.cs` means the very next startup recreates them.
8. **The open question on a startup consistency check is answerable now:** run it once in the 2→3 upgrade arm (an index-A equality seek, `O(log n)`), not on every boot. A per-boot scan would cost seconds forever to detect a condition the `SaveChanges` guard makes unreachable.

Claims I verified as **correct**: `v1.1.3` published / `SchemaVersion = 2` published / new migration required; next release `1.2.0`; `ExportVersion` stays at 1 (and bumping it would break import of all eight published `.release/db-samples` envelopes, since `ExportImportService.cs:77` compares with `!=`); `ClickHouseAdapter.cs:192,218` transient entities need nothing; `IX_BackupTableShards_Encrypted_BackupTableId` becomes redundant; the `chobo-schema-versioning` SKILL file is stale.