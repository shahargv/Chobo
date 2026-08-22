using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChoboServer.Data.Migrations;

/// <summary>
/// Consolidates redundant indexes and gives the garbage collector's parentless-shard lookup a
/// covering index.
///
/// Every index dropped here was verified with EXPLAIN QUERY PLAN to be either never chosen by the
/// planner, or a strict prefix of another index whose leading columns already serve the same
/// queries. SQLite uses at most one index per table per query, so a strict prefix earns nothing on
/// reads while still being maintained on every insert - and the two largest tables here take one
/// row per table and one per shard of every backup.
///
/// Uses IF EXISTS / IF NOT EXISTS rather than MigrationBuilder.DropIndex/CreateIndex because these
/// index names come from two different places: some are created by the baseline migration, others
/// by DatabasePerformanceMaintenance, which runs *after* MigrateAsync. So which of them exist when
/// this migration runs depends on whether the database is fresh or upgraded, and a plain DROP INDEX
/// fails with "no such index" on a fresh one.
/// </summary>
[DbContext(typeof(ChoboDbContext))]
[Migration("000000000003_IndexConsolidation")]
public sealed class IndexConsolidation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            -- Replaces IX_BackupTableShards_ParentFullBackupId and
            -- IX_BackupTableShards_ParentFullBackupId_BackupTableId; both are strict prefixes of it.
            -- EffectiveBackupType in the middle makes the garbage collector's "incremental shards
            -- with no parent" lookup a covering seek, rather than a seek over the whole NULL range
            -- followed by a row lookup per entry just to test the backup type.
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_ParentFullBackupId_EffectiveBackupType_BackupTableId
                ON BackupTableShards (ParentFullBackupId, EffectiveBackupType, BackupTableId);
            DROP INDEX IF EXISTS IX_BackupTableShards_ParentFullBackupId;
            DROP INDEX IF EXISTS IX_BackupTableShards_ParentFullBackupId_BackupTableId;

            -- Strict prefix of IX_BackupTableShards_BackupTableId_SourceShardNumber and of
            -- IX_BackupTableShards_BackupTableId_ParentFullBackupId. Measured never to be chosen by
            -- the planner even when present.
            DROP INDEX IF EXISTS IX_BackupTableShards_BackupTableId;

            -- Strict prefix of IX_BackupTables_BackupId_ParentFullBackupId_BackupSizeBytes.
            DROP INDEX IF EXISTS IX_BackupTables_BackupId;

            -- Strict prefixes of IX_AuditEntries_Timestamp_Id and
            -- IX_AuditEntries_OperationId_Timestamp_Id. The surviving indexes are a better match for
            -- the real query, which orders by Timestamp DESC, Id DESC.
            DROP INDEX IF EXISTS IX_AuditEntries_Timestamp;
            DROP INDEX IF EXISTS IX_AuditEntries_OperationId_Timestamp;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_ParentFullBackupId ON BackupTableShards (ParentFullBackupId);
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_ParentFullBackupId_BackupTableId ON BackupTableShards (ParentFullBackupId, BackupTableId);
            CREATE INDEX IF NOT EXISTS IX_BackupTableShards_BackupTableId ON BackupTableShards (BackupTableId);
            CREATE INDEX IF NOT EXISTS IX_BackupTables_BackupId ON BackupTables (BackupId);
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_Timestamp ON AuditEntries (Timestamp);
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_OperationId_Timestamp ON AuditEntries (OperationId, Timestamp);
            DROP INDEX IF EXISTS IX_BackupTableShards_ParentFullBackupId_EffectiveBackupType_BackupTableId;
            """);
    }
}
