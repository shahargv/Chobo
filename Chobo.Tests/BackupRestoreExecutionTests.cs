using Chobo.Contracts;
using ChoboServer.Application;
using ChoboServer.BackgroundServices;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Repositories;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chobo.Tests;

public sealed class BackupRestoreExecutionTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task Backup_paths_are_self_descriptive_and_unique()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.Inventory.Add(Table("analytics", "orders", "MergeTree"));

        var first = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(first);
        var second = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(second);

        fixture.Db.ChangeTracker.Clear();
        var paths = await fixture.Db.BackupTables.OrderBy(x => x.Table).ThenBy(x => x.S3Path).Select(x => x.S3Path).ToListAsync();
        Assert.Equal(2, paths.Count);
        Assert.All(paths, path =>
        {
            Assert.StartsWith("backups/full/manual/analytics/orders/", path);
            Assert.Matches(@"/[0-9]{8}T[0-9]{9}Z/[0-9a-f]{32}$", path);
        });
        Assert.NotEqual(paths[0], paths[1]);
    }

    [Fact]
    public async Task Backup_selection_excludes_system_databases_even_if_inventory_contains_them()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.AddRange([
            Table("system", "query_log", "MergeTree"),
            Table("information_schema", "tables", "Log"),
            Table("INFORMATION_SCHEMA", "columns", "Log"),
            Table("sales", "orders", "MergeTree")
        ]);

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var tables = await fixture.Db.BackupTables.Select(x => x.Database + "." + x.Table).ToListAsync();
        Assert.Equal(["sales.orders"], tables);
    }

    [Fact]
    public async Task Backup_runner_writes_credentials_free_storage_manifest()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        var manifestEntry = fixture.StorageDeletion.Objects.First(x => x.Key.EndsWith(BackupStorageManifestService.ManifestRelativePath, StringComparison.Ordinal));
        var manifestJson = Encoding.UTF8.GetString(manifestEntry.Value);
        var manifest = JsonSerializer.Deserialize<BackupStorageManifestV1>(manifestJson, JsonOptions)!;

        Assert.Equal(backupId, manifest.Backup.Id);
        Assert.Equal(BackupRunStatus.Succeeded, manifest.Backup.Status);
        Assert.Equal("source", manifest.SourceCluster.Name);
        Assert.Null(manifestJson.Contains("EncryptedPassword", StringComparison.OrdinalIgnoreCase) ? "leaked" : null);
        Assert.DoesNotContain("secret", manifestJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(manifest.Tables, x => x.Database == "sales" && x.Table == "orders");
    }

    [Fact]
    public async Task Backup_runner_keeps_success_when_storage_manifest_write_fails()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.StorageDeletion.FailWriteCount = 10;

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.SingleAsync(x => x.Id == backupId);
        Assert.Equal(BackupRunStatus.Succeeded, backup.Status);
        Assert.Null(backup.FailureReason);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "metadata-manifest-write-failed" && x.EntityId == backupId.ToString()));
    }

    [Fact]
    public async Task Backup_metadata_recovery_recreates_failed_backup_metadata_from_storage_manifest()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var failed = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "failed_orders", BackupTableStatus.Failed, "failed-op", true)
        ]);
        failed.Status = BackupRunStatus.Failed;
        failed.FailureReason = "simulated failure";
        failed.Error = "simulated failure";
        await fixture.Db.SaveChangesAsync();
        await fixture.Services.GetRequiredService<IBackupStorageManifestService>().WriteManifestAsync(failed.Id);

        var storedObjects = fixture.StorageDeletion.Objects.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        await fixture.Db.BackupTableShards.ExecuteDeleteAsync();
        await fixture.Db.BackupTables.ExecuteDeleteAsync();
        await fixture.Db.Backups.ExecuteDeleteAsync();
        await fixture.Db.BackupPolicies.ExecuteDeleteAsync();
        await fixture.Db.SchemaDefinitions.ExecuteDeleteAsync();
        await fixture.Db.ClickHouseAccessNodes.ExecuteDeleteAsync();
        await fixture.Db.ClickHouseClusters.ExecuteDeleteAsync();
        await fixture.Db.BackupTargets.ExecuteDeleteAsync();
        var scanTargetId = Guid.NewGuid();
        fixture.Db.BackupTargets.Add(new BackupTargetEntity
        {
            Id = scanTargetId,
            Name = "scan-target",
            Endpoint = "http://minio:9000",
            Bucket = "data-bucket",
            Region = "us-east-1",
            EncryptedAccessKey = "encrypted-access",
            EncryptedSecretKey = "encrypted-secret"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        fixture.StorageDeletion.Objects.Clear();
        foreach (var entry in storedObjects)
        {
            fixture.StorageDeletion.Objects[entry.Key] = entry.Value;
        }

        var result = await fixture.Services.GetRequiredService<IBackupStorageManifestService>()
            .RecoverFromScanAsync(new RecoverBackupMetadataScanRequest(scanTargetId, ""));

        Assert.Equal(1, result.ImportedBackupCount);
        var recovered = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == failed.Id);
        Assert.Equal(BackupRunStatus.Failed, recovered.Status);
        Assert.Equal("simulated failure", recovered.FailureReason);
        Assert.Equal("failed_orders", Assert.Single(recovered.Tables).Table);
        Assert.Equal("encrypted-access", (await fixture.Db.BackupTargets.SingleAsync(x => x.Id == fixture.TargetId)).EncryptedAccessKey);

        var second = await fixture.Services.GetRequiredService<IBackupStorageManifestService>()
            .RecoverFromScanAsync(new RecoverBackupMetadataScanRequest(scanTargetId, ""));
        Assert.Equal(0, second.ImportedBackupCount);
        Assert.Equal(1, second.UpdatedBackupCount);
    }

    [Fact]
    public async Task Replicated_merge_tree_backup_is_supported()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "replicated_orders", "ReplicatedMergeTree"));

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.SingleAsync(x => x.Id == backupId);
        Assert.Equal(BackupRunStatus.Succeeded, backup.Status);
        var table = await fixture.Db.BackupTables.Include(x => x.Shards).SingleAsync(x => x.BackupId == backupId);
        Assert.True(table.DataBackedUp);
        Assert.Equal(BackupTableStatus.Succeeded, table.Status);
        var shard = Assert.Single(table.Shards);
        Assert.Equal(BackupTableStatus.Succeeded, shard.Status);
        Assert.Contains("replicated_orders", fixture.ClickHouse.BackupStartTables);
    }

    [Fact]
    public async Task Backup_stores_schema_even_when_data_backup_fails()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.NextBackupStatus = "BACKUP_FAILED";
        fixture.ClickHouse.NextBackupError = "simulated data backup failure";

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var table = await fixture.Db.BackupTables.Include(x => x.SchemaDefinition).SingleAsync(x => x.BackupId == backupId);
        Assert.NotEqual(Guid.Empty, table.SchemaDefinitionId);
        Assert.NotNull(table.SchemaDefinition);
        Assert.Equal("CREATE TABLE sales.orders (id UInt64) ENGINE = MergeTree ORDER BY id", table.SchemaDefinition!.CreateTableSql);
        Assert.Equal(BackupTableStatus.Failed, table.Status);
    }

    [Fact]
    public async Task Manual_schema_only_backup_stores_schema_without_starting_data_backup()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));

        var backupId = await fixture.CreateManualBackupAsync(schemaOnly: true);
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.SchemaDefinition).SingleAsync(x => x.Id == backupId);
        Assert.Equal(BackupRunStatus.Succeeded, backup.Status);
        var table = Assert.Single(backup.Tables);
        Assert.False(table.DataBackedUp);
        Assert.Equal(BackupTableStatus.Succeeded, table.Status);
        Assert.Equal("SCHEMA_ONLY", table.ClickHouseStatus);
        Assert.NotNull(table.SchemaDefinition);
        Assert.Empty(fixture.ClickHouse.BackupStartTables);
        Assert.Empty(fixture.StorageDeletion.Objects);
        Assert.False(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "table-skipped"));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "schema-only-tables-skipped" && x.EntityId == backupId.ToString()));
        var summary = await fixture.Services.GetRequiredService<BackupApplicationService>().GetAsync(backupId, includeTables: false);
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.TableCount);
        Assert.Empty(summary.Tables);
    }

    [Fact]
    public async Task Manual_schema_only_backup_with_1000_tables_is_summarized_and_quick()
    {
        await using var fixture = await TestFixture.CreateAsync();
        for (var i = 0; i < 1000; i++)
        {
            fixture.ClickHouse.Inventory.Add(Table("large_schema", $"table_{i:0000}", "MergeTree"));
        }

        var backupId = await fixture.CreateManualBackupAsync(schemaOnly: true);
        var elapsed = await MeasureAsync(() => fixture.RunBackupAsync(backupId));

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.SingleAsync(x => x.Id == backupId);
        var tableCount = await fixture.Db.BackupTables.CountAsync(x => x.BackupId == backupId);
        var shardCount = await fixture.Db.BackupTableShards.CountAsync(x => x.BackupTable!.BackupId == backupId);
        var schemaDefinitions = await fixture.Db.SchemaDefinitions.CountAsync();
        var tableSkippedAudits = await fixture.Db.AuditEntries.CountAsync(x => x.Action == "table-skipped" && x.OperationId == backupId.ToString());
        var aggregateSkippedAudits = await fixture.Db.AuditEntries.CountAsync(x => x.Action == "schema-only-tables-skipped" && x.EntityId == backupId.ToString());
        var summary = await fixture.Services.GetRequiredService<BackupApplicationService>().GetAsync(backupId, includeTables: false);
        var detail = await fixture.Services.GetRequiredService<BackupApplicationService>().GetAsync(backupId, includeTables: true);

        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"Schema-only backup for 1,000 tables should be quick; elapsed {elapsed}.");
        Assert.Equal(BackupRunStatus.Succeeded, backup.Status);
        Assert.Equal(1000, tableCount);
        Assert.Equal(0, shardCount);
        Assert.Equal(1000, schemaDefinitions);
        Assert.Empty(fixture.ClickHouse.BackupStartTables);
        Assert.Equal(0, tableSkippedAudits);
        Assert.Equal(1, aggregateSkippedAudits);
        Assert.NotNull(summary);
        Assert.Equal(1000, summary!.TableCount);
        Assert.Empty(summary.Tables);
        Assert.NotNull(detail);
        Assert.Equal(1000, detail!.TableCount);
        Assert.Equal(1000, detail.Tables.Count);
    }
    [Fact]
    public async Task Schema_and_data_backup_only_starts_data_backup_for_merge_tree_engines()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.AddRange([
            Table("sales", "orders", "MergeTree"),
            Table("sales", "orders_replica", "ReplicatedMergeTree"),
            Table("sales", "events_log", "Log"),
            Table("sales", "lookup", "Join")
        ]);

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var tables = await fixture.Db.BackupTables.Where(x => x.BackupId == backupId).OrderBy(x => x.Table).ToListAsync();
        Assert.True(tables.Single(x => x.Table == "orders").DataBackedUp);
        Assert.True(tables.Single(x => x.Table == "orders_replica").DataBackedUp);
        Assert.False(tables.Single(x => x.Table == "events_log").DataBackedUp);
        Assert.False(tables.Single(x => x.Table == "lookup").DataBackedUp);
        Assert.Equal(["orders", "orders_replica"], fixture.ClickHouse.BackupStartTables.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task Cluster_schema_only_inventory_queries_every_node_and_dedupes_by_name()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var cluster = await fixture.Db.ClickHouseClusters.Include(x => x.AccessNodes).SingleAsync(x => x.Id == fixture.SourceClusterId);
        cluster.Mode = ClusterMode.Cluster;
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(2, "shard-2", 1, "source-s2", 9000, false, 0),
            new ClickHouseShardReplicaInfo(1, "shard-1", 1, "source-s1", 9000, false, 0)
        ]);
        fixture.ClickHouse.InventoryByEndpoint["source-s1:9000:False"] = [Table("sales", "orders", "MergeTree", createSql: "CREATE TABLE sales.orders (id UInt64) ENGINE = MergeTree ORDER BY id")];
        fixture.ClickHouse.InventoryByEndpoint["source-s2:9000:False"] = [Table("sales", "orders", "MergeTree", createSql: "CREATE TABLE sales.orders (id UInt64, shard UInt8) ENGINE = MergeTree ORDER BY id"), Table("sales", "events", "Log")];
        await fixture.Db.SaveChangesAsync();

        var backupId = await fixture.CreateManualBackupAsync(schemaOnly: true);
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(["source-s1", "source-s2"], fixture.ClickHouse.GetTablesEndpoints.Select(x => x.Host).ToList());
        var tables = await fixture.Db.BackupTables.Include(x => x.SchemaDefinition).Where(x => x.BackupId == backupId).OrderBy(x => x.Table).ToListAsync();
        Assert.Equal(["events", "orders"], tables.Select(x => x.Table).ToList());
        Assert.Equal("CREATE TABLE sales.orders (id UInt64) ENGINE = MergeTree ORDER BY id", tables.Single(x => x.Table == "orders").SchemaDefinition!.CreateTableSql);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "schema-inventory-deduplicated" && x.EntityId == backupId.ToString()));
    }

    [Fact]
    public async Task Schema_browser_returns_tree_and_exports_database_sql()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.AddRange([Table("sales", "orders", "MergeTree"), Table("audit", "events", "Log")]);
        var backupId = await fixture.CreateManualBackupAsync(schemaOnly: true);
        await fixture.RunBackupAsync(backupId);

        var service = fixture.Services.GetRequiredService<SchemaBrowserApplicationService>();
        var summaries = await service.ListBackupsAsync(DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Contains(summaries, x => x.Id == backupId && x.ContentMode == BackupContentMode.SchemaOnly && x.SourceClusterName == "source" && x.TableCount == 2);
        var schema = await service.GetBackupSchemaAsync(backupId);
        Assert.NotNull(schema);
        Assert.Equal(["audit", "sales"], schema!.Databases.Select(x => x.Database).ToList());
        var export = await service.ExportSqlAsync(backupId, "sales");
        Assert.Contains("CREATE TABLE sales.orders", export);
        Assert.DoesNotContain("audit.events", export);
    }

    [Fact]
    public async Task Schema_browser_filters_by_range_and_hides_deleted_backups()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        var visibleBackupId = await fixture.CreateManualBackupAsync(schemaOnly: true);
        await fixture.RunBackupAsync(visibleBackupId);
        var deletedBackupId = await fixture.CreateManualBackupAsync(schemaOnly: true);
        await fixture.RunBackupAsync(deletedBackupId);
        var oldBackupId = await fixture.CreateManualBackupAsync(schemaOnly: true);
        await fixture.RunBackupAsync(oldBackupId);

        var now = DateTimeOffset.UtcNow;
        var visible = await fixture.Db.Backups.SingleAsync(x => x.Id == visibleBackupId);
        visible.CompletedAt = now.AddDays(-2);
        var deleted = await fixture.Db.Backups.SingleAsync(x => x.Id == deletedBackupId);
        deleted.Status = BackupRunStatus.BackupExpiredDeleted;
        deleted.CompletedAt = now.AddDays(-1);
        var old = await fixture.Db.Backups.SingleAsync(x => x.Id == oldBackupId);
        old.CompletedAt = now.AddDays(-20);
        await fixture.Db.SaveChangesAsync();

        var service = fixture.Services.GetRequiredService<SchemaBrowserApplicationService>();
        var summaries = await service.ListBackupsAsync(now.AddDays(-7), now);

        Assert.Contains(summaries, x => x.Id == visibleBackupId && x.SourceClusterName == "source");
        Assert.DoesNotContain(summaries, x => x.Id == deletedBackupId);
        Assert.DoesNotContain(summaries, x => x.Id == oldBackupId);
    }

    [Fact]
    public async Task Incremental_backup_uses_parent_full_table_and_falls_back_to_full_for_new_table()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var policy = await fixture.SeedPolicyAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        var full = new BackupEntity
        {
            TriggerType = BackupTriggerType.Manual,
            Status = BackupRunStatus.Queued,
            BackupType = BackupType.Full,
            SourceClusterId = fixture.SourceClusterId,
            TargetId = fixture.TargetId,
            PolicyId = policy.Id
        };
        fixture.Db.Backups.Add(full);
        await fixture.Db.SaveChangesAsync();
        await fixture.RunBackupAsync(full.Id);

        fixture.ClickHouse.Inventory.Add(Table("sales", "new_orders", "MergeTree"));
        var incremental = new BackupEntity
        {
            TriggerType = BackupTriggerType.Manual,
            Status = BackupRunStatus.Queued,
            BackupType = BackupType.Incremental,
            SourceClusterId = fixture.SourceClusterId,
            TargetId = fixture.TargetId,
            PolicyId = policy.Id
        };
        fixture.Db.Backups.Add(incremental);
        await fixture.Db.SaveChangesAsync();
        await fixture.RunBackupAsync(incremental.Id);

        fixture.Db.ChangeTracker.Clear();
        var tables = await fixture.Db.BackupTables.Include(x => x.Shards).Where(x => x.BackupId == incremental.Id).OrderBy(x => x.Table).ToListAsync();
        var newTable = Assert.Single(tables, x => x.Table == "new_orders");
        var existingTable = Assert.Single(tables, x => x.Table == "orders");
        Assert.Equal(BackupType.Full, newTable.EffectiveBackupType);
        Assert.StartsWith("backups/full/", newTable.S3Path);
        Assert.Equal(BackupType.Incremental, existingTable.EffectiveBackupType);
        Assert.NotNull(existingTable.ParentFullBackupTableId);
        Assert.Contains($"parent-full-{full.Id:N}", existingTable.S3Path);
        Assert.Equal(BackupType.Incremental, Assert.Single(existingTable.Shards).EffectiveBackupType);
        Assert.Contains(fixture.Db.BackupTableShards.Single(x => x.Id == existingTable.Shards[0].ParentFullBackupTableShardId).S3Path, fixture.ClickHouse.BackupBasePaths);
    }

    [Fact]
    public async Task Incremental_sharded_backup_selects_parent_per_shard_and_falls_back_when_one_shard_has_no_parent()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "shard-1", 1, "source-s1", 9000, false, 0),
            new ClickHouseShardReplicaInfo(2, "shard-2", 1, "source-s2", 9000, false, 0)
        ]);
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        var policy = await fixture.SeedPolicyAsync();
        var full = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, DateTimeOffset.UtcNow.AddMinutes(-10), shardCount: 1, tableName: "orders");

        var incremental = new BackupEntity
        {
            TriggerType = BackupTriggerType.Manual,
            Status = BackupRunStatus.Queued,
            BackupType = BackupType.Incremental,
            SourceClusterId = fixture.SourceClusterId,
            TargetId = fixture.TargetId,
            PolicyId = policy.Id
        };
        fixture.Db.Backups.Add(incremental);
        await fixture.Db.SaveChangesAsync();
        await fixture.RunBackupAsync(incremental.Id);

        fixture.Db.ChangeTracker.Clear();
        var table = await fixture.Db.BackupTables.Include(x => x.Shards).SingleAsync(x => x.BackupId == incremental.Id);
        var shardOne = Assert.Single(table.Shards, x => x.SourceShardNumber == 1);
        var shardTwo = Assert.Single(table.Shards, x => x.SourceShardNumber == 2);
        Assert.Equal(BackupType.Incremental, shardOne.EffectiveBackupType);
        Assert.NotNull(shardOne.ParentFullBackupTableShardId);
        Assert.Contains($"parent-full-{full.Id:N}", shardOne.S3Path);
        Assert.Equal(BackupType.Full, shardTwo.EffectiveBackupType);
        Assert.Null(shardTwo.ParentFullBackupTableShardId);
        Assert.StartsWith("backups/full/", shardTwo.S3Path);
    }

    [Fact]
    public async Task Backup_runner_enforces_global_and_cluster_maxdop()
    {
        await using var globalFixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 2, PollInterval = TimeSpan.FromMilliseconds(1) });
        globalFixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(40);
        for (var i = 0; i < 6; i++)
        {
            globalFixture.ClickHouse.Inventory.Add(Table("sales", $"orders_{i}", "MergeTree"));
        }

        var globalBackup = await globalFixture.CreateManualBackupAsync();
        await globalFixture.RunBackupAsync(globalBackup);
        Assert.Equal(2, globalFixture.ClickHouse.MaxConcurrentBackupStarts);

        await using var overrideFixture = await TestFixture.CreateAsync(
            clusterMaxDop: 1,
            options: new ChoboBackupRestoreOptions { MaxDop = 3, PollInterval = TimeSpan.FromMilliseconds(1) });
        overrideFixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(40);
        for (var i = 0; i < 4; i++)
        {
            overrideFixture.ClickHouse.Inventory.Add(Table("sales", $"cluster_orders_{i}", "MergeTree"));
        }

        var overrideBackup = await overrideFixture.CreateManualBackupAsync();
        await overrideFixture.RunBackupAsync(overrideBackup);
        Assert.Equal(1, overrideFixture.ClickHouse.MaxConcurrentBackupStarts);
    }

    [Fact]
    public async Task Backup_runner_prefers_shard_parallelism_before_starting_next_table()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 3, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(40);
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.Add(new ClickHouseShardReplicaInfo(1, "shard-1", 1, "source-s1", 9000, false, 0));
        fixture.ClickHouse.Topology.Add(new ClickHouseShardReplicaInfo(2, "shard-2", 1, "source-s2", 9000, false, 0));
        fixture.ClickHouse.Topology.Add(new ClickHouseShardReplicaInfo(3, "shard-3", 1, "source-s3", 9000, false, 0));
        fixture.ClickHouse.Topology.Add(new ClickHouseShardReplicaInfo(4, "shard-4", 1, "source-s4", 9000, false, 0));
        fixture.ClickHouse.Inventory.Add(Table("sales", "aaa_wide", "MergeTree"));
        fixture.ClickHouse.Inventory.Add(Table("sales", "zzz_next", "MergeTree"));

        var backup = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backup);

        Assert.Equal(3, fixture.ClickHouse.MaxConcurrentBackupStarts);
        Assert.Equal(8, fixture.ClickHouse.BackupStartTables.Count);
        Assert.Equal(["aaa_wide", "aaa_wide", "aaa_wide"], fixture.ClickHouse.BackupStartTables.Take(3).ToArray());
        Assert.Equal(4, fixture.ClickHouse.BackupStartTables.Count(x => x == "aaa_wide"));
        Assert.Equal(4, fixture.ClickHouse.BackupStartTables.Count(x => x == "zzz_next"));
    }

    [Fact]
    public async Task Backup_resume_continues_known_operation_ids_and_does_not_rerun_completed_tables()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "done", BackupTableStatus.Succeeded, "already-done", true),
            new SeedBackupTable("sales", "running", BackupTableStatus.Running, "known-op", true)
        ]);
        fixture.ClickHouse.KnownOperations.Add("known-op", "BACKUP_CREATED");

        await fixture.RunBackupAsync(backup.Id);

        fixture.Db.ChangeTracker.Clear();
        Assert.DoesNotContain("done", fixture.ClickHouse.BackupStartTables);
        Assert.DoesNotContain("running", fixture.ClickHouse.BackupStartTables);
        var tables = await fixture.Db.BackupTables.OrderBy(x => x.Table).ToListAsync();
        Assert.All(tables, table => Assert.Equal(BackupTableStatus.Succeeded, table.Status));
    }

    [Fact]
    public async Task Backup_resume_fails_uncertain_missing_clickhouse_operation()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Running, "missing-op", true)
        ]);

        await fixture.RunBackupAsync(backup.Id);

        fixture.Db.ChangeTracker.Clear();
        var table = await fixture.Db.BackupTables.SingleAsync();
        var backupAfterRun = await fixture.Db.Backups.SingleAsync(x => x.Id == backup.Id);
        Assert.Equal(BackupRunStatus.Failed, backupAfterRun.Status);
        Assert.Equal(BackupTableStatus.Failed, table.Status);
        Assert.Equal("missing-op", table.ClickHouseOperationId);
        Assert.DoesNotContain("orders", fixture.ClickHouse.BackupStartTables);
        Assert.Contains("missing from system.backups", backupAfterRun.FailureReason);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-failed" && x.EntityType == "backup-table-shard"));
    }

    [Fact]
    public async Task Restore_resume_continues_known_operations_and_starts_queued_tables()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true),
            new SeedBackupTable("sales", "items", BackupTableStatus.Succeeded, "backup-op-2", true)
        ]);
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "orders", RestoreTableStatus.Running, "restore-known"),
            new SeedRestoreTable("sales", "items", RestoreTableStatus.Queued, null)
        ]);
        fixture.ClickHouse.KnownOperations.Add("restore-known", "RESTORED");

        await fixture.RunRestoreAsync(restore.Id);

        fixture.Db.ChangeTracker.Clear();
        var tables = await fixture.Db.RestoreTables.OrderBy(x => x.SourceTable).ToListAsync();
        Assert.All(tables, table => Assert.Equal(RestoreTableStatus.Succeeded, table.Status));
        Assert.DoesNotContain("orders", fixture.ClickHouse.RestoreStartTables);
        Assert.Contains("items", fixture.ClickHouse.RestoreStartTables);
    }

    [Fact]
    public async Task Restore_list_includes_shard_details()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "orders", RestoreTableStatus.Succeeded, "restore-op")
        ]);

        using var scope = fixture.Services.CreateScope();
        var restores = await scope.ServiceProvider.GetRequiredService<RestoreApplicationService>().ListAsync();

        var dto = Assert.Single(restores, x => x.Id == restore.Id);
        var table = Assert.Single(dto.Tables);
        var shard = Assert.Single(table.Shards);
        Assert.Equal(1, shard.SourceShardNumber);
        Assert.Equal(RestoreTableStatus.Succeeded, shard.Status);
    }

    [Fact]
    public async Task Scheduler_skips_duplicate_active_runs_for_same_schedule()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        fixture.Db.Backups.Add(new BackupEntity
        {
            TriggerType = BackupTriggerType.Scheduled,
            Status = BackupRunStatus.Running,
            SourceClusterId = fixture.SourceClusterId,
            TargetId = fixture.TargetId,
            PolicyId = schedule.PolicyId,
            ScheduleId = schedule.Id
        });
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<BackupSchedulerDispatcherBackgroundService>().DispatchOnceAsync();

        Assert.Equal(1, await fixture.Db.Backups.CountAsync(x => x.ScheduleId == schedule.Id));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "schedule-skip-active" && x.EntityId == schedule.Id.ToString()));
    }

    [Fact]
    public async Task Scheduled_backup_runner_skips_duplicate_active_policy_backup_but_allows_manual()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        var active = new BackupEntity
        {
            TriggerType = BackupTriggerType.Scheduled,
            Status = BackupRunStatus.Running,
            SourceClusterId = fixture.SourceClusterId,
            TargetId = fixture.TargetId,
            PolicyId = schedule.PolicyId,
            ScheduleId = schedule.Id,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        };
        var duplicate = new BackupEntity
        {
            TriggerType = BackupTriggerType.Scheduled,
            Status = BackupRunStatus.Queued,
            SourceClusterId = fixture.SourceClusterId,
            TargetId = fixture.TargetId,
            PolicyId = schedule.PolicyId,
            ScheduleId = schedule.Id,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        fixture.Db.Backups.AddRange(active, duplicate);
        await fixture.Db.SaveChangesAsync();

        await fixture.RunBackupAsync(duplicate.Id);

        fixture.Db.ChangeTracker.Clear();
        var skipped = await fixture.Db.Backups.SingleAsync(x => x.Id == duplicate.Id);
        Assert.Equal(BackupRunStatus.Canceled, skipped.Status);
        Assert.Contains("same policy", skipped.FailureReason);
        Assert.False(await fixture.Db.BackupTables.AnyAsync(x => x.BackupId == duplicate.Id));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "scheduled-duplicate-skipped" && x.EntityId == duplicate.Id.ToString()));

        var manual = new BackupEntity
        {
            TriggerType = BackupTriggerType.Manual,
            Status = BackupRunStatus.Queued,
            BackupType = BackupType.Full,
            SourceClusterId = fixture.SourceClusterId,
            TargetId = fixture.TargetId,
            PolicyId = schedule.PolicyId,
            RequestedByName = "operator"
        };
        fixture.Db.Backups.Add(manual);
        await fixture.Db.SaveChangesAsync();

        await fixture.RunBackupAsync(manual.Id);

        fixture.Db.ChangeTracker.Clear();
        var manualAfterRun = await fixture.Db.Backups.SingleAsync(x => x.Id == manual.Id);
        Assert.Equal(BackupRunStatus.Succeeded, manualAfterRun.Status);
        Assert.True(await fixture.Db.BackupTables.AnyAsync(x => x.BackupId == manual.Id));
    }

    [Fact]
    public async Task Scheduler_enqueues_only_when_cron_is_due()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        schedule.CronExpression = "0 0 0 1 1 ? 2099";
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<BackupSchedulerDispatcherBackgroundService>().DispatchOnceAsync();

        Assert.False(await fixture.Db.Backups.AnyAsync(x => x.ScheduleId == schedule.Id));
    }

    [Fact]
    public async Task Scheduler_skips_and_audits_schedule_with_invalid_cron_expression()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        schedule.CronExpression = "0 0 99 * * ?";
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<BackupSchedulerDispatcherBackgroundService>().DispatchOnceAsync();

        Assert.False(await fixture.Db.Backups.AnyAsync(x => x.ScheduleId == schedule.Id));
        var audit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "schedule-skip-invalid-cron" && x.EntityId == schedule.Id.ToString());
        Assert.Contains("cronExpression", audit.Details);
        Assert.Contains("invalid", audit.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schedule_service_rejects_invalid_cron_expression()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        var service = fixture.Services.GetRequiredService<ScheduleApplicationService>();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(
            schedule.Id,
            new UpsertScheduleRequest(
                "bad cron",
                schedule.PolicyId,
                BackupType.Full,
                "0 0 99 * * ?",
                "UTC",
                true,
                null,
                null)));

        Assert.Contains("CronExpression is invalid", error.Message);
    }

    [Fact]
    public async Task Policy_service_lists_inventory_and_simulates_selector_against_clickhouse_tables()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.AddRange([
            Table("sales", "orders", "MergeTree"),
            Table("sales", "orders_archive", "MergeTree"),
            Table("logs", "events", "MergeTree")
        ]);
        var service = fixture.Services.GetRequiredService<PolicyApplicationService>();
        var selector = new PolicySelector(1, [
            new PolicySelectorRule(PolicySelectorAction.Include, new SelectorPattern(PolicyMatchKind.Exact, "sales"), new SelectorPattern(PolicyMatchKind.Wildcard, "orders*")),
            new PolicySelectorRule(PolicySelectorAction.Exclude, new SelectorPattern(PolicyMatchKind.All, "*"), new SelectorPattern(PolicyMatchKind.Exact, "orders_archive"))
        ]);

        var inventory = await service.ListInventoryAsync(fixture.SourceClusterId);
        var simulation = await service.SimulateAsync(new PolicySimulationRequest(fixture.SourceClusterId, selector));

        Assert.NotNull(inventory);
        Assert.Equal(3, inventory!.Tables.Count);
        Assert.NotNull(simulation);
        Assert.Equal(3, simulation!.Inventory.Count);
        Assert.Equal([new PolicyInventoryTable("sales", "orders")], simulation.Tables);
        Assert.Equal(1, fixture.ClickHouse.GetTablesCallCount);
        fixture.ClickHouse.Inventory.Add(Table("sales", "new_orders", "MergeTree"));
        var cachedInventory = await service.ListInventoryAsync(fixture.SourceClusterId);
        Assert.Equal(3, cachedInventory!.Tables.Count);
        Assert.Equal(1, fixture.ClickHouse.GetTablesCallCount);
    }

    [Fact]
    public async Task Scheduler_enqueues_due_cron_schedule()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        schedule.CronExpression = "0/1 * * * * ?";
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<BackupSchedulerDispatcherBackgroundService>().DispatchOnceAsync();

        var backup = await fixture.Db.Backups.SingleAsync(x => x.ScheduleId == schedule.Id);
        Assert.Equal(schedule.PolicyId, backup.PolicyId);
        Assert.Equal(BackupTriggerType.Scheduled, backup.TriggerType);
        Assert.Equal(BackupRunStatus.Queued, backup.Status);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "scheduled-backup-enqueued" && x.EntityId == schedule.Id.ToString()));
    }

    [Fact]
    public async Task Scheduler_enqueues_schedule_that_is_due_within_grace_period_since_last_decision()
    {
        var now = new DateTimeOffset(2026, 5, 11, 8, 2, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(
            options: new ChoboBackupRestoreOptions
            {
                MaxDop = 3,
                PollInterval = TimeSpan.FromMilliseconds(1),
                SchedulerInterval = TimeSpan.FromMinutes(1),
                SchedulerMissedRunGracePeriod = TimeSpan.FromMinutes(5)
            },
            timeProvider: new FixedTimeProvider(now));
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        schedule.CronExpression = "0 0 8 ? * MON";
        schedule.CreatedAt = now.AddHours(-1);
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<BackupSchedulerDispatcherBackgroundService>().DispatchOnceAsync();

        var backup = await fixture.Db.Backups.SingleAsync(x => x.ScheduleId == schedule.Id);
        Assert.Equal(BackupRunStatus.Queued, backup.Status);
        var audit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "scheduled-backup-enqueued" && x.EntityId == schedule.Id.ToString());
        Assert.Contains("plannedRunAt", audit.Details);
    }

    [Fact]
    public async Task Scheduler_audits_and_skips_schedule_that_is_due_but_outside_grace_period()
    {
        var now = new DateTimeOffset(2026, 5, 11, 8, 50, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(
            options: new ChoboBackupRestoreOptions
            {
                MaxDop = 3,
                PollInterval = TimeSpan.FromMilliseconds(1),
                SchedulerInterval = TimeSpan.FromMinutes(1),
                SchedulerMissedRunGracePeriod = TimeSpan.FromMinutes(5)
            },
            timeProvider: new FixedTimeProvider(now));
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        schedule.CronExpression = "0 0 8 ? * MON";
        schedule.CreatedAt = now.AddHours(-2);
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<BackupSchedulerDispatcherBackgroundService>().DispatchOnceAsync();

        Assert.False(await fixture.Db.Backups.AnyAsync(x => x.ScheduleId == schedule.Id));
        var audit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "scheduled-backup-missed" && x.EntityId == schedule.Id.ToString());
        Assert.Contains("plannedRunAt", audit.Details);
        Assert.Contains("latenessSeconds", audit.Details);
    }

    [Fact]
    public async Task Scheduler_uses_schedule_grace_period_override_when_specified()
    {
        var now = new DateTimeOffset(2026, 5, 11, 8, 50, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(
            options: new ChoboBackupRestoreOptions
            {
                MaxDop = 3,
                PollInterval = TimeSpan.FromMilliseconds(1),
                SchedulerInterval = TimeSpan.FromMinutes(1),
                SchedulerMissedRunGracePeriod = TimeSpan.FromMinutes(5)
            },
            timeProvider: new FixedTimeProvider(now));
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        schedule.CronExpression = "0 0 8 ? * MON";
        schedule.CreatedAt = now.AddHours(-2);
        schedule.MissedRunGracePeriod = TimeSpan.FromHours(1);
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<BackupSchedulerDispatcherBackgroundService>().DispatchOnceAsync();

        var backup = await fixture.Db.Backups.SingleAsync(x => x.ScheduleId == schedule.Id);
        Assert.Equal(BackupRunStatus.Queued, backup.Status);
        Assert.False(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "scheduled-backup-missed" && x.EntityId == schedule.Id.ToString()));
    }

    [Fact]
    public async Task Scheduler_skips_due_schedule_when_same_policy_already_has_active_backup()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var firstSchedule = await fixture.SeedPolicyAndScheduleAsync();
        var secondSchedule = new BackupScheduleEntity
        {
            Name = "same-policy-fast",
            PolicyId = firstSchedule.PolicyId,
            BackupType = BackupType.Full,
            CronExpression = "* * * * * ?",
            TimeZoneId = "UTC",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        fixture.Db.BackupSchedules.Add(secondSchedule);
        fixture.Db.Backups.Add(new BackupEntity
        {
            TriggerType = BackupTriggerType.Scheduled,
            Status = BackupRunStatus.Running,
            SourceClusterId = fixture.SourceClusterId,
            TargetId = fixture.TargetId,
            PolicyId = firstSchedule.PolicyId,
            ScheduleId = firstSchedule.Id
        });
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<BackupSchedulerDispatcherBackgroundService>().DispatchOnceAsync();

        Assert.Equal(1, await fixture.Db.Backups.CountAsync(x => x.PolicyId == firstSchedule.PolicyId));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "schedule-skip-active-policy" && x.EntityId == secondSchedule.Id.ToString()));
    }

    [Fact]
    public async Task Dashboard_reports_active_runs_schedule_history_future_runs_and_policy_freshness()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        var completedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        fixture.Db.Backups.AddRange(
            new BackupEntity
            {
                TriggerType = BackupTriggerType.Scheduled,
                Status = BackupRunStatus.Succeeded,
                SourceClusterId = fixture.SourceClusterId,
                TargetId = fixture.TargetId,
                PolicyId = schedule.PolicyId,
                ScheduleId = schedule.Id,
                CreatedAt = completedAt.AddMinutes(-1),
                StartedAt = completedAt.AddMinutes(-1),
                CompletedAt = completedAt
            },
            new BackupEntity
            {
                TriggerType = BackupTriggerType.Scheduled,
                Status = BackupRunStatus.Running,
                SourceClusterId = fixture.SourceClusterId,
                TargetId = fixture.TargetId,
                PolicyId = schedule.PolicyId,
                ScheduleId = schedule.Id,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(-8)
            });
        await fixture.Db.SaveChangesAsync();

        var service = fixture.Services.GetRequiredService<DashboardApplicationService>();
        var dashboard = await service.GetDashboardAsync(nextHours: 1);
        var metrics = await service.GetMetricsAsync();

        var running = Assert.Single(dashboard.RunningBackups);
        Assert.Equal("hourly", running.PolicyName);
        Assert.Equal("hourly", running.ScheduleName);
        Assert.Equal(BackupRunStatus.Running, running.Status);

        var summary = Assert.Single(dashboard.Schedules);
        Assert.Equal("hourly", summary.ScheduleName);
        Assert.Equal("hourly", summary.PolicyName);
        Assert.Equal(BackupRunStatus.Running, summary.LastRunStatus);
        Assert.NotNull(summary.LastSuccessfulRunCompletedAt);
        Assert.True(Math.Abs((summary.LastSuccessfulRunCompletedAt.Value - completedAt).TotalSeconds) < 1);
        Assert.NotEmpty(dashboard.FutureSchedules);
        Assert.All(dashboard.FutureSchedules, x => Assert.Equal(schedule.Id, x.ScheduleId));

        Assert.Equal(3, metrics.Count);
        Assert.NotNull(metrics["Policies.TimeSecondsSinceLastPolicyBackup.hourly"]);
        Assert.True(metrics["Policies.TimeSecondsSinceLastPolicyBackup.hourly"] >= 0);
        Assert.Equal(0, metrics["Policies.PartialBackups.hourly"]);
        Assert.Equal(0, metrics["Policies.FailedBackups.hourly"]);
    }

    [Fact]
    public async Task Manual_delete_is_requested_without_deleting_storage_inline()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        backup.CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await fixture.Db.SaveChangesAsync();

        var actor = fixture.Services.GetRequiredService<ActorContext>();
        actor.ActorName = "operator";
        actor.UserId = Guid.NewGuid();
        var dto = await fixture.Services.GetRequiredService<BackupApplicationService>().RequestDeleteAsync(backup.Id, confirmDestructive: true);

        Assert.NotNull(dto);
        Assert.Equal(BackupRunStatus.ManualDeleteRequested, dto!.Status);
        Assert.Empty(fixture.StorageDeletion.DeletedDirectories);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "delete-requested" && x.EntityId == backup.Id.ToString()));
    }

    [Fact]
    public async Task Manual_delete_requires_destructive_confirmation()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Services.GetRequiredService<BackupApplicationService>().RequestDeleteAsync(backup.Id));

        Assert.Contains("ConfirmDestructive=true", error.Message);
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == backup.Id)).Status);
    }
    [Fact]
    public async Task Background_cleanup_deletes_manual_request_and_keeps_records()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.ManualDeleteRequested;
        backup.DeletionRequestedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<RetentionManagementBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Db.Backups.SingleAsync(x => x.Id == backup.Id);
        Assert.Equal(BackupRunStatus.ManualDeleted, completed.Status);
        Assert.NotEmpty(fixture.StorageDeletion.DeletedDirectories);
        Assert.Equal(1, await fixture.Db.Backups.CountAsync(x => x.Id == backup.Id));
        Assert.True(await fixture.Db.BackupTables.AnyAsync(x => x.BackupId == backup.Id));
        Assert.True(await fixture.Db.BackupTableShards.AnyAsync(x => x.BackupTable!.BackupId == backup.Id));
    }

    [Fact]
    public async Task Retention_skips_pinned_backups_and_preserves_minimum_successful_backups()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync(retentionMinutes: 60, minBackupsToKeep: 1);
        var oldest = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-5));
        var pinned = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-4), isPinned: true);
        var keptNewest = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-3));
        var failed = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Failed, now.AddHours(-6));

        await fixture.Services.GetRequiredService<RetentionManagementBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(BackupRunStatus.BackupExpiredDeleted, (await fixture.Db.Backups.SingleAsync(x => x.Id == oldest.Id)).Status);
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == pinned.Id)).Status);
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == keptNewest.Id)).Status);
        Assert.Equal(BackupRunStatus.Failed, (await fixture.Db.Backups.SingleAsync(x => x.Id == failed.Id)).Status);
    }

    [Fact]
    public async Task Retention_keeps_full_parent_while_dependent_incremental_is_retained()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync(retentionMinutes: null);
        policy.FullRetentionMinutes = 1;
        policy.IncrementalRetentionMinutes = 120;
        await fixture.Db.SaveChangesAsync();
        var full = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-2), tableName: "orders");
        var incremental = await fixture.SeedDependentIncrementalAsync(policy.Id, full, now.AddMinutes(-10));

        await fixture.Services.GetRequiredService<RetentionManagementBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == full.Id)).Status);
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == incremental.Id)).Status);
    }

    [Fact]
    public async Task Retention_keeps_incremental_run_that_owns_full_fallback_table_while_dependent_incremental_is_retained()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync(retentionMinutes: null);
        policy.FullRetentionMinutes = 1;
        policy.IncrementalRetentionMinutes = 120;
        await fixture.Db.SaveChangesAsync();

        var originalFull = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-3), tableName: "orders");
        var fallbackRun = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true),
            new SeedBackupTable("sales", "new_orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        fallbackRun.PolicyId = policy.Id;
        fallbackRun.Status = BackupRunStatus.Succeeded;
        fallbackRun.BackupType = BackupType.Incremental;
        fallbackRun.CompletedAt = now.AddHours(-2);
        fallbackRun.CreatedAt = now.AddHours(-2).AddMinutes(-1);
        var originalFullTable = await fixture.Db.BackupTables.Include(x => x.Shards).SingleAsync(x => x.BackupId == originalFull.Id);
        foreach (var table in fallbackRun.Tables)
        {
            if (table.Table == "orders")
            {
                table.EffectiveBackupType = BackupType.Incremental;
                table.ParentFullBackupId = originalFull.Id;
                table.ParentFullBackupTableId = originalFullTable.Id;
                foreach (var shard in table.Shards)
                {
                    shard.EffectiveBackupType = BackupType.Incremental;
                    shard.ParentFullBackupId = originalFull.Id;
                    shard.ParentFullBackupTableShardId = originalFullTable.Shards.Single().Id;
                }
            }
            else
            {
                table.EffectiveBackupType = BackupType.Full;
                foreach (var shard in table.Shards)
                {
                    shard.EffectiveBackupType = BackupType.Full;
                }
            }
        }
        await fixture.Db.SaveChangesAsync();

        var fallbackFullTable = fallbackRun.Tables.Single(x => x.Table == "new_orders");
        var laterIncremental = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddMinutes(-10), backupType: BackupType.Incremental, tableName: "new_orders");
        var laterTable = laterIncremental.Tables.Single();
        laterTable.EffectiveBackupType = BackupType.Incremental;
        laterTable.ParentFullBackupId = fallbackRun.Id;
        laterTable.ParentFullBackupTableId = fallbackFullTable.Id;
        laterTable.Shards.Single().EffectiveBackupType = BackupType.Incremental;
        laterTable.Shards.Single().ParentFullBackupId = fallbackRun.Id;
        laterTable.Shards.Single().ParentFullBackupTableShardId = fallbackFullTable.Shards.Single().Id;
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<RetentionManagementBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == fallbackRun.Id)).Status);
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == laterIncremental.Id)).Status);
    }

    [Fact]
    public async Task Retention_deletes_expired_incremental_before_full_parent()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync(retentionMinutes: null);
        policy.FullRetentionMinutes = 1;
        policy.IncrementalRetentionMinutes = 1;
        await fixture.Db.SaveChangesAsync();
        var full = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-3), tableName: "orders");
        var incremental = await fixture.SeedDependentIncrementalAsync(policy.Id, full, now.AddHours(-2));

        await fixture.Services.GetRequiredService<RetentionManagementBackgroundService>().RunOnceAsync();
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == full.Id)).Status);
        Assert.Equal(BackupRunStatus.BackupExpiredDeleted, (await fixture.Db.Backups.SingleAsync(x => x.Id == incremental.Id)).Status);

        await fixture.Services.GetRequiredService<RetentionManagementBackgroundService>().RunOnceAsync();
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(BackupRunStatus.BackupExpiredDeleted, (await fixture.Db.Backups.SingleAsync(x => x.Id == full.Id)).Status);
    }

    [Fact]
    public async Task Retention_minimums_protect_mixed_backups_and_full_backups_independently()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync(retentionMinutes: null);
        policy.FullRetentionMinutes = 1;
        policy.IncrementalRetentionMinutes = 1;
        policy.MinBackupsToKeep = 1;
        policy.MinFullBackupsToKeep = 1;
        await fixture.Db.SaveChangesAsync();
        var oldFull = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-4));
        var protectedFull = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-3));
        var oldIncremental = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-2), backupType: BackupType.Incremental);
        var protectedNewest = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-1), backupType: BackupType.Incremental);

        await fixture.Services.GetRequiredService<RetentionManagementBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(BackupRunStatus.BackupExpiredDeleted, (await fixture.Db.Backups.SingleAsync(x => x.Id == oldFull.Id)).Status);
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == protectedFull.Id)).Status);
        Assert.Equal(BackupRunStatus.BackupExpiredDeleted, (await fixture.Db.Backups.SingleAsync(x => x.Id == oldIncremental.Id)).Status);
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == protectedNewest.Id)).Status);
    }

    [Fact]
    public async Task Manual_delete_of_full_parent_is_blocked_by_pinned_incremental_unless_forced()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var policy = await fixture.SeedPolicyAsync();
        var full = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, DateTimeOffset.UtcNow.AddHours(-2), tableName: "orders");
        var incremental = await fixture.SeedDependentIncrementalAsync(policy.Id, full, DateTimeOffset.UtcNow.AddHours(-1), isPinned: true);

        var service = fixture.Services.GetRequiredService<BackupApplicationService>();
        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestDeleteAsync(full.Id, confirmDestructive: true));

        await service.RequestDeleteAsync(full.Id, force: true, confirmDestructive: true);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(BackupRunStatus.ManualDeleteRequested, (await fixture.Db.Backups.SingleAsync(x => x.Id == full.Id)).Status);
        Assert.Equal(BackupRunStatus.ManualDeleteRequested, (await fixture.Db.Backups.SingleAsync(x => x.Id == incremental.Id)).Status);
    }

    [Fact]
    public async Task Cleanup_retries_after_failed_attempt_and_completes_after_restart()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync(retentionMinutes: 60, minBackupsToKeep: 0);
        var backup = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-5));
        fixture.StorageDeletion.FailNextDelete = true;

        await fixture.Services.GetRequiredService<RetentionManagementBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        var afterFailure = await fixture.Db.Backups.SingleAsync(x => x.Id == backup.Id);
        Assert.Equal(BackupRunStatus.BackupExpiredDeleteStarted, afterFailure.Status);
        Assert.NotNull(afterFailure.DeletionError);
        Assert.Equal(1, afterFailure.DeletionAttemptCount);

        await fixture.Services.GetRequiredService<RetentionManagementBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Db.Backups.SingleAsync(x => x.Id == backup.Id);
        Assert.Equal(BackupRunStatus.BackupExpiredDeleted, completed.Status);
        Assert.Null(completed.DeletionError);
        Assert.Equal(2, completed.DeletionAttemptCount);
    }

    [Fact]
    public async Task Garbage_collector_cleans_failed_backups_only_when_policy_opts_in()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var cleanupPolicy = await fixture.SeedPolicyAsync(failedMode: FailedBackupRetentionMode.DeleteByGarbageCollectorAfterFailure);
        var keepPolicy = await fixture.SeedPolicyAsync(name: "keep-failures");
        var failed = await fixture.SeedPolicyBackupAsync(cleanupPolicy.Id, BackupRunStatus.Failed, now.AddMinutes(-30));
        var partial = await fixture.SeedPolicyBackupAsync(cleanupPolicy.Id, BackupRunStatus.PartiallySucceeded, now.AddMinutes(-20));
        var kept = await fixture.SeedPolicyBackupAsync(keepPolicy.Id, BackupRunStatus.Failed, now.AddMinutes(-10));

        await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(BackupRunStatus.FailedBackupDeletedByGarbageCollector, (await fixture.Db.Backups.SingleAsync(x => x.Id == failed.Id)).Status);
        Assert.Equal(BackupRunStatus.FailedBackupDeletedByGarbageCollector, (await fixture.Db.Backups.SingleAsync(x => x.Id == partial.Id)).Status);
        Assert.Equal(BackupRunStatus.Failed, (await fixture.Db.Backups.SingleAsync(x => x.Id == kept.Id)).Status);
    }

    [Fact]
    public async Task Garbage_collector_completes_manual_delete_requests()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync();
        var backup = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.ManualDeleteRequested, now.AddMinutes(-30));
        backup.DeletionReason = "manual";
        backup.DeletionRequestedAt = now.AddMinutes(-20);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Db.Backups.SingleAsync(x => x.Id == backup.Id);
        Assert.Equal(BackupRunStatus.ManualDeleted, completed.Status);
        Assert.NotNull(completed.DeletedAt);
        Assert.Equal(1, result.LastPendingCleanupCount);
        Assert.Equal(1, result.LastCleanedCount);
        Assert.Contains(backup.Tables.Single().S3Path, fixture.StorageDeletion.DeletedDirectories);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "backup-cleanup-succeeded" && x.EntityId == backup.Id.ToString()));
    }

    [Fact]
    public async Task Garbage_collector_deletes_failed_incremental_without_deleting_parent_full()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync(failedMode: FailedBackupRetentionMode.DeleteByGarbageCollectorAfterFailure);
        var full = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-2), tableName: "orders");
        var incremental = await fixture.SeedDependentIncrementalAsync(policy.Id, full, now.AddHours(-1));
        incremental.Status = BackupRunStatus.Failed;
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == full.Id)).Status);
        Assert.Equal(BackupRunStatus.FailedBackupDeletedByGarbageCollector, (await fixture.Db.Backups.SingleAsync(x => x.Id == incremental.Id)).Status);
    }

    [Fact]
    public async Task Garbage_collector_deletes_failed_full_parent_and_dependent_incrementals()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync(failedMode: FailedBackupRetentionMode.DeleteByGarbageCollectorAfterFailure);
        var full = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-2), tableName: "orders");
        var incremental = await fixture.SeedDependentIncrementalAsync(policy.Id, full, now.AddHours(-1));
        full.Status = BackupRunStatus.Failed;
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(BackupRunStatus.FailedBackupDeletedByGarbageCollector, (await fixture.Db.Backups.SingleAsync(x => x.Id == full.Id)).Status);
        Assert.Equal(BackupRunStatus.FailedBackupDeletedByGarbageCollector, (await fixture.Db.Backups.SingleAsync(x => x.Id == incremental.Id)).Status);
    }

    [Fact]
    public async Task Garbage_collector_deletes_orphaned_incremental_after_parent_deletion()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync();
        var full = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.ManualDeleted, now.AddHours(-2), tableName: "orders");
        var incremental = await fixture.SeedDependentIncrementalAsync(policy.Id, full, now.AddHours(-1));

        await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(BackupRunStatus.FailedBackupDeletedByGarbageCollector, (await fixture.Db.Backups.SingleAsync(x => x.Id == incremental.Id)).Status);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "orphaned-incremental-garbage-collection-requested" && x.EntityId == incremental.Id.ToString()));
    }

    [Fact]
    public async Task Backup_cancel_marks_run_canceled_kills_queries_and_garbage_collector_cleans_remains()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Running, "backup-op-1", true)
        ]);
        backup.Status = BackupRunStatus.Running;
        await fixture.Db.SaveChangesAsync();

        var canceled = await fixture.Services.GetRequiredService<BackupApplicationService>().CancelAsync(backup.Id);

        Assert.NotNull(canceled);
        Assert.Equal(BackupRunStatus.Canceled, canceled.Status);
        Assert.Contains("backup-op-1", fixture.ClickHouse.KilledQueries);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "canceled" && x.EntityType == "backup" && x.EntityId == backup.Id.ToString()));

        await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        var afterCleanup = await fixture.Db.Backups.SingleAsync(x => x.Id == backup.Id);
        Assert.Equal(BackupRunStatus.Canceled, afterCleanup.Status);
        Assert.NotNull(afterCleanup.DeletedAt);
        Assert.Contains(backup.Tables.Single().S3Path, fixture.StorageDeletion.DeletedDirectories);
    }

    [Fact]
    public async Task Restore_cancel_marks_run_canceled_and_kills_queries()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "orders", RestoreTableStatus.Running, "restore-op-1")
        ]);
        restore.Status = RestoreRunStatus.Running;
        await fixture.Db.SaveChangesAsync();

        var canceled = await fixture.Services.GetRequiredService<RestoreApplicationService>().CancelAsync(restore.Id);

        Assert.NotNull(canceled);
        Assert.Equal(RestoreRunStatus.Canceled, canceled.Status);
        Assert.Contains("restore-op-1", fixture.ClickHouse.KilledQueries);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "canceled" && x.EntityType == "restore" && x.EntityId == restore.Id.ToString()));
    }

    [Fact]
    public async Task Restore_rejects_deleted_or_delete_pending_backups()
    {
        await using var fixture = await TestFixture.CreateAsync();
        foreach (var status in new[]
                 {
                     BackupRunStatus.ManualDeleteRequested,
                     BackupRunStatus.ManualDeleted,
                     BackupRunStatus.FailedBackupDeleteRequested,
                     BackupRunStatus.FailedBackupDeletedByGarbageCollector,
                     BackupRunStatus.BackupExpiredDeleteStarted,
                     BackupRunStatus.BackupExpiredDeleted
                 })
        {
            var backup = await fixture.SeedPolicyBackupAsync(null, status, DateTimeOffset.UtcNow.AddMinutes(-1));
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(backup.Id, fixture.TargetClusterId, null, null, null, null, false, false)));
        }
    }

    [Fact]
    public async Task Restore_with_schema_mismatch_allowed_requires_destructive_confirmation()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            "restored",
            "orders_copy",
            false,
            true)));

        Assert.Contains("ConfirmDestructive=true", error.Message);
        Assert.Contains("Schema mismatch", error.Message);
        Assert.False(await fixture.Db.Restores.AnyAsync());
    }

    [Fact]
    public async Task Restore_to_existing_target_table_requires_destructive_confirmation()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false)));

        Assert.Contains("ConfirmDestructive=true", error.Message);
        Assert.Contains("already exists", error.Message);
        Assert.False(await fixture.Db.Restores.AnyAsync());
    }
    [Fact]
    public async Task Restore_can_select_multiple_tables_with_target_mappings()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true),
            new SeedBackupTable("sales", "items", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        var tables = backup.Tables.OrderBy(x => x.Table).ToList();

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false,
            Tables: [
                new RestoreTableMappingRequest(tables[0].Id, "restored", "items_copy"),
                new RestoreTableMappingRequest(tables[1].Id, "restored", "orders_copy")
            ]));

        Assert.Equal(2, restore.Tables.Count);
        Assert.Contains(restore.Tables, x => x.SourceTable == "items" && x.TargetDatabase == "restored" && x.TargetTable == "items_copy");
        Assert.Contains(restore.Tables, x => x.SourceTable == "orders" && x.TargetDatabase == "restored" && x.TargetTable == "orders_copy");
    }

    [Fact]
    public async Task Restore_can_select_multiple_source_shards()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "shard-1", 1, "restore-s1", 9000, false, 0),
            new ClickHouseShardReplicaInfo(2, "shard-2", 1, "restore-s2", 9000, false, 0)
        ]);
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 3);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false,
            Layout: RestoreLayout.Redistribute,
            SourceShards: [1, 3]));

        var table = Assert.Single(restore.Tables);
        Assert.Equal([1, 3], table.Shards.Select(x => x.SourceShardNumber).Order().ToArray());
    }

    [Fact]
    public async Task Restore_preserve_allows_different_cluster_when_selected_source_shards_exist_on_target()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "shard-1", 1, "restore-s1", 9000, false, 0),
            new ClickHouseShardReplicaInfo(2, "shard-2", 1, "restore-s2", 9000, false, 0),
            new ClickHouseShardReplicaInfo(3, "shard-3", 1, "restore-s3", 9000, false, 0)
        ]);
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 3);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false,
            Layout: RestoreLayout.Preserve,
            SourceShards: [1, 3]));

        var table = Assert.Single(restore.Tables);
        Assert.Equal([1, 3], table.Shards.OrderBy(x => x.SourceShardNumber).Select(x => x.TargetShardNumber).ToArray());
    }

    [Fact]
    public async Task Restore_preserve_rejects_different_cluster_missing_selected_source_shard()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "shard-1", 1, "restore-s1", 9000, false, 0),
            new ClickHouseShardReplicaInfo(2, "shard-2", 1, "restore-s2", 9000, false, 0)
        ]);
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 3);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false,
            Layout: RestoreLayout.Preserve,
            SourceShards: [1, 3])));

        Assert.Contains("Preserve layout requires target shard 3", error.Message);
    }

    [Fact]
    public async Task Restore_redistribute_can_limit_target_shards_to_one_shard()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "shard-1", 1, "restore-s1", 9000, false, 0),
            new ClickHouseShardReplicaInfo(2, "shard-2", 1, "restore-s2", 9000, false, 0),
            new ClickHouseShardReplicaInfo(3, "shard-3", 1, "restore-s3", 9000, false, 0)
        ]);
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 4);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false,
            Layout: RestoreLayout.Redistribute,
            TargetShards: [2]));

        var table = Assert.Single(restore.Tables);
        Assert.Equal([2, 2, 2, 2], table.Shards.OrderBy(x => x.SourceShardNumber).Select(x => x.TargetShardNumber).ToArray());
        Assert.All(table.Shards, shard => Assert.Equal("restore-s2", shard.TargetHost));
    }

    [Fact]
    public async Task Restore_redistribute_uses_selected_target_shard_pool()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "shard-1", 1, "restore-s1", 9000, false, 0),
            new ClickHouseShardReplicaInfo(2, "shard-2", 1, "restore-s2", 9000, false, 0),
            new ClickHouseShardReplicaInfo(3, "shard-3", 1, "restore-s3", 9000, false, 0)
        ]);
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 4);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false,
            Layout: RestoreLayout.Redistribute,
            TargetShards: [2, 3]));

        var table = Assert.Single(restore.Tables);
        Assert.Equal([2, 3, 2, 3], table.Shards.OrderBy(x => x.SourceShardNumber).Select(x => x.TargetShardNumber).ToArray());
    }

    [Fact]
    public async Task Restore_redistribute_target_subset_creates_target_table_only_on_selected_target_shards()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "shard-1", 1, "restore-s1", 9000, false, 0),
            new ClickHouseShardReplicaInfo(2, "shard-2", 1, "restore-s2", 9000, false, 0),
            new ClickHouseShardReplicaInfo(3, "shard-3", 1, "restore-s3", 9000, false, 0)
        ]);
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 3);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false,
            Layout: RestoreLayout.Redistribute,
            TargetShards: [1, 2]));

        await fixture.RunRestoreAsync(restore.Id);

        var createTableHosts = fixture.ClickHouse.EndpointExecuteSql
            .Where(x => x.Sql.Contains("CREATE TABLE IF NOT EXISTS `sales`.`orders`", StringComparison.Ordinal))
            .Select(x => x.Endpoint.Host)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(["restore-s1", "restore-s2"], createTableHosts);
    }

    [Fact]
    public async Task Restore_rejects_duplicate_target_shards()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "shard-1", 1, "restore-s1", 9000, false, 0),
            new ClickHouseShardReplicaInfo(2, "shard-2", 1, "restore-s2", 9000, false, 0)
        ]);
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 2);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false,
            Layout: RestoreLayout.Redistribute,
            TargetShards: [2, 2])));
        Assert.Contains("TargetShards must not contain duplicates", error.Message);
    }

    [Fact]
    public async Task Restore_table_mappings_can_override_table_options()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true),
            new SeedBackupTable("sales", "items", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        var tables = backup.Tables.OrderBy(x => x.Table).ToList();

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false,
            Tables: [
                new RestoreTableMappingRequest(tables[0].Id, "restored", "items_schema", SchemaOnly: true),
                new RestoreTableMappingRequest(tables[1].Id, "restored", "orders_append", Append: true, AllowSchemaMismatch: true)
            ],
            ConfirmDestructive: true));

        var schemaOnly = Assert.Single(restore.Tables, x => x.SourceTable == "items");
        Assert.True(schemaOnly.SchemaOnly);
        Assert.False(schemaOnly.Append);
        Assert.Empty(schemaOnly.Shards);
        var append = Assert.Single(restore.Tables, x => x.SourceTable == "orders");
        Assert.False(append.SchemaOnly);
        Assert.True(append.Append);
        Assert.True(append.AllowSchemaMismatch);
        Assert.NotEmpty(append.Shards);
    }

    [Fact]
    public async Task Restore_schema_only_creates_tables_without_submitting_restore_operations()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            "restored",
            "orders_schema",
            false,
            false,
            SchemaOnly: true));
        await fixture.RunRestoreAsync(restore.Id);

        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == restore.Id);
        Assert.Equal(RestoreRunStatus.Succeeded, completed.Status);
        var table = Assert.Single(completed.Tables);
        Assert.Equal(RestoreTableStatus.Succeeded, table.Status);
        Assert.True(table.SchemaOnly);
        Assert.Empty(table.Shards);
        Assert.Empty(fixture.ClickHouse.RestoreStartTables);
        Assert.Contains(fixture.ClickHouse.ExecuteSql, sql => sql.Contains("CREATE TABLE IF NOT EXISTS `restored`.`orders_schema`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Schema_definitions_are_reused_by_hash()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var identicalColumns = """[{"name":"id","type":"UInt64","defaultKind":"","defaultExpression":""}]""";
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders_a", "MergeTree", identicalColumns, "same-hash"));
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders_b", "MergeTree", identicalColumns, "same-hash"));

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Db.SchemaDefinitions.CountAsync());
        var schemaIds = await fixture.Db.BackupTables.Select(x => x.SchemaDefinitionId).Distinct().ToListAsync();
        Assert.Single(schemaIds);
    }

    [Fact]
    public async Task Backup_audit_events_use_backup_operation_id_and_keep_clickhouse_operation_id_separate()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var table = await fixture.Db.BackupTables.Include(x => x.Shards).SingleAsync(x => x.BackupId == backupId);
        var shard = Assert.Single(table.Shards);
        Assert.False(string.IsNullOrWhiteSpace(table.ClickHouseOperationId));
        Assert.False(string.IsNullOrWhiteSpace(shard.ClickHouseOperationId));

        var submittedAudit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "clickhouse-operation-submitted" && x.EntityType == "backup-table-shard" && x.EntityId == shard.Id.ToString());
        var shardSucceededAudit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "shard-succeeded" && x.EntityType == "backup-table-shard" && x.EntityId == shard.Id.ToString());
        var tableSucceededAudit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "table-succeeded" && x.EntityType == "backup-table" && x.EntityId == table.Id.ToString());

        AssertAuditCorrelation(submittedAudit, backupId, shard.ClickHouseOperationId);
        AssertAuditCorrelation(shardSucceededAudit, backupId, shard.ClickHouseOperationId);
        AssertAuditCorrelation(tableSucceededAudit, backupId, table.ClickHouseOperationId);

        var operationAudits = await new AuditStore(fixture.Db).QueryAsync(null, null, null, limit: 500, operationId: backupId.ToString());
        Assert.Contains(operationAudits.Items, x => x.Action == "clickhouse-operation-submitted");
        Assert.Contains(operationAudits.Items, x => x.Action == "shard-succeeded");
        Assert.Contains(operationAudits.Items, x => x.Action == "table-succeeded");
        Assert.All(operationAudits.Items, x => Assert.Equal(backupId.ToString(), x.Details.GetProperty("operationId").GetString()));
    }

    [Fact]
    public async Task Operation_id_is_persisted_before_backup_polling_finishes()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.BlockOperationStatus = true;

        var backupId = await fixture.CreateManualBackupAsync();
        var runTask = fixture.RunBackupAsync(backupId);
        await fixture.ClickHouse.WaitForBlockedStatusAsync();

        var table = await fixture.Db.BackupTables.SingleAsync(x => x.BackupId == backupId);
        Assert.False(string.IsNullOrWhiteSpace(table.ClickHouseOperationId));
        Assert.Equal(BackupTableStatus.Running, table.Status);

        fixture.ClickHouse.ReleaseBlockedStatus();
        await runTask;
    }

    [Fact]
    public async Task Backup_source_failure_finishes_with_audited_failure_reason()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.GetTablesException = new InvalidOperationException("Source ClickHouse instance is not reachable at source:9000.");

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.SingleAsync(x => x.Id == backupId);
        Assert.Equal(BackupRunStatus.Failed, backup.Status);
        Assert.Contains("Source ClickHouse instance is not reachable", backup.FailureReason);
        Assert.NotNull(backup.CompletedAt);
        var backupDto = BackupRestoreMappingAccessor.ToDto(backup);
        Assert.Equal(backup.CompletedAt, backupDto.EndedAt);
        var backupJson = SerializeRunDto(backupDto);
        AssertRunJsonUsesEndedAt(backupJson);
        var audit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "failed" && x.EntityType == "backup" && x.EntityId == backupId.ToString());
        Assert.Contains("Source ClickHouse instance is not reachable", audit.Details);
    }

    [Fact]
    public async Task Backup_storage_failure_is_correlated_on_sharded_run_table_and_dashboard()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "shard-1", 1, "source-s1", 9000, false, 0),
            new ClickHouseShardReplicaInfo(2, "shard-2", 1, "source-s2", 9000, false, 0)
        ]);
        fixture.ClickHouse.NextBackupStatus = "BACKUP_FAILED";
        fixture.ClickHouse.NextBackupError = "S3 target http://minio:9000 is unavailable while writing backups/sales/orders.";

        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        var backupId = await fixture.CreateManualBackupAsync();
        var backup = await fixture.Db.Backups.SingleAsync(x => x.Id == backupId);
        backup.PolicyId = schedule.PolicyId;
        backup.ScheduleId = schedule.Id;
        await fixture.Db.SaveChangesAsync();

        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        backup = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backupId);
        Assert.Equal(BackupRunStatus.Failed, backup.Status);
        Assert.Contains("sales.orders shard 1", backup.FailureReason);
        Assert.Contains("sales.orders shard 2", backup.FailureReason);
        Assert.Contains("minio:9000", backup.FailureReason);
        Assert.Contains("Shard 1", Assert.Single(backup.Tables).Error);
        Assert.Contains("Shard 2", Assert.Single(backup.Tables).Error);
        Assert.All(backup.Tables.SelectMany(x => x.Shards), shard => Assert.Contains("minio:9000", shard.Error));

        var dashboard = await fixture.Services.GetRequiredService<DashboardApplicationService>().GetDashboardAsync();
        var summary = Assert.Single(dashboard.Schedules, x => x.ScheduleId == schedule.Id);
        Assert.Equal(BackupRunStatus.Failed, summary.LastRunStatus);
        Assert.Contains("minio:9000", summary.LastRunFailureReason);

        var audit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "failed" && x.EntityType == "backup" && x.EntityId == backupId.ToString());
        Assert.Contains("minio:9000", audit.Details);
    }

    [Fact]
    public async Task Backup_missing_clickhouse_tracking_failure_is_not_left_running()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.DropStartedBackupOperation = true;

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backupId);
        Assert.Equal(BackupRunStatus.Failed, backup.Status);
        Assert.Contains("missing from system.backups", backup.FailureReason);
        Assert.Equal(BackupTableStatus.Failed, Assert.Single(backup.Tables).Status);
    }

    [Fact]
    public async Task Restore_target_failure_finishes_with_failure_reason()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "orders", RestoreTableStatus.Queued, null)
        ]);
        fixture.ClickHouse.ExecuteException = new InvalidOperationException("Destination ClickHouse instance is not reachable at restore:9000.");

        await fixture.RunRestoreAsync(restore.Id);

        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Db.Restores.Include(x => x.Tables).SingleAsync(x => x.Id == restore.Id);
        Assert.Equal(RestoreRunStatus.Failed, completed.Status);
        Assert.NotNull(completed.CompletedAt);
        var restoreDto = BackupRestoreMappingAccessor.ToDto(completed);
        Assert.Equal(completed.CompletedAt, restoreDto.EndedAt);
        var restoreJson = SerializeRunDto(restoreDto);
        AssertRunJsonUsesEndedAt(restoreJson);
        Assert.Contains("Destination ClickHouse instance is not reachable", completed.FailureReason);
        Assert.Contains("sales.orders", completed.FailureReason);
        var audit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "failed" && x.EntityType == "restore" && x.EntityId == restore.Id.ToString());
        Assert.Contains("Destination ClickHouse instance is not reachable", audit.Details);
    }

    [Fact]
    public async Task Restore_table_failure_marks_pending_shards_failed()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 3);
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "orders", RestoreTableStatus.Queued, null)
        ]);
        fixture.ClickHouse.ExecuteException = new InvalidOperationException("Destination ClickHouse timed out before restore submission.");

        await fixture.RunRestoreAsync(restore.Id);

        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == restore.Id);
        var table = Assert.Single(completed.Tables);
        Assert.Equal(RestoreTableStatus.Failed, table.Status);
        Assert.All(table.Shards, shard =>
        {
            Assert.Equal(RestoreTableStatus.Failed, shard.Status);
            Assert.Contains("Destination ClickHouse timed out", shard.Error);
            Assert.NotNull(shard.CompletedAt);
        });
    }

    [Fact]
    public async Task Restore_storage_or_credential_failure_is_correlated_on_sharded_restore()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 2);
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "orders", RestoreTableStatus.Queued, null)
        ]);
        fixture.ClickHouse.NextRestoreStatus = "RESTORE_FAILED";
        fixture.ClickHouse.NextRestoreError = "S3 credentials were rejected by backup target minio.";

        await fixture.RunRestoreAsync(restore.Id);

        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == restore.Id);
        Assert.Equal(RestoreRunStatus.Failed, completed.Status);
        Assert.Contains("source shard 1", completed.FailureReason);
        Assert.Contains("source shard 2", completed.FailureReason);
        Assert.Contains("S3 credentials were rejected", completed.FailureReason);
        Assert.Contains("Source shard 1", Assert.Single(completed.Tables).Error);
        Assert.Contains("Source shard 2", Assert.Single(completed.Tables).Error);
        Assert.All(completed.Tables.SelectMany(x => x.Shards), shard => Assert.Contains("S3 credentials were rejected", shard.Error));
        var audit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "failed" && x.EntityType == "restore" && x.EntityId == restore.Id.ToString());
        Assert.Contains("S3 credentials were rejected", audit.Details);
    }

    [Fact]
    public async Task Manual_backup_metadata_is_persisted_and_mapped()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backupId = await fixture.CreateManualBackupAsync(PolicySelector.Empty, actorName: "operator", actorUserId: Guid.NewGuid());
        var dto = BackupRestoreMappingAccessor.ToDto(await fixture.Db.Backups.Include(x => x.Tables).SingleAsync(x => x.Id == backupId));

        Assert.Null(dto.PolicyId);
        Assert.Equal("operator", dto.RequestedByName);
        Assert.NotNull(dto.RequestedByUserId);
        Assert.Contains("\"clusterId\"", dto.ManualRequestJson);
        Assert.Equal(fixture.SourceClusterId, dto.SourceClusterId);
        Assert.Equal(fixture.TargetId, dto.TargetId);
        Assert.True(dto.CreatedAt <= DateTimeOffset.UtcNow);
    }

    private static async Task<TimeSpan> MeasureAsync(Func<Task> action)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }
    private static ClickHouseTableInfo Table(string database, string table, string engine, string columnsJson = "[]", string? schemaHash = null, string? createSql = null) =>
        new(database, table, engine, createSql ?? $"CREATE TABLE {database}.{table} (id UInt64) ENGINE = {engine} ORDER BY id", columnsJson, schemaHash ?? $"{database}.{table}.{engine}.{columnsJson}");

    private static void AssertAuditCorrelation(AuditEntryEntity entry, Guid backupId, string? clickHouseOperationId)
    {
        Assert.Equal(backupId.ToString(), entry.OperationId);
        using var document = JsonDocument.Parse(entry.Details);
        Assert.Equal(backupId.ToString(), document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal(clickHouseOperationId, document.RootElement.GetProperty("clickHouseOperationId").GetString());
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string SerializeRunDto<T>(T dto)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(dto, options);
    }

    private static void AssertRunJsonUsesEndedAt(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("endedAt", out var endedAt));
        Assert.NotEqual(JsonValueKind.Null, endedAt.ValueKind);
        Assert.False(document.RootElement.TryGetProperty("completedAt", out _));
    }

    private sealed record SeedBackupTable(string Database, string Table, BackupTableStatus Status, string? OperationId, bool DataBackedUp);

    private sealed record SeedRestoreTable(string SourceDatabase, string SourceTable, RestoreTableStatus Status, string? OperationId);

    private sealed class FakeClickHouseAdapter : IClickHouseAdapter
    {
        private readonly object _lock = new();
        private int _activeBackupStarts;
        public List<ClickHouseTableInfo> Inventory { get; } = [];
        public List<ClickHouseShardReplicaInfo> Topology { get; } = [new(1, "single", 1, "source", 9000, false, 0)];
        public Dictionary<string, string> KnownOperations { get; } = new(StringComparer.Ordinal);
        public List<string> BackupStartTables { get; } = [];
        public List<string?> BackupBasePaths { get; } = [];
        public List<string> RestoreStartTables { get; } = [];
        public List<string> ExecuteSql { get; } = [];
        public List<ClickHouseNodeEndpoint> GetTablesEndpoints { get; } = [];
        public int GetTablesCallCount { get; private set; }
        public Dictionary<string, List<ClickHouseTableInfo>> InventoryByEndpoint { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> KilledQueries { get; } = [];
        public List<ClickHouseNodeEndpoint> ExecuteEndpoints { get; } = [];
        public List<(ClickHouseNodeEndpoint Endpoint, string Sql)> EndpointExecuteSql { get; } = [];
        public int MaxConcurrentBackupStarts { get; private set; }
        public TimeSpan StartDelay { get; set; }
        public bool BlockOperationStatus { get; set; }
        public Exception? GetTablesException { get; set; }
        public Exception? ExecuteException { get; set; }
        public string NextBackupStatus { get; set; } = "BACKUP_CREATED";
        public string? NextBackupError { get; set; }
        public string NextRestoreStatus { get; set; } = "RESTORED";
        public string? NextRestoreError { get; set; }
        public bool DropStartedBackupOperation { get; set; }
        public bool DropStartedRestoreOperation { get; set; }
        public Dictionary<string, string> OperationErrors { get; } = new(StringComparer.Ordinal);
        private TaskCompletionSource _statusBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _releaseStatus = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ClickHouseTableInfo>> GetTablesAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken)
        {
            GetTablesCallCount++;
            if (GetTablesException is not null)
            {
                throw GetTablesException;
            }

            return Task.FromResult<IReadOnlyList<ClickHouseTableInfo>>(Inventory.ToList());
        }


        public Task<IReadOnlyList<ClickHouseTableInfo>> GetTablesAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, CancellationToken cancellationToken)
        {
            GetTablesEndpoints.Add(endpoint);
            if (InventoryByEndpoint.TryGetValue(EndpointKey(endpoint), out var inventory))
            {
                return Task.FromResult<IReadOnlyList<ClickHouseTableInfo>>(inventory.ToList());
            }

            return GetTablesAsync(cluster, cancellationToken);
        }

        private static string EndpointKey(ClickHouseNodeEndpoint endpoint) => $"{endpoint.Host}:{endpoint.Port}:{endpoint.UseTls}";

        public Task<IReadOnlyList<string>> GetClusterNamesAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken) =>Task.FromResult<IReadOnlyList<string>>(["test_cluster"]);

        public Task<ClickHouseTableInfo?> GetTableAsync(ClickHouseClusterEntity cluster, string database, string table, CancellationToken cancellationToken) =>
            Task.FromResult(Inventory.FirstOrDefault(x => x.Database == database && x.Table == table));

        public Task<ClickHouseTableInfo?> GetTableAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string database, string table, CancellationToken cancellationToken) =>
            Task.FromResult((InventoryByEndpoint.TryGetValue(EndpointKey(endpoint), out var inventory) ? inventory : Inventory).FirstOrDefault(x => x.Database == database && x.Table == table));

        public Task ExecuteAsync(ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken)
        {
            if (ExecuteException is not null)
            {
                throw ExecuteException;
            }

            ExecuteSql.Add(sql);
            return Task.CompletedTask;
        }

        public Task ExecuteAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken)
        {
            if (ExecuteException is not null)
            {
                throw ExecuteException;
            }

            ExecuteSql.Add(sql);
            ExecuteEndpoints.Add(endpoint);
            EndpointExecuteSql.Add((endpoint, sql));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ClickHouseShardReplicaInfo>> GetTopologyAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClickHouseShardReplicaInfo>>(Topology.ToList());

        public async Task<ClickHouseOperationResult> StartBackupAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, string? baseBackupPath, CancellationToken cancellationToken)
        {
            var shard = new BackupTableShardEntity { SourceShardNumber = 1, S3Path = table.S3Path };
            return await StartBackupShardAsync(new ClickHouseNodeEndpoint("source", 9000, false), cluster, target, table, shard, baseBackupPath, cancellationToken);
        }

        public async Task<ClickHouseOperationResult> StartBackupShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, BackupTableShardEntity shard, string? baseBackupPath, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _activeBackupStarts++;
                MaxConcurrentBackupStarts = Math.Max(MaxConcurrentBackupStarts, _activeBackupStarts);
                BackupStartTables.Add(table.Table);
                BackupBasePaths.Add(baseBackupPath);
            }

            if (StartDelay > TimeSpan.Zero)
            {
                await Task.Delay(StartDelay, cancellationToken);
            }

            lock (_lock)
            {
                _activeBackupStarts--;
            }

            var operationId = $"backup-{table.Table}-s{shard.SourceShardNumber}-{Guid.NewGuid():N}";
            lock (_lock)
            {
                if (!DropStartedBackupOperation)
                {
                    KnownOperations[operationId] = NextBackupStatus;
                    if (!string.IsNullOrWhiteSpace(NextBackupError))
                    {
                        OperationErrors[operationId] = NextBackupError;
                    }
                }
            }
            return new ClickHouseOperationResult(operationId, "CREATING_BACKUP");
        }

        public Task<ClickHouseOperationResult> StartRestoreAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, RestoreTableEntity table, BackupTableEntity backupTable, CancellationToken cancellationToken)
        {
            var shard = new RestoreTableShardEntity { SourceShardNumber = 1, RestoreDatabase = table.TargetDatabase, RestoreTableName = table.TargetTable };
            var backupShard = new BackupTableShardEntity { SourceShardNumber = 1, S3Path = backupTable.S3Path };
            return StartRestoreShardAsync(new ClickHouseNodeEndpoint("restore", 9000, false), cluster, target, shard, backupTable, backupShard, cancellationToken);
        }

        public Task<ClickHouseOperationResult> StartRestoreShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, RestoreTableShardEntity table, BackupTableEntity backupTable, BackupTableShardEntity backupShard, CancellationToken cancellationToken)
        {
            string operationId;
            lock (_lock)
            {
                RestoreStartTables.Add(backupTable.Table);
                operationId = $"restore-{backupTable.Table}-s{backupShard.SourceShardNumber}-{Guid.NewGuid():N}";
                if (!DropStartedRestoreOperation)
                {
                    KnownOperations[operationId] = NextRestoreStatus;
                    if (!string.IsNullOrWhiteSpace(NextRestoreError))
                    {
                        OperationErrors[operationId] = NextRestoreError;
                    }
                }
            }
            return Task.FromResult(new ClickHouseOperationResult(operationId, "RESTORING"));
        }

        public async Task<ClickHouseOperationStatus> GetOperationStatusAsync(ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken)
        {
            return await GetOperationStatusAsync(new ClickHouseNodeEndpoint("source", 9000, false), cluster, operationId, cancellationToken);
        }

        public async Task<ClickHouseOperationStatus> GetOperationStatusAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken)
        {
            if (BlockOperationStatus)
            {
                _statusBlocked.TrySetResult();
                await _releaseStatus.Task.WaitAsync(cancellationToken);
            }

            lock (_lock)
            {
                return KnownOperations.TryGetValue(operationId, out var status)
                    ? new ClickHouseOperationStatus(true, status, OperationErrors.GetValueOrDefault(operationId))
                    : new ClickHouseOperationStatus(false, null, null);
            }
        }

        public Task KillQueryAsync(ClickHouseClusterEntity cluster, string queryId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                KilledQueries.Add(queryId);
                KnownOperations[queryId] = "CANCELLED";
            }
            return Task.CompletedTask;
        }

        public Task KillQueryAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string queryId, CancellationToken cancellationToken) =>
            KillQueryAsync(cluster, queryId, cancellationToken);

        public Task WaitForBlockedStatusAsync() => _statusBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void ReleaseBlockedStatus() => _releaseStatus.TrySetResult();
    }

    private sealed class FakeBackupStorageOperations : IBackupStorageOperations
    {
        public List<string> DeletedDirectories { get; } = [];
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public bool FailNextDelete { get; set; }
        public int FailWriteCount { get; set; }

        public Task DeleteDirectoryAsync(BackupTargetEntity target, string directoryPath, CancellationToken cancellationToken = default)
        {
            if (FailNextDelete)
            {
                FailNextDelete = false;
                throw new InvalidOperationException("simulated cleanup crash");
            }

            DeletedDirectories.Add(directoryPath);
            return Task.CompletedTask;
        }

        public Task WriteObjectAsync(BackupTargetEntity target, string path, byte[] content, CancellationToken cancellationToken = default)
        {
            if (FailWriteCount > 0)
            {
                FailWriteCount--;
                throw new InvalidOperationException("simulated manifest write crash");
            }

            Objects[path] = content;
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Objects.TryGetValue(path, out var content) ? content : Array.Empty<byte>());

        public Task<IReadOnlyList<string>> ListObjectPathsAsync(BackupTargetEntity target, string rootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Objects.Keys.Where(x => x.StartsWith(rootPath, StringComparison.Ordinal)).ToList());

        public Task DeleteObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<StorageConnectionTestResult> TestConnectionAsync(BackupTargetEntity target, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StorageConnectionTestResult(target.Id, target.Type, true, "ok"));
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly string _dataDirectory;
        public IServiceProvider Services { get; }
        public ChoboDbContext Db { get; }
        public FakeClickHouseAdapter ClickHouse { get; }
        public FakeBackupStorageOperations StorageDeletion { get; }
        public Guid SourceClusterId { get; }
        public Guid TargetClusterId { get; }
        public Guid TargetId { get; }

        private TestFixture(string dataDirectory, IServiceProvider services, ChoboDbContext db, FakeClickHouseAdapter clickHouse, FakeBackupStorageOperations storageDeletion, Guid sourceClusterId, Guid targetClusterId, Guid targetId)
        {
            _dataDirectory = dataDirectory;
            Services = services;
            Db = db;
            ClickHouse = clickHouse;
            StorageDeletion = storageDeletion;
            SourceClusterId = sourceClusterId;
            TargetClusterId = targetClusterId;
            TargetId = targetId;
        }

        public static async Task<TestFixture> CreateAsync(int? clusterMaxDop = null, ChoboBackupRestoreOptions? options = null, TimeProvider? timeProvider = null)
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), "chobo-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDirectory);
            var dbPath = Path.Combine(dataDirectory, "chobo.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                DefaultTimeout = 60
            }.ToString();
            var fake = new FakeClickHouseAdapter();
            var storageDeletion = new FakeBackupStorageOperations();
            var services = new ServiceCollection()
                .AddDbContext<ChoboDbContext>(builder => builder.UseSqlite(connectionString))
                .AddDbContextFactory<ChoboDbContext>(builder => builder.UseSqlite(connectionString), ServiceLifetime.Scoped)
                .AddSingleton(fake)
                .AddScoped<IClickHouseAdapter>(provider => provider.GetRequiredService<FakeClickHouseAdapter>())
                .AddSingleton(storageDeletion)
                .AddScoped<IBackupStorageOperations>(provider => provider.GetRequiredService<FakeBackupStorageOperations>())
                .AddScoped<PolicySelectorEvaluationService>()
                .AddMemoryCache()
                .AddScoped<ActorContext>()
                .AddScoped<IActorContext>(provider => provider.GetRequiredService<ActorContext>())
                .AddScoped<IAuditService, AuditService>()
                .AddSingleton(Options.Create(new ChoboTestHooksOptions()))
                .AddSingleton<ITestHookCoordinator, TestHookCoordinator>()
                .AddScoped<DashboardApplicationService>()
                .AddScoped<BackupApplicationService>()
                .AddScoped<IClusterRepository, ClusterRepository>()
                .AddScoped<ITargetRepository, TargetRepository>()
                .AddScoped<IPolicyRepository, PolicyRepository>()
                .AddScoped<IScheduleRepository, ScheduleRepository>()
                .AddScoped<IUnitOfWork, EfUnitOfWork>()
                .AddScoped<PolicyApplicationService>()
                .AddScoped<ScheduleApplicationService>()
                .AddScoped<SchemaBrowserApplicationService>()
                .AddScoped<SystemDefaultBackupPolicyService>()
                .AddScoped<IBackupStorageManifestService, BackupStorageManifestService>()
                .AddScoped<RestoreApplicationService>()
                .AddScoped<BackupRunnerService>()
                .AddScoped<RestoreRunnerService>()
                .AddScoped<BackupCleanupService>()
                .AddSingleton<IBackupRestoreQueues, BackupRestoreQueues>()
                .AddSingleton(Options.Create(options ?? new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1), SchedulerInterval = TimeSpan.FromSeconds(1) }))
                .AddSingleton(Options.Create(new RetentionManagementOptions { Interval = TimeSpan.FromSeconds(1), MaxDop = 2 }))
                .AddSingleton(Options.Create(new BackupsGarbageCollectorOptions { Interval = TimeSpan.FromSeconds(1), MaxDop = 2 }))
                .AddSingleton(timeProvider ?? TimeProvider.System)
                .AddSingleton<BackupSchedulerDispatcherBackgroundService>()
                .AddSingleton<RetentionManagementBackgroundService>()
                .AddSingleton<BackupsGarbageCollectorBackgroundService>()
                .AddSingleton<Serilog.ILogger>(Serilog.Core.Logger.None)
                .BuildServiceProvider();

            var db = services.GetRequiredService<ChoboDbContext>();
            await db.Database.EnsureCreatedAsync();

            var sourceClusterId = Guid.NewGuid();
            var targetClusterId = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            db.ClickHouseClusters.AddRange(
                new ClickHouseClusterEntity
                {
                    Id = sourceClusterId,
                    Name = "source",
                    Mode = ClusterMode.SingleInstance,
                    BackupRestoreMaxDop = clusterMaxDop,
                    AccessNodes = [new ClickHouseAccessNodeEntity { Host = "source", Port = 9000 }]
                },
                new ClickHouseClusterEntity
                {
                    Id = targetClusterId,
                    Name = "restore",
                    Mode = ClusterMode.SingleInstance,
                    AccessNodes = [new ClickHouseAccessNodeEntity { Host = "restore", Port = 9000 }]
                });
            db.BackupTargets.Add(new BackupTargetEntity
            {
                Id = targetId,
                Name = "minio",
                Endpoint = "http://minio:9000",
                Bucket = "data-bucket",
                Region = "us-east-1"
            });
            await db.SaveChangesAsync();

            return new TestFixture(dataDirectory, services, db, fake, storageDeletion, sourceClusterId, targetClusterId, targetId);
        }

        public async Task<Guid> CreateManualBackupAsync(PolicySelector? selector = null, string actorName = "system", Guid? actorUserId = null, bool schemaOnly = false)
        {
            var actor = Services.GetRequiredService<ActorContext>();
            actor.ActorName = actorName;
            actor.UserId = actorUserId;
            var backup = new BackupEntity
            {
                TriggerType = BackupTriggerType.Manual,
                Status = BackupRunStatus.Queued,
                SourceClusterId = SourceClusterId,
                TargetId = TargetId,
                RequestedByName = actorName,
                RequestedByUserId = actorUserId,
                ManualRequestJson = System.Text.Json.JsonSerializer.Serialize(new ManualBackupRequest(SourceClusterId, TargetId, selector ?? PolicySelector.Empty, SchemaOnly: schemaOnly), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                })
            };
            Db.Backups.Add(backup);
            await Db.SaveChangesAsync();
            return backup.Id;
        }

        public async Task<BackupEntity> SeedBackupWithTablesAsync(IReadOnlyList<SeedBackupTable> seedTables, int shardCount = 1)
        {
            var schema = new SchemaDefinitionEntity
            {
                SchemaHash = Guid.NewGuid().ToString("N"),
                Database = "sales",
                Table = "template",
                Engine = "MergeTree",
                CreateTableSql = "CREATE TABLE sales.template (id UInt64) ENGINE = MergeTree ORDER BY id"
            };
            var backup = new BackupEntity
            {
                TriggerType = BackupTriggerType.Manual,
                Status = BackupRunStatus.Running,
                SourceClusterId = SourceClusterId,
                TargetId = TargetId
            };
            foreach (var seed in seedTables)
            {
                var backupTable = new BackupTableEntity
                {
                    Database = seed.Database,
                    Table = seed.Table,
                    Engine = "MergeTree",
                    DataBackedUp = seed.DataBackedUp,
                    SchemaDefinition = schema,
                    S3Path = $"backups/{seed.Database}/{seed.Table}/manual/full/seed/{Guid.NewGuid():N}",
                    Status = seed.Status,
                    ClickHouseOperationId = seed.OperationId
                };
                if (seed.DataBackedUp)
                {
                    for (var shardNumber = 1; shardNumber <= shardCount; shardNumber++)
                    {
                        backupTable.Shards.Add(new BackupTableShardEntity
                        {
                            SourceShardNumber = shardNumber,
                            SourceShardName = shardCount == 1 ? "single" : $"shard-{shardNumber}",
                            ReplicaNumber = 1,
                            Host = shardCount == 1 ? "source" : $"source-s{shardNumber}",
                            Port = 9000,
                            S3Path = shardCount == 1 ? backupTable.S3Path : $"{backupTable.S3Path}/shards/shard-{shardNumber:0000}",
                            Status = seed.Status,
                            ClickHouseOperationId = seed.OperationId
                        });
                    }
                }

                backup.Tables.Add(backupTable);
            }

            Db.Backups.Add(backup);
            await Db.SaveChangesAsync();
            return backup;
        }

        public async Task<RestoreEntity> SeedRestoreAsync(BackupEntity backup, IReadOnlyList<SeedRestoreTable> seedTables)
        {
            var restore = new RestoreEntity
            {
                BackupId = backup.Id,
                TargetClusterId = TargetClusterId,
                Status = RestoreRunStatus.Running
            };
            var backupTables = await Db.BackupTables.Include(x => x.Shards).Where(x => x.BackupId == backup.Id).ToListAsync();
            foreach (var seed in seedTables)
            {
                var backupTable = backupTables.Single(x => x.Database == seed.SourceDatabase && x.Table == seed.SourceTable);
                var restoreTable = new RestoreTableEntity
                {
                    BackupTableId = backupTable.Id,
                    SourceDatabase = seed.SourceDatabase,
                    SourceTable = seed.SourceTable,
                    TargetDatabase = seed.SourceDatabase,
                    TargetTable = seed.SourceTable,
                    Status = seed.Status,
                    ClickHouseOperationId = seed.OperationId
                };
                foreach (var backupShard in backupTable.Shards)
                {
                    restoreTable.Shards.Add(new RestoreTableShardEntity
                    {
                        BackupTableShardId = backupShard.Id,
                        SourceShardNumber = backupShard.SourceShardNumber,
                        TargetShardNumber = 1,
                        TargetHost = "restore",
                        TargetPort = 9000,
                        LayoutRole = "Preserve",
                        RestoreDatabase = seed.SourceDatabase,
                        RestoreTableName = seed.SourceTable,
                        Status = seed.Status,
                        ClickHouseOperationId = seed.OperationId
                    });
                }

                restore.Tables.Add(restoreTable);
            }

            Db.Restores.Add(restore);
            await Db.SaveChangesAsync();
            return restore;
        }

        public async Task<BackupScheduleEntity> SeedPolicyAndScheduleAsync()
        {
            var policy = NewPolicy("hourly");
            var schedule = new BackupScheduleEntity
            {
                Name = "hourly",
                Policy = policy,
                BackupType = BackupType.Full,
                CronExpression = "* * * * * ?",
                TimeZoneId = "UTC",
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            };
            Db.BackupSchedules.Add(schedule);
            await Db.SaveChangesAsync();
            return schedule;
        }

        public async Task<BackupPolicyEntity> SeedPolicyAsync(
            string name = "policy",
            int? retentionMinutes = null,
            int minBackupsToKeep = 0,
            FailedBackupRetentionMode failedMode = FailedBackupRetentionMode.KeepAndExcludeFromMinBackupsToKeep)
        {
            var policy = NewPolicy(name);
            policy.FullRetentionMinutes = retentionMinutes;
            policy.IncrementalRetentionMinutes = retentionMinutes;
            policy.MinBackupsToKeep = minBackupsToKeep;
            policy.FailedBackupRetentionMode = failedMode;
            Db.BackupPolicies.Add(policy);
            await Db.SaveChangesAsync();
            return policy;
        }

        public async Task<BackupEntity> SeedPolicyBackupAsync(Guid? policyId, BackupRunStatus status, DateTimeOffset completedAt, bool isPinned = false, BackupType backupType = BackupType.Full, int shardCount = 1, string? tableName = null)
        {
            var backup = await SeedBackupWithTablesAsync([
                new SeedBackupTable("sales", tableName ?? $"orders_{Guid.NewGuid():N}", BackupTableStatus.Succeeded, "backup-op", true)
            ], shardCount);
            backup.PolicyId = policyId;
            backup.Status = status;
            backup.BackupType = backupType;
            backup.CompletedAt = completedAt;
            backup.CreatedAt = completedAt.AddMinutes(-1);
            backup.IsPinned = isPinned;
            backup.PinnedAt = isPinned ? completedAt.AddMinutes(1) : null;
            foreach (var table in backup.Tables)
            {
                table.EffectiveBackupType = backupType;
                foreach (var shard in table.Shards)
                {
                    shard.EffectiveBackupType = backupType;
                }
            }
            await Db.SaveChangesAsync();
            return backup;
        }

        public async Task<BackupEntity> SeedDependentIncrementalAsync(Guid policyId, BackupEntity parentFullBackup, DateTimeOffset completedAt, bool isPinned = false)
        {
            var parent = await Db.Backups
                .Include(x => x.Tables).ThenInclude(x => x.Shards)
                .SingleAsync(x => x.Id == parentFullBackup.Id);
            var parentTable = parent.Tables.Single();
            var incremental = await SeedPolicyBackupAsync(policyId, BackupRunStatus.Succeeded, completedAt, isPinned, BackupType.Incremental, parentTable.Shards.Count, parentTable.Table);
            var incrementalTable = incremental.Tables.Single();
            incrementalTable.EffectiveBackupType = BackupType.Incremental;
            incrementalTable.ParentFullBackupId = parent.Id;
            incrementalTable.ParentFullBackupTableId = parentTable.Id;
            for (var i = 0; i < incrementalTable.Shards.Count; i++)
            {
                incrementalTable.Shards[i].EffectiveBackupType = BackupType.Incremental;
                incrementalTable.Shards[i].ParentFullBackupId = parent.Id;
                incrementalTable.Shards[i].ParentFullBackupTableShardId = parentTable.Shards.OrderBy(x => x.SourceShardNumber).ElementAt(i).Id;
            }

            await Db.SaveChangesAsync();
            return incremental;
        }

        private BackupPolicyEntity NewPolicy(string name) =>
            new()
            {
                Name = name,
                SourceClusterId = SourceClusterId,
                TargetId = TargetId,
                SelectorJson = """{"version":1,"rules":[{"action":"Include","database":{"kind":"All","value":"*"},"table":{"kind":"All","value":"*"}}]}"""
            };

        public async Task RunBackupAsync(Guid id)
        {
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<BackupRunnerService>().RunAsync(id);
        }

        public async Task RunRestoreAsync(Guid id)
        {
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RestoreRunnerService>().RunAsync(id);
        }

        public async ValueTask DisposeAsync()
        {
            if (Services is IDisposable disposable)
            {
                disposable.Dispose();
            }

            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp test databases.
            }
        }
    }

    private static class BackupRestoreMappingAccessor
    {
        public static BackupDto ToDto(BackupEntity backup) =>
            new(
                backup.Id,
                backup.TriggerType,
                backup.Status,
                backup.BackupType,
                backup.ContentMode,
                backup.SourceClusterId,
                backup.TargetId,
                backup.PolicyId,
                backup.ScheduleId,
                backup.RequestedByUserId,
                backup.RequestedByName,
                backup.ManualRequestJson,
                backup.CreatedAt,
                backup.StartedAt,
                backup.CompletedAt,
                backup.Error,
                backup.FailureReason,
                backup.IsPinned,
                backup.PinnedAt,
                backup.PinnedByUserId,
                backup.PinnedByName,
                backup.DeletionReason,
                backup.DeletionRequestedAt,
                backup.DeletionStartedAt,
                backup.DeletedAt,
                backup.DeletionError,
                backup.DeletionAttemptCount,
                backup.Tables.Count,
                backup.Tables.Select(table => new BackupTableDto(
                    table.Id,
                    table.BackupId,
                    table.EffectiveBackupType,
                    table.ParentFullBackupId,
                    table.ParentFullBackupTableId,
                    table.Database,
                    table.Table,
                    table.Engine,
                    table.DataBackedUp,
                    table.SchemaDefinitionId,
                    table.S3Path,
                    table.Status,
                    table.ClickHouseOperationId,
                    table.ClickHouseStatus,
                    table.StartedAt,
                    table.CompletedAt,
                    table.Error,
                    table.Shards.Select(shard => new BackupTableShardDto(
                        shard.Id,
                        shard.BackupTableId,
                        shard.EffectiveBackupType,
                        shard.ParentFullBackupId,
                        shard.ParentFullBackupTableShardId,
                        shard.SourceShardNumber,
                        shard.SourceShardName,
                        shard.ReplicaNumber,
                        shard.Host,
                        shard.Port,
                        shard.UseTls,
                        shard.S3Path,
                        shard.Status,
                        shard.ClickHouseOperationId,
                        shard.ClickHouseStatus,
                        shard.StartedAt,
                        shard.CompletedAt,
                        shard.Error)).ToList())).ToList());

        public static RestoreDto ToDto(RestoreEntity restore) =>
            new(
                restore.Id,
                restore.BackupId,
                restore.TargetClusterId,
                restore.Status,
                restore.Append,
                restore.AllowSchemaMismatch,
                restore.Layout,
                restore.SourceShard,
                restore.TargetShard,
                restore.RequestedByUserId,
                restore.RequestedByName,
                restore.RequestJson,
                restore.CreatedAt,
                restore.StartedAt,
                restore.CompletedAt,
                restore.Error,
                restore.FailureReason,
                restore.Tables.Select(table => new RestoreTableDto(
                    table.Id,
                    table.RestoreId,
                    table.BackupTableId,
                    table.SourceDatabase,
                    table.SourceTable,
                    table.TargetDatabase,
                    table.TargetTable,
                    table.Append,
                    table.AllowSchemaMismatch,
                    table.SchemaOnly,
                    table.Status,
                    table.ClickHouseOperationId,
                    table.ClickHouseStatus,
                    table.Warning,
                    table.StartedAt,
                    table.CompletedAt,
                    table.Error,
                    table.Shards.Select(shard => new RestoreTableShardDto(
                        shard.Id,
                        shard.RestoreTableId,
                        shard.BackupTableShardId,
                        shard.SourceShardNumber,
                        shard.TargetShardNumber,
                        shard.TargetShardName,
                        shard.TargetReplicaNumber,
                        shard.TargetHost,
                        shard.TargetPort,
                        shard.TargetUseTls,
                        shard.LayoutRole,
                        shard.RestoreDatabase,
                        shard.RestoreTableName,
                        shard.Status,
                        shard.ClickHouseOperationId,
                        shard.ClickHouseStatus,
                        shard.Warning,
                        shard.StartedAt,
                        shard.CompletedAt,
                        shard.Error)).ToList())).ToList());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}




