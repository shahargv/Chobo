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
    private static ChoboBackupRestoreOptions FastCancelOptions(int maxDop = 3, TimeSpan? pollInterval = null) => new()
    {
        MaxDop = maxDop,
        PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1),
        CancelKillRetryDelay = TimeSpan.Zero
    };

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static IReadOnlyDictionary<string, JsonElement> Settings(params (string Name, object Value)[] settings) =>
        settings.ToDictionary(
            setting => setting.Name,
            setting => JsonSerializer.SerializeToElement(setting.Value, setting.Value.GetType()),
            StringComparer.OrdinalIgnoreCase);

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
        var paths = await fixture.Db.BackupTables.OrderBy(x => x.Table).ThenBy(x => x.StoragePath).Select(x => x.StoragePath).ToListAsync();
        Assert.Equal(2, paths.Count);
        Assert.All(paths, path =>
        {
            Assert.StartsWith("backups/full/manual/analytics/orders/", path);
            Assert.Matches(@"/[0-9]{8}T[0-9]{9}Z/[0-9a-f]{32}$", path);
        });
        Assert.NotEqual(paths[0], paths[1]);
    }

    [Fact]
    public async Task Backup_paths_keep_slash_and_underscore_table_names_distinct()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.Inventory.AddRange([
            Table("sales", "orders/2026", "MergeTree"),
            Table("sales", "orders_2026", "MergeTree")
        ]);

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var paths = await fixture.Db.BackupTables
            .Where(x => x.BackupId == backupId)
            .OrderBy(x => x.Table)
            .Select(x => x.StoragePath)
            .ToListAsync();
        Assert.Equal(2, paths.Count);
        Assert.Contains(paths, path => path.Contains("/orders%2F2026/", StringComparison.Ordinal));
        Assert.Contains(paths, path => path.Contains("/orders_2026/", StringComparison.Ordinal));
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

        var manifestEntry = fixture.StorageDeletion.Objects.First(x => x.Key.Contains("/_chobo/", StringComparison.Ordinal) && x.Key.EndsWith($"{backupId:N}.json", StringComparison.Ordinal));
        var manifestJson = Encoding.UTF8.GetString(manifestEntry.Value);
        var manifest = JsonSerializer.Deserialize<BackupStorageManifestV1>(manifestJson, JsonOptions)!;

        Assert.Equal($"backups/manual/_chobo/{backupId:N}.json", manifestEntry.Key);
        Assert.Equal(backupId, manifest.Backup.Id);
        Assert.Equal(BackupRunStatus.Succeeded, manifest.Backup.Status);
        Assert.Contains("backups/full/manual/sales/orders/", Assert.Single(manifest.RequiredStoragePaths));
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
    public async Task Backup_runner_does_not_fail_shards_when_storage_manifest_write_fails()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.StorageDeletion.FailWriteCount = 10;

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var shards = await fixture.Db.BackupTableShards.Where(x => x.BackupTable!.BackupId == backupId).ToListAsync();
        Assert.All(shards, shard => Assert.Equal(BackupTableStatus.Succeeded, shard.Status));
        Assert.False(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-failed" && x.EntityType == "backup-table-shard"));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "metadata-manifest-write-failed" && x.EntityId == backupId.ToString()));
    }

    [Fact]
    public async Task Backup_runner_writes_checkpoint_manifest_after_configured_shard_interval()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions
        {
            MaxDop = 1,
            PollInterval = TimeSpan.FromMilliseconds(1),
            ManifestCheckpointShardInterval = 20,
            ManifestWriteTimeout = TimeSpan.FromSeconds(1)
        });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.Topology.Clear();
        for (var shardNumber = 1; shardNumber <= 20; shardNumber++)
        {
            fixture.ClickHouse.Topology.Add(new ClickHouseShardReplicaInfo(shardNumber, $"shard-{shardNumber}", 1, $"source-s{shardNumber}", 9000, false, 0));
        }

        var cluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.SourceClusterId);
        cluster.Mode = ClusterMode.Cluster;
        cluster.ClickHouseClusterName = "prod";
        await fixture.Db.SaveChangesAsync();

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var shards = await fixture.Db.BackupTableShards.Where(x => x.BackupTable!.BackupId == backupId).ToListAsync();
        Assert.Equal(20, shards.Count);
        Assert.All(shards, shard => Assert.Equal(BackupTableStatus.Succeeded, shard.Status));
        Assert.True(fixture.StorageDeletion.WriteObjectCount >= 2);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "metadata-manifest-checkpoint-written" && x.EntityId == backupId.ToString()));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "metadata-manifest-written" && x.EntityId == backupId.ToString()));
    }

    [Fact]
    public async Task Backup_runner_treats_storage_manifest_timeout_as_best_effort()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions
        {
            MaxDop = 1,
            PollInterval = TimeSpan.FromMilliseconds(1),
            ManifestWriteTimeout = TimeSpan.FromMilliseconds(10)
        });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.StorageDeletion.DelayWritesUntilCanceled = true;

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.SingleAsync(x => x.Id == backupId);
        var shard = await fixture.Db.BackupTableShards.SingleAsync(x => x.BackupTable!.BackupId == backupId);
        Assert.Equal(BackupRunStatus.Succeeded, backup.Status);
        Assert.Equal(BackupTableStatus.Succeeded, shard.Status);
        Assert.False(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-failed" && x.EntityType == "backup-table-shard"));
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
        var failedShardPath = failed.Tables.Single().Shards.Single().StoragePath;
        fixture.StorageDeletion.Objects[$"{failedShardPath}/data.bin"] = Encoding.UTF8.GetBytes("data");

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
        fixture.Db.BackupTargets.Add(CreateS3TargetEntity(scanTargetId, "scan-target", "http://minio:9000", "us-east-1", "data-bucket", null, true, true));
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
        Assert.Contains("encrypted-access", (await fixture.Db.BackupTargets.SingleAsync(x => x.Id == fixture.TargetId)).SecretsJson);

        var second = await fixture.Services.GetRequiredService<IBackupStorageManifestService>()
            .RecoverFromScanAsync(new RecoverBackupMetadataScanRequest(scanTargetId, ""));
        Assert.Equal(0, second.ImportedBackupCount);
        Assert.Equal(1, second.UpdatedBackupCount);
    }

    [Fact]
    public async Task Backup_metadata_recovery_imports_missing_storage_paths_as_partially_succeeded()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 2);
        backup.Status = BackupRunStatus.Succeeded;
        backup.CompletedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();
        await fixture.Services.GetRequiredService<IBackupStorageManifestService>().WriteManifestAsync(backup.Id);

        var presentShard = backup.Tables.Single().Shards.OrderBy(x => x.SourceShardNumber).First();
        fixture.StorageDeletion.Objects[$"{presentShard.StoragePath}/data.bin"] = Encoding.UTF8.GetBytes("data");
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
        fixture.Db.BackupTargets.Add(CreateS3TargetEntity(scanTargetId, "scan-target", "http://minio:9000", "us-east-1", "data-bucket", null, true, true));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        fixture.StorageDeletion.Objects.Clear();
        foreach (var entry in storedObjects)
        {
            fixture.StorageDeletion.Objects[entry.Key] = entry.Value;
        }

        var result = await fixture.Services.GetRequiredService<IBackupStorageManifestService>()
            .RecoverFromScanAsync(new RecoverBackupMetadataScanRequest(scanTargetId, "backups"));

        Assert.Equal(1, result.ImportedBackupCount);
        Assert.Contains(result.Errors, x => x.Contains("required storage path is missing", StringComparison.Ordinal));
        var recovered = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backup.Id);
        Assert.Equal(BackupRunStatus.PartiallySucceeded, recovered.Status);
        Assert.Contains("missing storage data path", recovered.FailureReason);
        Assert.Equal(BackupTableStatus.PartiallySucceeded, recovered.Tables.Single().Status);
        Assert.Contains(recovered.Tables.Single().Shards, x => x.SourceShardNumber == 1 && x.Status == BackupTableStatus.Succeeded);
        Assert.Contains(recovered.Tables.Single().Shards, x => x.SourceShardNumber == 2 && x.Status == BackupTableStatus.Failed && x.Error!.Contains("Required storage data path was missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backup_metadata_recovery_scan_ignores_legacy_per_data_path_manifests()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "legacy_orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        await fixture.Services.GetRequiredService<IBackupStorageManifestService>().WriteManifestAsync(backup.Id);

        var manifestEntry = fixture.StorageDeletion.Objects.Single(x => x.Key.Contains("/_chobo/", StringComparison.Ordinal));
        var legacyPath = $"{backup.Tables.Single().StoragePath}/_chobo/backup-metadata.v1.json";
        fixture.StorageDeletion.Objects.Clear();
        fixture.StorageDeletion.Objects[legacyPath] = manifestEntry.Value;
        fixture.StorageDeletion.Objects[$"{backup.Tables.Single().StoragePath}/data.bin"] = Encoding.UTF8.GetBytes("data");

        await fixture.Db.BackupTableShards.ExecuteDeleteAsync();
        await fixture.Db.BackupTables.ExecuteDeleteAsync();
        await fixture.Db.Backups.ExecuteDeleteAsync();
        await fixture.Db.BackupPolicies.ExecuteDeleteAsync();
        await fixture.Db.SchemaDefinitions.ExecuteDeleteAsync();
        await fixture.Db.ClickHouseAccessNodes.ExecuteDeleteAsync();
        await fixture.Db.ClickHouseClusters.ExecuteDeleteAsync();
        await fixture.Db.BackupTargets.ExecuteDeleteAsync();
        var scanTargetId = Guid.NewGuid();
        fixture.Db.BackupTargets.Add(CreateS3TargetEntity(scanTargetId, "scan-target", "http://minio:9000", "us-east-1", "data-bucket", null, true, true));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await fixture.Services.GetRequiredService<IBackupStorageManifestService>()
            .RecoverFromScanAsync(new RecoverBackupMetadataScanRequest(scanTargetId, "backups"));

        Assert.Equal(0, result.ImportedBackupCount);
        Assert.Empty(result.Items);
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
    public async Task Backup_shard_recovers_transient_submission_failure_when_clickhouse_operation_exists()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions
        {
            MaxDop = 1,
            PollInterval = TimeSpan.FromMilliseconds(1),
            BackupSubmissionStatusCheckDelay = TimeSpan.FromMilliseconds(1),
            TransientShardRetryDelay = TimeSpan.FromMilliseconds(1),
            TransientShardMaxRetries = 3
        });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.FailNextBackupStartTransientCount = 1;
        fixture.ClickHouse.CreateOperationBeforeFailingBackupStart = true;

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backupId);
        var shard = Assert.Single(Assert.Single(backup.Tables).Shards);
        Assert.Equal(BackupRunStatus.Succeeded, backup.Status);
        Assert.Equal(BackupTableStatus.Succeeded, shard.Status);
        Assert.NotNull(shard.ClickHouseOperationId);
        Assert.Single(fixture.ClickHouse.BackupStartTables);
        Assert.False(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-retry-scheduled" && x.EntityId == shard.Id.ToString()));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "clickhouse-operation-recovered" && x.EntityId == shard.Id.ToString()));
    }

    [Fact]
    public async Task Backup_shard_retries_transient_poll_failure_after_recovering_operation_id()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions
        {
            MaxDop = 1,
            PollInterval = TimeSpan.FromMilliseconds(1),
            BackupSubmissionStatusCheckDelay = TimeSpan.FromMilliseconds(1),
            TransientShardRetryDelay = TimeSpan.FromMilliseconds(1),
            TransientShardMaxRetries = 3
        });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.NextBackupStatus = "CREATING_BACKUP";
        fixture.ClickHouse.FailNextBackupStartTransientCount = 1;
        fixture.ClickHouse.CreateOperationBeforeFailingBackupStart = true;
        fixture.ClickHouse.FailNextOperationStatusTransientCount = 1;
        fixture.ClickHouse.CompleteCreatingBackupOperationsAfterStatusRead = true;

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backupId);
        var shard = Assert.Single(Assert.Single(backup.Tables).Shards);
        Assert.Equal(BackupRunStatus.Succeeded, backup.Status);
        Assert.Equal(BackupTableStatus.Succeeded, shard.Status);
        Assert.NotNull(shard.ClickHouseOperationId);
        Assert.Single(fixture.ClickHouse.BackupStartTables);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "clickhouse-operation-recovered" && x.EntityId == shard.Id.ToString()));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-retry-scheduled" && x.EntityId == shard.Id.ToString() && x.Details.Contains("recovered")));
    }

    [Fact]
    public async Task Backup_shard_does_not_retry_transient_submission_failure_when_no_clickhouse_operation_exists()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions
        {
            MaxDop = 1,
            PollInterval = TimeSpan.FromMilliseconds(1),
            BackupSubmissionStatusCheckDelay = TimeSpan.FromMilliseconds(1),
            TransientShardRetryDelay = TimeSpan.FromMilliseconds(1),
            TransientShardMaxRetries = 3
        });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.FailNextBackupStartTransientCount = 1;

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backupId);
        var shard = Assert.Single(Assert.Single(backup.Tables).Shards);
        Assert.Equal(BackupRunStatus.Failed, backup.Status);
        Assert.Equal(BackupTableStatus.Failed, shard.Status);
        Assert.Contains("did not find a matching ClickHouse backup operation", shard.Error);
        Assert.Null(shard.ClickHouseOperationId);
        Assert.Single(fixture.ClickHouse.BackupStartTables);
        Assert.False(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-retry-scheduled" && x.EntityId == shard.Id.ToString()));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "clickhouse-operation-recovery-not-found" && x.EntityId == shard.Id.ToString()));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-failed" && x.EntityId == shard.Id.ToString() && x.Details.Contains("unknown-submission-outcome")));
    }

    [Fact]
    public async Task Backup_shard_does_not_recover_prefix_matched_clickhouse_operation()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions
        {
            MaxDop = 1,
            PollInterval = TimeSpan.FromMilliseconds(1),
            BackupSubmissionStatusCheckDelay = TimeSpan.FromMilliseconds(1),
            TransientShardRetryDelay = TimeSpan.FromMilliseconds(1),
            TransientShardMaxRetries = 3
        });
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Queued, null, true)
        ]);
        var shard = await fixture.Db.BackupTableShards.SingleAsync(x => x.BackupTable!.BackupId == backup.Id);
        fixture.ClickHouse.KnownOperations["prefix-op"] = "BACKUP_CREATED";
        fixture.ClickHouse.BackupOperations.Add(("prefix-op", $"{shard.StoragePath}-other"));
        fixture.ClickHouse.FailNextBackupStartTransientCount = 1;

        await fixture.RunBackupAsync(backup.Id);

        fixture.Db.ChangeTracker.Clear();
        var backupAfterRun = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backup.Id);
        var shardAfterRun = Assert.Single(Assert.Single(backupAfterRun.Tables).Shards);
        Assert.Equal(BackupRunStatus.Failed, backupAfterRun.Status);
        Assert.Equal(BackupTableStatus.Failed, shardAfterRun.Status);
        Assert.Null(shardAfterRun.ClickHouseOperationId);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "clickhouse-operation-recovery-not-found" && x.EntityId == shardAfterRun.Id.ToString()));
    }

    [Fact]
    public async Task Backup_shard_retry_resumes_submitted_operation_after_transient_poll_failure()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions
        {
            MaxDop = 1,
            PollInterval = TimeSpan.FromMilliseconds(1),
            TransientShardRetryDelay = TimeSpan.FromMilliseconds(1),
            TransientShardMaxRetries = 3
        });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.FailNextOperationStatusTransientCount = 1;

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backupId);
        var shard = Assert.Single(Assert.Single(backup.Tables).Shards);
        Assert.Equal(BackupRunStatus.Succeeded, backup.Status);
        Assert.Equal(BackupTableStatus.Succeeded, shard.Status);
        Assert.NotNull(shard.ClickHouseOperationId);
        Assert.Single(fixture.ClickHouse.BackupStartTables);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-retry-scheduled" && x.EntityId == shard.Id.ToString()));
    }

    [Fact]
    public async Task Backup_shard_does_not_retry_s3_failures()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions
        {
            MaxDop = 1,
            PollInterval = TimeSpan.FromMilliseconds(1),
            TransientShardRetryDelay = TimeSpan.FromMilliseconds(1),
            TransientShardMaxRetries = 3
        });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.NextBackupStatus = "BACKUP_FAILED";
        fixture.ClickHouse.NextBackupError = "Code: 499. DB::Exception: Not found address of host: missing-minio. (DNS_ERROR). (S3_ERROR)";

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backupId);
        var shard = Assert.Single(Assert.Single(backup.Tables).Shards);
        Assert.Equal(BackupRunStatus.Failed, backup.Status);
        Assert.Equal(BackupTableStatus.Failed, shard.Status);
        Assert.Contains("S3_ERROR", shard.Error);
        Assert.Single(fixture.ClickHouse.BackupStartTables);
        Assert.False(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-retry-scheduled" && x.EntityId == shard.Id.ToString()));
    }

    [Fact]
    public async Task Backup_statement_includes_cluster_and_policy_advanced_settings()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        var cluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.SourceClusterId);
        cluster.ClickHouseBackupSettingsJson = ClickHouseAdvancedSettings.Serialize(Settings(("max_backup_bandwidth", 100), ("backup_threads", 2)), ClickHouseAdvancedSettingsKind.Backup);
        var policy = await fixture.SeedPolicyAsync();
        policy.ClickHouseBackupSettingsJson = ClickHouseAdvancedSettings.Serialize(Settings(("backup_threads", 8), ("use_same_s3_credentials_for_base_backup", true)), ClickHouseAdvancedSettingsKind.Backup);
        await fixture.Db.SaveChangesAsync();

        var backup = await fixture.Services.GetRequiredService<BackupApplicationService>()
            .ManualAsync(new ManualBackupRequest(Guid.Empty, null, PolicySelector.Empty, PolicyId: policy.Id));
        await fixture.RunBackupAsync(backup.Id);

        var sql = Assert.Single(fixture.ClickHouse.BackupStartSql);
        Assert.Contains("BACKUP TABLE `sales`.`orders`", sql, StringComparison.Ordinal);
        Assert.Contains("SETTINGS backup_threads = 8", sql, StringComparison.Ordinal);
        Assert.Contains("max_backup_bandwidth = 100", sql, StringComparison.Ordinal);
        Assert.Contains("use_same_s3_credentials_for_base_backup = 1", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("backup_threads = 2", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manual_backup_statement_uses_request_advanced_settings()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        var cluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.SourceClusterId);
        cluster.ClickHouseBackupSettingsJson = ClickHouseAdvancedSettings.Serialize(Settings(("max_backup_bandwidth", 100)), ClickHouseAdvancedSettingsKind.Backup);
        await fixture.Db.SaveChangesAsync();

        var backup = await fixture.Services.GetRequiredService<BackupApplicationService>()
            .ManualAsync(new ManualBackupRequest(
                fixture.SourceClusterId,
                fixture.TargetId,
                PolicySelector.Empty,
                ClickHouseBackupSettings: Settings(("max_backup_bandwidth", 777), ("s3_storage_class", "STANDARD_IA"))));
        await fixture.RunBackupAsync(backup.Id);

        var sql = Assert.Single(fixture.ClickHouse.BackupStartSql);
        Assert.Contains("BACKUP TABLE `sales`.`orders`", sql, StringComparison.Ordinal);
        Assert.Contains("max_backup_bandwidth = 777", sql, StringComparison.Ordinal);
        Assert.Contains("s3_storage_class = 'STANDARD_IA'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("max_backup_bandwidth = 100", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backup_executor_capacity_ignores_queued_rows_that_have_not_started()
    {
        await using var fixture = await TestFixture.CreateAsync(
            clusterMaxDop: 1,
            options: new ChoboBackupRestoreOptions { MaxDop = 1, MaxActiveQueueItems = 1, PollInterval = TimeSpan.FromMilliseconds(10) });
        fixture.ClickHouse.Inventory.Add(Table("sales", "capacity_orders", "MergeTree"));
        var service = fixture.Services.GetRequiredService<BackupApplicationService>();
        var queues = fixture.Services.GetRequiredService<IBackupRestoreQueues>();
        var executor = new BackupExecutorBackgroundService(
            fixture.Services,
            queues,
            fixture.Services.GetRequiredService<IOptionsMonitor<ChoboBackupRestoreOptions>>(),
            Serilog.Core.Logger.None);

        var backup = await service.ManualAsync(new ManualBackupRequest(fixture.SourceClusterId, fixture.TargetId, PolicySelector.Empty));
        Assert.True(await fixture.Db.BackupRestoreQueueItems.AnyAsync(x => x.OperationId == backup.Id && x.StartedAt == null && x.CompletedAt == null));

        await executor.StartAsync(CancellationToken.None);
        await WaitUntilAsync(async () =>
        {
            fixture.Db.ChangeTracker.Clear();
            return await fixture.Db.BackupRestoreQueueItems.AnyAsync(x => x.OperationId == backup.Id && x.StartedAt != null);
        });

        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Backup_executor_prepares_second_data_backup_queue_while_first_backup_is_running()
    {
        await using var fixture = await TestFixture.CreateAsync(
            clusterMaxDop: 1,
            options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(10) });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.BlockOperationStatus = true;
        var service = fixture.Services.GetRequiredService<BackupApplicationService>();
        var queues = fixture.Services.GetRequiredService<IBackupRestoreQueues>();
        var executor = new BackupExecutorBackgroundService(
            fixture.Services,
            queues,
            fixture.Services.GetRequiredService<IOptionsMonitor<ChoboBackupRestoreOptions>>(),
            Serilog.Core.Logger.None);

        var first = await service.ManualAsync(new ManualBackupRequest(fixture.SourceClusterId, fixture.TargetId, PolicySelector.Empty));
        var second = await service.ManualAsync(new ManualBackupRequest(fixture.SourceClusterId, fixture.TargetId, PolicySelector.Empty));

        await executor.StartAsync(CancellationToken.None);
        await fixture.ClickHouse.WaitForBlockedStatusAsync();
        await WaitUntilAsync(async () =>
        {
            fixture.Db.ChangeTracker.Clear();
            return await fixture.Db.BackupRestoreQueueItems.AnyAsync(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == second.Id);
        });

        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.BackupRestoreQueueItems.AnyAsync(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == first.Id && x.StartedAt != null));
        var secondQueueItem = await fixture.Db.BackupRestoreQueueItems.SingleAsync(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == second.Id);
        Assert.Null(secondQueueItem.StartedAt);

        fixture.ClickHouse.ReleaseBlockedStatus();
        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Policy_manual_execution_prepares_queue_rows_before_executor_runs()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        var policy = await fixture.SeedPolicyAsync();
        var service = fixture.Services.GetRequiredService<BackupApplicationService>();

        var backup = await service.ManualAsync(new ManualBackupRequest(Guid.Empty, null, PolicySelector.Empty, PolicyId: policy.Id));

        fixture.Db.ChangeTracker.Clear();
        var item = await fixture.Db.BackupRestoreQueueItems.SingleAsync(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == backup.Id);
        Assert.Null(item.StartedAt);
        Assert.Null(item.CompletedAt);
        Assert.Equal(fixture.SourceClusterId, item.ClusterId);
        Assert.True(await fixture.Db.BackupTableShards.AnyAsync(x => x.Id == item.ShardId && x.Status == BackupTableStatus.Queued));
    }

    [Fact]
    public async Task Manual_policy_backup_finishes_queueing_after_request_cancellation_once_created()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.BlockTopology = true;
        var policy = await fixture.SeedPolicyAsync();
        var service = fixture.Services.GetRequiredService<BackupApplicationService>();
        var queues = fixture.Services.GetRequiredService<IBackupRestoreQueues>();
        using var cts = new CancellationTokenSource();

        var task = service.ManualAsync(new ManualBackupRequest(Guid.Empty, null, PolicySelector.Empty, BackupType.Incremental, PolicyId: policy.Id), cts.Token);
        await fixture.ClickHouse.WaitForBlockedTopologyAsync();
        cts.Cancel();
        fixture.ClickHouse.ReleaseBlockedTopology();

        var backup = await task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Db.ChangeTracker.Clear();
        Assert.True(await fixture.Db.BackupRestoreQueueItems.AnyAsync(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == backup.Id));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "queued" && x.EntityId == backup.Id.ToString()));
        Assert.True(queues.Backups.Reader.TryRead(out var queuedBackupId));
        Assert.Equal(backup.Id, queuedBackupId);
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
    public async Task Manual_schema_only_backup_is_queued_independently_from_data_backups()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var service = fixture.Services.GetRequiredService<BackupApplicationService>();
        var queues = fixture.Services.GetRequiredService<IBackupRestoreQueues>();

        var dataBackup = await service.ManualAsync(new ManualBackupRequest(fixture.SourceClusterId, fixture.TargetId, PolicySelector.Empty));
        var schemaOnlyBackup = await service.ManualAsync(new ManualBackupRequest(fixture.SourceClusterId, fixture.TargetId, PolicySelector.Empty, SchemaOnly: true));

        Assert.True(queues.Backups.Reader.TryRead(out var queuedDataBackupId));
        Assert.Equal(dataBackup.Id, queuedDataBackupId);
        Assert.True(queues.SchemaOnlyBackups.Reader.TryRead(out var queuedSchemaOnlyBackupId));
        Assert.Equal(schemaOnlyBackup.Id, queuedSchemaOnlyBackupId);
        Assert.False(queues.Backups.Reader.TryRead(out _));
        Assert.False(queues.SchemaOnlyBackups.Reader.TryRead(out _));
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
        var policy = await fixture.SeedPolicyAsync("schema-policy");
        policy.ContentMode = BackupContentMode.SchemaOnly;
        policy.TargetId = null;
        await fixture.Db.SaveChangesAsync();
        var backup = await fixture.Services.GetRequiredService<BackupApplicationService>()
            .ManualAsync(new ManualBackupRequest(Guid.Empty, null, PolicySelector.Empty, PolicyId: policy.Id));
        var backupId = backup.Id;
        await fixture.RunBackupAsync(backupId);

        var service = fixture.Services.GetRequiredService<SchemaBrowserApplicationService>();
        var summaries = await service.ListBackupsAsync(DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddDays(1));
        Assert.Contains(summaries, x => x.Id == backupId && x.ContentMode == BackupContentMode.SchemaOnly && x.SourceClusterName == "source" && x.PolicyName == "schema-policy" && x.TableCount == 2);
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
    public async Task First_incremental_policy_backup_falls_back_to_full_and_prepares_queue_rows()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var policy = await fixture.SeedPolicyAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        var service = fixture.Services.GetRequiredService<BackupApplicationService>();

        var backup = await service.ManualAsync(new ManualBackupRequest(Guid.Empty, null, PolicySelector.Empty, BackupType.Incremental, PolicyId: policy.Id));

        var table = await fixture.Db.BackupTables.Include(x => x.Shards).SingleAsync(x => x.BackupId == backup.Id);
        Assert.Equal(BackupType.Full, table.EffectiveBackupType);
        Assert.Null(table.ParentFullBackupId);
        Assert.NotEmpty(table.Shards);
        Assert.All(table.Shards, shard =>
        {
            Assert.Equal(BackupType.Full, shard.EffectiveBackupType);
            Assert.Null(shard.ParentFullBackupId);
        });
        Assert.True(await fixture.Db.BackupRestoreQueueItems.AnyAsync(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == backup.Id));
        Assert.Equal(BackupRunStatus.Queued, (await fixture.Db.Backups.SingleAsync(x => x.Id == backup.Id)).Status);
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
        Assert.StartsWith("backups/full/", newTable.StoragePath);
        Assert.Equal(BackupType.Incremental, existingTable.EffectiveBackupType);
        Assert.NotNull(existingTable.ParentFullBackupTableId);
        Assert.Contains($"parent-full-{full.Id:N}", existingTable.StoragePath);
        Assert.Equal(BackupType.Incremental, Assert.Single(existingTable.Shards).EffectiveBackupType);
        Assert.Contains(fixture.Db.BackupTableShards.Single(x => x.Id == existingTable.Shards[0].ParentFullBackupTableShardId).StoragePath, fixture.ClickHouse.BackupBasePaths);

        var detail = await fixture.Services.GetRequiredService<BackupApplicationService>().GetAsync(incremental.Id, includeTables: true);
        var summary = await fixture.Services.GetRequiredService<BackupApplicationService>().GetAsync(incremental.Id, includeTables: false);
        Assert.Equal([full.Id], detail!.RelatedFullBackupIds);
        Assert.Equal([full.Id], summary!.RelatedFullBackupIds);
    }

    [Fact]
    public async Task Incremental_backup_uses_succeeded_rows_from_partially_succeeded_parent()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var policy = await fixture.SeedPolicyAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.Inventory.Add(Table("sales", "line_items", "MergeTree"));
        fixture.ClickHouse.Inventory.Add(Table("sales", "failed_orders", "MergeTree"));
        var parent = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "orders-op", true),
            new SeedBackupTable("sales", "line_items", BackupTableStatus.Succeeded, "line-items-op", true),
            new SeedBackupTable("sales", "failed_orders", BackupTableStatus.Failed, "failed-op", true)
        ]);
        var parentCompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        parent.PolicyId = policy.Id;
        parent.Status = BackupRunStatus.PartiallySucceeded;
        parent.BackupType = BackupType.Full;
        parent.CompletedAt = parentCompletedAt;
        parent.CreatedAt = parentCompletedAt.AddMinutes(-1);
        foreach (var table in parent.Tables)
        {
            table.EffectiveBackupType = BackupType.Full;
            foreach (var shard in table.Shards)
            {
                shard.EffectiveBackupType = BackupType.Full;
            }
        }
        await fixture.Db.SaveChangesAsync();

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
        var tables = await fixture.Db.BackupTables.Include(x => x.Shards).Where(x => x.BackupId == incremental.Id).ToListAsync();
        var orders = Assert.Single(tables, x => x.Table == "orders");
        var lineItems = Assert.Single(tables, x => x.Table == "line_items");
        var failedOrders = Assert.Single(tables, x => x.Table == "failed_orders");
        Assert.Equal(BackupType.Incremental, orders.EffectiveBackupType);
        Assert.Equal(parent.Id, orders.ParentFullBackupId);
        Assert.Equal(BackupType.Incremental, Assert.Single(orders.Shards).EffectiveBackupType);
        Assert.Equal(parent.Id, Assert.Single(orders.Shards).ParentFullBackupId);
        Assert.Equal(BackupType.Incremental, lineItems.EffectiveBackupType);
        Assert.Equal(parent.Id, lineItems.ParentFullBackupId);
        Assert.Equal(BackupType.Full, failedOrders.EffectiveBackupType);
        Assert.Null(failedOrders.ParentFullBackupId);
        Assert.Equal(BackupType.Full, Assert.Single(failedOrders.Shards).EffectiveBackupType);
        Assert.Null(Assert.Single(failedOrders.Shards).ParentFullBackupId);
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
        Assert.Contains($"parent-full-{full.Id:N}", shardOne.StoragePath);
        Assert.Equal(BackupType.Full, shardTwo.EffectiveBackupType);
        Assert.Null(shardTwo.ParentFullBackupTableShardId);
        Assert.StartsWith("backups/full/", shardTwo.StoragePath);

        var laterIncremental = new BackupEntity
        {
            TriggerType = BackupTriggerType.Manual,
            Status = BackupRunStatus.Queued,
            BackupType = BackupType.Incremental,
            SourceClusterId = fixture.SourceClusterId,
            TargetId = fixture.TargetId,
            PolicyId = policy.Id
        };
        fixture.Db.Backups.Add(laterIncremental);
        await fixture.Db.SaveChangesAsync();
        await fixture.RunBackupAsync(laterIncremental.Id);

        fixture.Db.ChangeTracker.Clear();
        var laterTable = await fixture.Db.BackupTables.Include(x => x.Shards).SingleAsync(x => x.BackupId == laterIncremental.Id);
        var laterShardOne = Assert.Single(laterTable.Shards, x => x.SourceShardNumber == 1);
        var laterShardTwo = Assert.Single(laterTable.Shards, x => x.SourceShardNumber == 2);
        Assert.Equal(BackupType.Incremental, laterTable.EffectiveBackupType);
        Assert.Equal(BackupType.Incremental, laterShardOne.EffectiveBackupType);
        Assert.Equal(shardOne.ParentFullBackupTableShardId, laterShardOne.ParentFullBackupTableShardId);
        Assert.Equal(full.Id, laterShardOne.ParentFullBackupId);
        Assert.Equal(BackupType.Incremental, laterShardTwo.EffectiveBackupType);
        Assert.Equal(shardTwo.Id, laterShardTwo.ParentFullBackupTableShardId);
        Assert.Equal(incremental.Id, laterShardTwo.ParentFullBackupId);

        var detail = await fixture.Services.GetRequiredService<BackupApplicationService>().GetAsync(laterIncremental.Id, includeTables: false);
        Assert.Equal(new[] { full.Id, incremental.Id }.OrderBy(x => x).ToList(), detail!.RelatedFullBackupIds.OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task Incremental_backup_ignores_parent_full_table_older_than_inherited_max_base_age()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1), DefaultMaxAgeHoursForBaseBackup = 2 });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        var policy = await fixture.SeedPolicyAsync();
        var full = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, DateTimeOffset.UtcNow.AddHours(-3), tableName: "orders");

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
        Assert.Equal(BackupType.Full, table.EffectiveBackupType);
        Assert.Null(table.ParentFullBackupTableId);
        Assert.StartsWith("backups/full/", table.StoragePath);
        Assert.DoesNotContain($"parent-full-{full.Id:N}", table.StoragePath);
        var shard = Assert.Single(table.Shards);
        Assert.Equal(BackupType.Full, shard.EffectiveBackupType);
        Assert.Null(shard.ParentFullBackupTableShardId);
    }

    [Fact]
    public async Task Incremental_backup_uses_parent_full_table_within_policy_max_base_age_override()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1), DefaultMaxAgeHoursForBaseBackup = 2 });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        var policy = await fixture.SeedPolicyAsync();
        policy.MaxAgeHoursForBaseBackup = 4;
        await fixture.Db.SaveChangesAsync();
        var full = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, DateTimeOffset.UtcNow.AddHours(-3), tableName: "orders");

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
        Assert.Equal(BackupType.Incremental, table.EffectiveBackupType);
        Assert.Equal(full.Id, table.ParentFullBackupId);
        Assert.Equal(BackupType.Incremental, Assert.Single(table.Shards).EffectiveBackupType);
        Assert.Equal(full.Id, Assert.Single(table.Shards).ParentFullBackupId);
    }

    [Fact]
    public async Task Incremental_sharded_backup_ignores_old_base_shards_and_uses_recent_shards()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1), DefaultMaxAgeHoursForBaseBackup = 2 });
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "shard-1", 1, "source-s1", 9000, false, 0),
            new ClickHouseShardReplicaInfo(2, "shard-2", 1, "source-s2", 9000, false, 0)
        ]);
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        var policy = await fixture.SeedPolicyAsync();
        _ = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, DateTimeOffset.UtcNow.AddHours(-3), shardCount: 1, tableName: "orders");
        var recent = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, DateTimeOffset.UtcNow.AddMinutes(-30), shardCount: 2, tableName: "orders");
        recent.Tables.Single().Shards.Single(x => x.SourceShardNumber == 1).Status = BackupTableStatus.Failed;
        await fixture.Db.SaveChangesAsync();

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
        Assert.Equal(BackupType.Full, shardOne.EffectiveBackupType);
        Assert.Null(shardOne.ParentFullBackupTableShardId);
        Assert.Equal(BackupType.Incremental, shardTwo.EffectiveBackupType);
        Assert.Equal(recent.Id, shardTwo.ParentFullBackupId);
        Assert.NotNull(shardTwo.ParentFullBackupTableShardId);
    }

    [Fact]
    public async Task Backup_runner_enforces_global_and_cluster_maxdop()
    {
        await using var globalFixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 2, PollInterval = TimeSpan.FromMilliseconds(1) });
        var globalCluster = await globalFixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == globalFixture.SourceClusterId);
        globalCluster.ShardMaxDopDefault = 16;
        globalCluster.NodeMaxDopDefault = 16;
        await globalFixture.Db.SaveChangesAsync();
        globalFixture.ClickHouse.RequiredConcurrentBackupStartsBeforeRelease = 2;
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
        fixture.ClickHouse.RequiredConcurrentBackupStartsBeforeRelease = 3;
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
    public async Task Backup_queue_with_maxdop_one_drains_all_rows()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 1, options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(10);
        for (var i = 0; i < 4; i++)
        {
            fixture.ClickHouse.Inventory.Add(Table("sales", $"drain_{i}", "MergeTree"));
        }

        var backupId = await fixture.CreateManualBackupAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.Db.ChangeTracker.Clear();
        var backup = await fixture.Db.Backups.Include(x => x.Tables).SingleAsync(x => x.Id == backupId);
        Assert.Equal(BackupRunStatus.Succeeded, backup.Status);
        Assert.Equal(4, fixture.ClickHouse.BackupStartTables.Count);
        Assert.All(backup.Tables, table => Assert.Equal(BackupTableStatus.Succeeded, table.Status));
        Assert.Equal(1, fixture.ClickHouse.MaxConcurrentBackupStarts);
    }

    [Fact]
    public async Task Backup_queue_table_reorder_changes_execution_order()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 1, options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "aaa_first", BackupTableStatus.Queued, null, true),
            new SeedBackupTable("sales", "zzz_promoted", BackupTableStatus.Queued, null, true)
        ]);
        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureBackupQueueItemsAsync(backup.Id);
            var promotedTableId = await fixture.Db.BackupTables.Where(x => x.BackupId == backup.Id && x.Table == "zzz_promoted").Select(x => x.Id).SingleAsync();
            await queue.MoveTableAsync(BackupRestoreQueueKind.Backup, promotedTableId, new MoveQueueItemRequest(BackupRestoreQueueMoveDirection.Top));
        }

        await fixture.RunBackupAsync(backup.Id);

        Assert.Equal("zzz_promoted", fixture.ClickHouse.BackupStartTables.First());
    }

    [Fact]
    public async Task Backup_queue_table_move_top_keeps_table_shards_together()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 1, options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "aaa_first", BackupTableStatus.Queued, null, true),
            new SeedBackupTable("sales", "mmm_middle", BackupTableStatus.Queued, null, true),
            new SeedBackupTable("sales", "zzz_promoted", BackupTableStatus.Queued, null, true)
        ], shardCount: 2);

        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureBackupQueueItemsAsync(backup.Id);
            var promotedTableId = await fixture.Db.BackupTables.Where(x => x.BackupId == backup.Id && x.Table == "zzz_promoted").Select(x => x.Id).SingleAsync();
            var minQueuedPosition = await fixture.Db.BackupRestoreQueueItems
                .Where(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == backup.Id && x.StartedAt == null && x.CompletedAt == null)
                .MinAsync(x => x.Position);

            await queue.MoveTableAsync(BackupRestoreQueueKind.Backup, promotedTableId, new MoveQueueItemRequest(BackupRestoreQueueMoveDirection.Top));

            var promotedPositions = await fixture.Db.BackupRestoreQueueItems
                .Where(x => x.Kind == BackupRestoreQueueKind.Backup && x.TableId == promotedTableId)
                .Select(x => x.Position)
                .ToListAsync();
            var otherPositions = await fixture.Db.BackupRestoreQueueItems
                .Where(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == backup.Id && x.TableId != promotedTableId)
                .Select(x => x.Position)
                .ToListAsync();

            var promotedPosition = Assert.Single(promotedPositions.Distinct());
            Assert.Equal(1000, minQueuedPosition);
            Assert.Equal(minQueuedPosition - 1, promotedPosition);
            Assert.True(promotedPosition < otherPositions.Min());
        }
    }

    [Fact]
    public async Task Backup_queue_row_reorder_changes_next_shard()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 1, options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Queued, null, true)
        ], shardCount: 3);
        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureBackupQueueItemsAsync(backup.Id);
            var shardThree = (await queue.ListAsync(BackupRestoreQueueKind.Backup, "queued", 10)).Single(x => x.LogicalShardNumber == 3);
            await queue.MoveItemAsync(shardThree.Id, new MoveQueueItemRequest(BackupRestoreQueueMoveDirection.Top));
        }

        await fixture.RunBackupAsync(backup.Id);

        Assert.Equal(3, fixture.ClickHouse.BackupStartShardNumbers.First());
    }

    [Fact]
    public async Task Forced_backup_rows_bypass_global_maxdop_when_already_forced()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 1, options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.RequiredConcurrentBackupStartsBeforeRelease = 2;
        fixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(60);
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "forced_a", BackupTableStatus.Queued, null, true),
            new SeedBackupTable("sales", "forced_b", BackupTableStatus.Queued, null, true)
        ]);
        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureBackupQueueItemsAsync(backup.Id);
            foreach (var item in await queue.ListAsync(BackupRestoreQueueKind.Backup, "queued", 10))
            {
                await queue.ForceAsync(item.Id);
            }
        }

        await fixture.RunBackupAsync(backup.Id);

        Assert.True(fixture.ClickHouse.MaxConcurrentBackupStarts > 1);
    }

    [Fact]
    public async Task Shard_maxdop_limits_normal_rows_but_forced_rows_bypass_it()
    {
        await using var normalFixture = await TestFixture.CreateAsync(clusterMaxDop: 2, options: new ChoboBackupRestoreOptions { MaxDop = 2, PollInterval = TimeSpan.FromMilliseconds(1) });
        normalFixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(40);
        var normalCluster = await normalFixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == normalFixture.SourceClusterId);
        normalCluster.ShardMaxDopDefault = 1;
        await normalFixture.Db.SaveChangesAsync();
        var normalBackup = await normalFixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "same_shard_a", BackupTableStatus.Queued, null, true),
            new SeedBackupTable("sales", "same_shard_b", BackupTableStatus.Queued, null, true)
        ]);
        await normalFixture.RunBackupAsync(normalBackup.Id);
        Assert.Equal(1, normalFixture.ClickHouse.MaxConcurrentBackupStarts);

        await using var forcedFixture = await TestFixture.CreateAsync(clusterMaxDop: 2, options: new ChoboBackupRestoreOptions { MaxDop = 2, PollInterval = TimeSpan.FromMilliseconds(1) });
        forcedFixture.ClickHouse.RequiredConcurrentBackupStartsBeforeRelease = 2;
        forcedFixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(40);
        var forcedCluster = await forcedFixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == forcedFixture.SourceClusterId);
        forcedCluster.ShardMaxDopDefault = 1;
        await forcedFixture.Db.SaveChangesAsync();
        var forcedBackup = await forcedFixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "forced_same_shard_a", BackupTableStatus.Queued, null, true),
            new SeedBackupTable("sales", "forced_same_shard_b", BackupTableStatus.Queued, null, true)
        ]);
        using (var scope = forcedFixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureBackupQueueItemsAsync(forcedBackup.Id);
            foreach (var item in await queue.ListAsync(BackupRestoreQueueKind.Backup, "queued", 10))
            {
                await queue.ForceAsync(item.Id);
            }
        }
        await forcedFixture.RunBackupAsync(forcedBackup.Id);
        Assert.True(forcedFixture.ClickHouse.MaxConcurrentBackupStarts > 1);
    }

    [Fact]
    public async Task Backup_runner_does_not_reset_live_claim_for_running_backup()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "live_claim_orders", BackupTableStatus.Running, null, true)
        ]);
        var shard = await fixture.Db.BackupTableShards.SingleAsync(x => x.BackupTable!.BackupId == backup.Id);
        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>().EnsureBackupQueueItemsAsync(backup.Id);
        }

        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        await fixture.Db.BackupRestoreQueueItems
            .Where(x => x.Kind == BackupRestoreQueueKind.Backup && x.ShardId == shard.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.StartedAt, startedAt));
        fixture.Db.ChangeTracker.Clear();

        await fixture.RunBackupAsync(backup.Id);

        Assert.Empty(fixture.ClickHouse.BackupStartTables);
        var queueItem = await fixture.Db.BackupRestoreQueueItems.AsNoTracking().SingleAsync(x => x.Kind == BackupRestoreQueueKind.Backup && x.ShardId == shard.Id);
        Assert.NotNull(queueItem.StartedAt);
        Assert.Null(queueItem.CompletedAt);
        var backupAfterRun = await fixture.Db.Backups.AsNoTracking().SingleAsync(x => x.Id == backup.Id);
        var shardAfterRun = await fixture.Db.BackupTableShards.AsNoTracking().SingleAsync(x => x.Id == shard.Id);
        Assert.Equal(BackupRunStatus.Running, backupAfterRun.Status);
        Assert.Equal(BackupTableStatus.Running, shardAfterRun.Status);
    }

    [Fact]
    public async Task Target_update_rejects_active_backup_target_mutation()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "active_target_orders", BackupTableStatus.Running, null, true)
        ]);

        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TargetApplicationService>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateS3Async(
            fixture.TargetId,
            new UpsertS3TargetRequest("minio-renamed", "http://minio:9000", "us-east-1", "data-bucket", "changed-prefix", true, null, null),
            updateSecrets: false));

        Assert.Contains("cannot be updated", ex.Message, StringComparison.OrdinalIgnoreCase);
        fixture.Db.ChangeTracker.Clear();
        var target = await fixture.Db.BackupTargets.AsNoTracking().SingleAsync(x => x.Id == fixture.TargetId);
        var settings = JsonSerializer.Deserialize<S3TargetSettingsDto>(target.SettingsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(settings);
        Assert.Equal("minio", target.Name);
        Assert.Null(settings!.PathPrefix);
    }

    [Fact]
    public async Task Backup_queue_does_not_claim_duplicate_destination_concurrently()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 8, options: new ChoboBackupRestoreOptions { MaxDop = 8, PollInterval = TimeSpan.FromMilliseconds(1) });
        var cluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.SourceClusterId);
        cluster.ShardMaxDopDefault = 8;
        cluster.NodeMaxDopDefault = 8;
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "duplicate_destination_a", BackupTableStatus.Queued, null, true),
            new SeedBackupTable("sales", "duplicate_destination_b", BackupTableStatus.Queued, null, true)
        ]);
        var tableIds = backup.Tables.Select(x => x.Id).ToList();
        var shards = await fixture.Db.BackupTableShards
            .Where(x => tableIds.Contains(x.BackupTableId))
            .OrderBy(x => x.Id)
            .ToListAsync();
        shards[1].StoragePath = shards[0].StoragePath;
        await fixture.Db.SaveChangesAsync();

        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>().EnsureBackupQueueItemsAsync(backup.Id);
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                await start.Task;
                using var scope = fixture.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>().TryTakeNextBackupWorkAsync(backup.Id);
            })
            .ToArray();
        start.SetResult();
        var results = await Task.WhenAll(claims);

        var claimedWork = Assert.Single(results, x => x.WorkItem is not null).WorkItem!;
        Assert.True(results.Where(x => x.WorkItem is null).All(x => x.HasQueuedWork));

        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.MarkCompletedAsync(BackupRestoreQueueKind.Backup, claimedWork.ShardId);
            var next = await queue.TryTakeNextBackupWorkAsync(backup.Id);
            var nextWork = next.WorkItem;
            Assert.NotNull(nextWork);
            Assert.NotEqual(claimedWork.ShardId, nextWork.ShardId);
        }
    }

    [Fact]
    public async Task Backup_queue_does_not_claim_destination_already_started_in_database()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 8, options: new ChoboBackupRestoreOptions { MaxDop = 8, PollInterval = TimeSpan.FromMilliseconds(1) });
        var cluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.SourceClusterId);
        cluster.ShardMaxDopDefault = 8;
        cluster.NodeMaxDopDefault = 8;
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "persisted_destination_a", BackupTableStatus.Queued, null, true),
            new SeedBackupTable("sales", "persisted_destination_b", BackupTableStatus.Queued, null, true)
        ]);
        var tableIds = backup.Tables.Select(x => x.Id).ToList();
        var shards = await fixture.Db.BackupTableShards
            .Where(x => tableIds.Contains(x.BackupTableId))
            .OrderBy(x => x.Id)
            .ToListAsync();
        shards[1].StoragePath = shards[0].StoragePath;
        await fixture.Db.SaveChangesAsync();

        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>().EnsureBackupQueueItemsAsync(backup.Id);
        }

        var activeShardId = shards[0].Id;
        var queuedShardId = shards[1].Id;
        await fixture.Db.BackupRestoreQueueItems
            .Where(x => x.Kind == BackupRestoreQueueKind.Backup && x.ShardId == activeShardId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.StartedAt, DateTimeOffset.UtcNow));
        fixture.Db.ChangeTracker.Clear();

        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            var blocked = await queue.TryTakeNextBackupWorkAsync(backup.Id);
            Assert.Null(blocked.WorkItem);
            Assert.True(blocked.HasQueuedWork);

            await queue.MarkCompletedAsync(BackupRestoreQueueKind.Backup, activeShardId);
            var next = await queue.TryTakeNextBackupWorkAsync(backup.Id);
            Assert.NotNull(next.WorkItem);
            Assert.Equal(queuedShardId, next.WorkItem.ShardId);
        }
    }

    [Fact]
    public async Task Backup_queue_blocks_leading_slash_variant_of_active_destination()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 8, options: new ChoboBackupRestoreOptions { MaxDop = 8, PollInterval = TimeSpan.FromMilliseconds(1) });
        var cluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.SourceClusterId);
        cluster.ShardMaxDopDefault = 8;
        cluster.NodeMaxDopDefault = 8;
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "canonical_destination_a", BackupTableStatus.Queued, null, true),
            new SeedBackupTable("sales", "canonical_destination_b", BackupTableStatus.Queued, null, true)
        ]);
        var tableIds = backup.Tables.Select(x => x.Id).ToList();
        var shards = await fixture.Db.BackupTableShards
            .Where(x => tableIds.Contains(x.BackupTableId))
            .OrderBy(x => x.Id)
            .ToListAsync();
        shards[0].StoragePath = "backups/canonical/same-path";
        shards[1].StoragePath = "/backups/canonical/same-path";
        await fixture.Db.SaveChangesAsync();

        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>().EnsureBackupQueueItemsAsync(backup.Id);
        }

        var activeShardId = shards[0].Id;
        await fixture.Db.BackupRestoreQueueItems
            .Where(x => x.Kind == BackupRestoreQueueKind.Backup && x.ShardId == activeShardId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.StartedAt, DateTimeOffset.UtcNow));
        fixture.Db.ChangeTracker.Clear();

        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            var blocked = await queue.TryTakeNextBackupWorkAsync(backup.Id);
            Assert.Null(blocked.WorkItem);
            Assert.True(blocked.HasQueuedWork);
        }
    }

    [Fact]
    public async Task Backup_queue_blocks_targets_that_resolve_to_same_s3_destination()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 8, options: new ChoboBackupRestoreOptions { MaxDop = 8, PollInterval = TimeSpan.FromMilliseconds(1) });
        var cluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.SourceClusterId);
        cluster.ShardMaxDopDefault = 8;
        cluster.NodeMaxDopDefault = 8;
        var prefixedTargetId = Guid.NewGuid();
        fixture.Db.BackupTargets.Add(CreateS3TargetEntity(prefixedTargetId, "prefixed", "http://minio:9000", "us-east-1", "data-bucket", "prod", true, false));

        var activeBackup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "target_collision_active", BackupTableStatus.Queued, null, true)
        ]);
        var candidateBackup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "target_collision_candidate", BackupTableStatus.Queued, null, true)
        ]);
        candidateBackup.TargetId = prefixedTargetId;
        await fixture.Db.SaveChangesAsync();

        var activeShard = await fixture.Db.BackupTableShards.SingleAsync(x => x.BackupTable!.BackupId == activeBackup.Id);
        var candidateShard = await fixture.Db.BackupTableShards.SingleAsync(x => x.BackupTable!.BackupId == candidateBackup.Id);
        activeShard.StoragePath = "prod/backups/canonical/cross-target";
        candidateShard.StoragePath = "backups/canonical/cross-target";
        await fixture.Db.SaveChangesAsync();

        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureBackupQueueItemsAsync(activeBackup.Id);
            await queue.EnsureBackupQueueItemsAsync(candidateBackup.Id);
        }

        await fixture.Db.BackupRestoreQueueItems
            .Where(x => x.Kind == BackupRestoreQueueKind.Backup && x.ShardId == activeShard.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.StartedAt, DateTimeOffset.UtcNow));
        fixture.Db.ChangeTracker.Clear();

        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            var blocked = await queue.TryTakeNextBackupWorkAsync(candidateBackup.Id);
            Assert.Null(blocked.WorkItem);
            Assert.True(blocked.HasQueuedWork);
        }
    }

    [Fact]
    public async Task Backup_queue_resumes_requeued_operation_before_duplicate_destination()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 8, options: new ChoboBackupRestoreOptions { MaxDop = 8, PollInterval = TimeSpan.FromMilliseconds(1) });
        var cluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.SourceClusterId);
        cluster.ShardMaxDopDefault = 8;
        cluster.NodeMaxDopDefault = 8;
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "requeued_destination_a", BackupTableStatus.Queued, "op-requeued-a", true),
            new SeedBackupTable("sales", "requeued_destination_b", BackupTableStatus.Queued, null, true)
        ]);
        var tableIds = backup.Tables.Select(x => x.Id).ToList();
        var shards = await fixture.Db.BackupTableShards
            .Where(x => tableIds.Contains(x.BackupTableId))
            .ToListAsync();
        var activeShard = shards.Single(x => x.ClickHouseOperationId == "op-requeued-a");
        var duplicateShard = shards.Single(x => x.ClickHouseOperationId is null);
        var activeShardId = activeShard.Id;
        var duplicateShardId = duplicateShard.Id;
        duplicateShard.StoragePath = activeShard.StoragePath;
        await fixture.Db.SaveChangesAsync();

        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureBackupQueueItemsAsync(backup.Id);
            var duplicateItem = (await queue.ListAsync(BackupRestoreQueueKind.Backup, "queued", 10)).Single(x => x.ShardId == duplicateShardId);
            await queue.ForceAsync(duplicateItem.Id);
        }

        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            var resumed = await queue.TryTakeNextBackupWorkAsync(backup.Id);
            Assert.NotNull(resumed.WorkItem);
            Assert.Equal(activeShardId, resumed.WorkItem.ShardId);
            Assert.NotEqual(duplicateShardId, resumed.WorkItem.ShardId);

            await queue.MarkCompletedAsync(BackupRestoreQueueKind.Backup, activeShardId);

            await fixture.Db.BackupTableShards
                .Where(x => x.Id == activeShardId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, BackupTableStatus.Succeeded)
                    .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow));
            fixture.Db.ChangeTracker.Clear();

            var next = await queue.TryTakeNextBackupWorkAsync(backup.Id);
            Assert.NotNull(next.WorkItem);
            Assert.Equal(duplicateShardId, next.WorkItem.ShardId);
        }
    }

    [Fact]
    public async Task Node_maxdop_selects_different_replica_when_one_replica_is_busy()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 2, options: new ChoboBackupRestoreOptions { MaxDop = 2, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(80);
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "single", 1, "replica-a", 9000, false, 0),
            new ClickHouseShardReplicaInfo(1, "single", 2, "replica-b", 9000, false, 0)
        ]);
        var cluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.SourceClusterId);
        cluster.NodeMaxDopDefault = 1;
        cluster.ShardMaxDopDefault = 2;
        await fixture.Db.SaveChangesAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "replica_choice_a", BackupTableStatus.Queued, null, true),
            new SeedBackupTable("sales", "replica_choice_b", BackupTableStatus.Queued, null, true)
        ]);

        MarkBackupAsReplicated(backup);
        await fixture.Db.SaveChangesAsync();

        await fixture.RunBackupAsync(backup.Id);

        Assert.Equal(2, fixture.ClickHouse.BackupStartEndpoints.Select(x => x.Host).Distinct().Count());
    }

    [Fact]
    public async Task Backup_replica_selection_uses_available_replica_when_another_replica_is_unavailable()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 1, options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "single", 1, "replica-a", 9000, false, 0),
            new ClickHouseShardReplicaInfo(1, "single", 2, "replica-b", 9000, false, 0)
        ]);
        fixture.ClickHouse.UnavailableVersionEndpoints.Add("replica-a:9000:False");
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "available_replica_choice", BackupTableStatus.Queued, null, true)
        ]);
        MarkBackupAsReplicated(backup);
        await fixture.Db.SaveChangesAsync();

        await fixture.RunBackupAsync(backup.Id);

        Assert.Equal("replica-b", Assert.Single(fixture.ClickHouse.BackupStartEndpoints).Host);
        var probedHosts = fixture.ClickHouse.EndpointExecuteSql
            .Where(x => string.Equals(x.Sql, "SELECT version()", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Endpoint.Host)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["replica-a", "replica-b"], probedHosts);
    }

    [Fact]
    public async Task Backup_replica_selection_falls_back_to_all_replicas_when_none_are_available()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 1, options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "single", 1, "replica-a", 9000, false, 0),
            new ClickHouseShardReplicaInfo(1, "single", 2, "replica-b", 9000, false, 0)
        ]);
        fixture.ClickHouse.UnavailableVersionEndpoints.Add("replica-a:9000:False");
        fixture.ClickHouse.UnavailableVersionEndpoints.Add("replica-b:9000:False");
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "all_unavailable_replica_choice", BackupTableStatus.Queued, null, true)
        ]);
        MarkBackupAsReplicated(backup);
        await fixture.Db.SaveChangesAsync();

        await fixture.RunBackupAsync(backup.Id);

        var selected = Assert.Single(fixture.ClickHouse.BackupStartEndpoints);
        Assert.Contains(selected.Host, new[] { "replica-a", "replica-b" });
        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backup.Id);
        Assert.Equal(BackupRunStatus.Succeeded, completed.Status);
        Assert.Equal(["replica-a", "replica-b"], fixture.ClickHouse.EndpointExecuteSql
            .Where(x => string.Equals(x.Sql, "SELECT version()", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Endpoint.Host)
            .Order(StringComparer.Ordinal)
            .ToList());
    }

    [Fact]
    public async Task Backup_random_replica_selection_does_not_always_pick_first_replica()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 1, options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "single", 1, "replica-a", 9000, false, 0),
            new ClickHouseShardReplicaInfo(1, "single", 2, "replica-b", 9000, false, 0),
            new ClickHouseShardReplicaInfo(1, "single", 3, "replica-c", 9000, false, 0)
        ]);

        for (var i = 0; i < 20; i++)
        {
            var backup = await fixture.SeedBackupWithTablesAsync([
                new SeedBackupTable("sales", $"random_{i}", BackupTableStatus.Queued, null, true)
            ]);
            MarkBackupAsReplicated(backup);
            await fixture.Db.SaveChangesAsync();
            await fixture.RunBackupAsync(backup.Id);
        }

        Assert.True(fixture.ClickHouse.BackupStartEndpoints.Select(x => x.Host).Distinct().Count() >= 2);
    }

    [Fact]
    public async Task Restore_queue_table_reorder_changes_restore_execution_order()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 1, options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "aaa_restore", BackupTableStatus.Succeeded, "backup-op-a", true),
            new SeedBackupTable("sales", "zzz_restore", BackupTableStatus.Succeeded, "backup-op-z", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "aaa_restore", RestoreTableStatus.Queued, null),
            new SeedRestoreTable("sales", "zzz_restore", RestoreTableStatus.Queued, null)
        ]);
        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureRestoreQueueItemsAsync(restore.Id);
            var promotedTableId = await fixture.Db.RestoreTables.Where(x => x.RestoreId == restore.Id && x.SourceTable == "zzz_restore").Select(x => x.Id).SingleAsync();
            await queue.MoveTableAsync(BackupRestoreQueueKind.Restore, promotedTableId, new MoveQueueItemRequest(BackupRestoreQueueMoveDirection.Top));
        }

        await fixture.RunRestoreAsync(restore.Id);

        Assert.Equal("zzz_restore", fixture.ClickHouse.RestoreStartTables.First());
    }

    [Fact]
    public async Task Restore_queue_row_reorder_changes_next_shard()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 1, options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders_restore_rows", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 3);
        backup.Status = BackupRunStatus.Succeeded;
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "orders_restore_rows", RestoreTableStatus.Queued, null)
        ]);
        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureRestoreQueueItemsAsync(restore.Id);
            var shardThree = (await queue.ListAsync(BackupRestoreQueueKind.Restore, "queued", 10)).Single(x => x.LogicalShardNumber == 3);
            await queue.MoveItemAsync(shardThree.Id, new MoveQueueItemRequest(BackupRestoreQueueMoveDirection.Top));
        }

        await fixture.RunRestoreAsync(restore.Id);

        Assert.Equal(3, fixture.ClickHouse.RestoreStartShardNumbers.First());
    }

    [Fact]
    public async Task Restore_per_cluster_maxdop_limits_concurrency()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 4, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(40);
        var targetCluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.TargetClusterId);
        targetCluster.BackupRestoreMaxDop = 2;
        targetCluster.NodeMaxDopDefault = 16;
        targetCluster.ShardMaxDopDefault = 16;
        await fixture.Db.SaveChangesAsync();
        fixture.ClickHouse.RequiredConcurrentRestoreStartsBeforeRelease = 2;
        var backup = await fixture.SeedBackupWithTablesAsync(Enumerable.Range(0, 5)
            .Select(i => new SeedBackupTable("sales", $"restore_cluster_dop_{i}", BackupTableStatus.Succeeded, $"backup-op-{i}", true))
            .ToList());
        backup.Status = BackupRunStatus.Succeeded;
        var restore = await fixture.SeedRestoreAsync(backup, backup.Tables
            .Select(t => new SeedRestoreTable(t.Database, t.Table, RestoreTableStatus.Queued, null))
            .ToList());

        await fixture.RunRestoreAsync(restore.Id);

        Assert.Equal(2, fixture.ClickHouse.MaxConcurrentRestoreStarts);
    }

    [Fact]
    public async Task Restore_shards_for_same_table_can_run_concurrently_when_dop_allows()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 2, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(80);
        var targetCluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.TargetClusterId);
        targetCluster.BackupRestoreMaxDop = 2;
        targetCluster.NodeMaxDopDefault = 16;
        targetCluster.ShardMaxDopDefault = 16;
        await fixture.Db.SaveChangesAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "restore_parallel_shards", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 2);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "restore_parallel_shards", RestoreTableStatus.Queued, null)
        ]);
        foreach (var shard in restore.Tables.Single().Shards)
        {
            shard.TargetHost = $"restore-s{shard.TargetShardNumber}";
        }
        await fixture.Db.SaveChangesAsync();

        await fixture.RunRestoreAsync(restore.Id);

        Assert.Equal(2, fixture.ClickHouse.MaxConcurrentRestoreStarts);
        Assert.Equal([1, 2], fixture.ClickHouse.RestoreStartShardNumbers.Order().ToArray());
    }

    [Fact]
    public async Task Restore_shard_maxdop_limits_normal_rows_but_forced_rows_bypass_it()
    {
        await using var normalFixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 2, PollInterval = TimeSpan.FromMilliseconds(1) });
        normalFixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(40);
        var normalCluster = await normalFixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == normalFixture.TargetClusterId);
        normalCluster.BackupRestoreMaxDop = 2;
        normalCluster.NodeMaxDopDefault = 16;
        normalCluster.ShardMaxDopDefault = 1;
        await normalFixture.Db.SaveChangesAsync();
        var normalBackup = await normalFixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "restore_same_shard_a", BackupTableStatus.Succeeded, "backup-op-a", true),
            new SeedBackupTable("sales", "restore_same_shard_b", BackupTableStatus.Succeeded, "backup-op-b", true)
        ]);
        normalBackup.Status = BackupRunStatus.Succeeded;
        var normalRestore = await normalFixture.SeedRestoreAsync(normalBackup, normalBackup.Tables
            .Select(t => new SeedRestoreTable(t.Database, t.Table, RestoreTableStatus.Queued, null))
            .ToList());
        await normalFixture.RunRestoreAsync(normalRestore.Id);
        Assert.Equal(1, normalFixture.ClickHouse.MaxConcurrentRestoreStarts);

        await using var forcedFixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 2, PollInterval = TimeSpan.FromMilliseconds(1) });
        forcedFixture.ClickHouse.RequiredConcurrentRestoreStartsBeforeRelease = 2;
        forcedFixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(40);
        var forcedCluster = await forcedFixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == forcedFixture.TargetClusterId);
        forcedCluster.BackupRestoreMaxDop = 2;
        forcedCluster.NodeMaxDopDefault = 16;
        forcedCluster.ShardMaxDopDefault = 1;
        await forcedFixture.Db.SaveChangesAsync();
        var forcedBackup = await forcedFixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "restore_forced_same_shard_a", BackupTableStatus.Succeeded, "backup-op-a", true),
            new SeedBackupTable("sales", "restore_forced_same_shard_b", BackupTableStatus.Succeeded, "backup-op-b", true)
        ]);
        forcedBackup.Status = BackupRunStatus.Succeeded;
        var forcedRestore = await forcedFixture.SeedRestoreAsync(forcedBackup, forcedBackup.Tables
            .Select(t => new SeedRestoreTable(t.Database, t.Table, RestoreTableStatus.Queued, null))
            .ToList());
        using (var scope = forcedFixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureRestoreQueueItemsAsync(forcedRestore.Id);
            foreach (var item in await queue.ListAsync(BackupRestoreQueueKind.Restore, "queued", 10))
            {
                await queue.ForceAsync(item.Id);
            }
        }
        await forcedFixture.RunRestoreAsync(forcedRestore.Id);
        Assert.True(forcedFixture.ClickHouse.MaxConcurrentRestoreStarts > 1);
    }

    [Fact]
    public async Task Restore_node_maxdop_selects_different_replica_when_one_replica_is_busy()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 2, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.StartDelay = TimeSpan.FromMilliseconds(80);
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "single", 1, "restore-a", 9000, false, 0),
            new ClickHouseShardReplicaInfo(1, "single", 2, "restore-b", 9000, false, 0)
        ]);
        var cluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.TargetClusterId);
        cluster.BackupRestoreMaxDop = 2;
        cluster.NodeMaxDopDefault = 1;
        cluster.ShardMaxDopDefault = 2;
        await fixture.Db.SaveChangesAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "restore_replica_choice_a", BackupTableStatus.Succeeded, "backup-op-a", true),
            new SeedBackupTable("sales", "restore_replica_choice_b", BackupTableStatus.Succeeded, "backup-op-b", true)
        ]);
        MarkBackupAsReplicated(backup);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        var restore = await fixture.SeedRestoreAsync(backup, backup.Tables
            .Select(t => new SeedRestoreTable(t.Database, t.Table, RestoreTableStatus.Queued, null))
            .ToList());

        await fixture.RunRestoreAsync(restore.Id);

        Assert.Equal(2, fixture.ClickHouse.RestoreStartEndpoints.Select(x => x.Host).Distinct().Count());
    }

    [Fact]
    public async Task Restore_random_replica_selection_does_not_always_pick_first_replica()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "single", 1, "restore-a", 9000, false, 0),
            new ClickHouseShardReplicaInfo(1, "single", 2, "restore-b", 9000, false, 0),
            new ClickHouseShardReplicaInfo(1, "single", 3, "restore-c", 9000, false, 0)
        ]);

        for (var i = 0; i < 20; i++)
        {
            var backup = await fixture.SeedBackupWithTablesAsync([
                new SeedBackupTable("sales", $"restore_random_{i}", BackupTableStatus.Succeeded, $"backup-op-{i}", true)
            ]);
            MarkBackupAsReplicated(backup);
            backup.Status = BackupRunStatus.Succeeded;
            await fixture.Db.SaveChangesAsync();
            var restore = await fixture.SeedRestoreAsync(backup, [
                new SeedRestoreTable("sales", $"restore_random_{i}", RestoreTableStatus.Queued, null)
            ]);
            await fixture.RunRestoreAsync(restore.Id);
        }

        Assert.True(fixture.ClickHouse.RestoreStartEndpoints.Select(x => x.Host).Distinct().Count() >= 2);
    }

    [Fact]
    public async Task Restore_single_instance_defaults_drain_queue_successfully()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "restore_single_instance", BackupTableStatus.Succeeded, "backup-op", true)
        ], shardCount: 2);
        backup.Status = BackupRunStatus.Succeeded;
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "restore_single_instance", RestoreTableStatus.Queued, null)
        ]);

        await fixture.RunRestoreAsync(restore.Id);

        fixture.Db.ChangeTracker.Clear();
        var restored = await fixture.Db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == restore.Id);
        Assert.Equal(RestoreRunStatus.Succeeded, restored.Status);
        Assert.All(restored.Tables.SelectMany(x => x.Shards), shard => Assert.Equal(RestoreTableStatus.Succeeded, shard.Status));
        Assert.Equal(1, fixture.ClickHouse.MaxConcurrentRestoreStarts);
    }

    [Fact]
    public async Task Restore_runner_skips_duplicate_execution_for_same_restore_id()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "restore_duplicate", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "restore_duplicate", RestoreTableStatus.Queued, null)
        ]);
        fixture.ClickHouse.BlockOperationStatus = true;

        var firstRun = fixture.RunRestoreAsync(restore.Id);
        await fixture.ClickHouse.WaitForBlockedStatusAsync();
        await fixture.RunRestoreAsync(restore.Id);

        fixture.ClickHouse.ReleaseBlockedStatus();
        await firstRun;

        Assert.Single(fixture.ClickHouse.RestoreStartTables);
        Assert.Equal(1, await fixture.Db.AuditEntries.CountAsync(x => x.Action == "started" && x.EntityType == "restore" && x.EntityId == restore.Id.ToString()));
    }

    [Fact]
    public async Task Backup_runner_skips_duplicate_execution_for_same_backup_id()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.BlockOperationStatus = true;
        var backupId = await fixture.CreateManualBackupAsync();

        var firstRun = fixture.RunBackupAsync(backupId);
        await fixture.ClickHouse.WaitForBlockedStatusAsync();
        await fixture.RunBackupAsync(backupId);

        fixture.ClickHouse.ReleaseBlockedStatus();
        await firstRun;

        Assert.Single(fixture.ClickHouse.BackupStartTables);
        Assert.Equal(1, await fixture.Db.AuditEntries.CountAsync(x => x.Action == "started" && x.EntityType == "backup" && x.EntityId == backupId.ToString()));
    }

    [Fact]
    public async Task Queue_operation_move_promotes_all_queued_items_for_backup()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var first = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "first", BackupTableStatus.Queued, null, true)
        ], shardCount: 2);
        var second = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "second", BackupTableStatus.Queued, null, true)
        ], shardCount: 2);

        using var scope = fixture.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
        await queue.EnsureBackupQueueItemsAsync(first.Id);
        await queue.EnsureBackupQueueItemsAsync(second.Id);
        await queue.MoveOperationAsync(BackupRestoreQueueKind.Backup, second.Id, new MoveQueueItemRequest(BackupRestoreQueueMoveDirection.Top));

        var firstPositions = await fixture.Db.BackupRestoreQueueItems.Where(x => x.OperationId == first.Id).Select(x => x.Position).ToListAsync();
        var secondPositions = await fixture.Db.BackupRestoreQueueItems.Where(x => x.OperationId == second.Id).Select(x => x.Position).ToListAsync();
        Assert.True(secondPositions.Max() < firstPositions.Min());
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "queue-operation-moved" && x.EntityId == second.Id.ToString()));
    }

    [Fact]
    public async Task Garbage_collector_prunes_completed_queue_rows_before_new_positions_are_assigned()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var completed = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "completed", BackupTableStatus.Queued, null, true)
        ], shardCount: 1);
        var next = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "next", BackupTableStatus.Queued, null, true)
        ], shardCount: 1);

        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>().EnsureBackupQueueItemsAsync(completed.Id);
        }
        await fixture.Db.BackupRestoreQueueItems
            .Where(x => x.OperationId == completed.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow));

        await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().RunOnceAsync();

        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>().EnsureBackupQueueItemsAsync(next.Id);
        }

        var row = await fixture.Db.BackupRestoreQueueItems.SingleAsync(x => x.OperationId == next.Id);
        Assert.Equal(1000, row.Position);
        Assert.False(await fixture.Db.BackupRestoreQueueItems.AnyAsync(x => x.OperationId == completed.Id));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "queue-completed-pruned" && x.EntityType == "backup-garbage-collector"));
    }

    [Fact]
    public async Task Garbage_collector_prunes_completed_restore_rows_and_keeps_active_queue_rows()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var restoreSourceBackup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "completed_restore", BackupTableStatus.Succeeded, "backup-complete", true)
        ], shardCount: 1);
        var activeBackup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "active_backup", BackupTableStatus.Queued, null, true)
        ], shardCount: 1);
        var restore = await fixture.SeedRestoreAsync(restoreSourceBackup, [
            new SeedRestoreTable("sales", "completed_restore", RestoreTableStatus.Succeeded, "restore-complete")
        ]);
        var next = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "next_after_active", BackupTableStatus.Queued, null, true)
        ], shardCount: 1);

        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureBackupQueueItemsAsync(activeBackup.Id);
            await queue.EnsureRestoreQueueItemsAsync(restore.Id);
        }

        await fixture.Db.BackupRestoreQueueItems
            .Where(x => x.Kind == BackupRestoreQueueKind.Restore && x.OperationId == restore.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow));
        await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().RunOnceAsync();

        Assert.False(await fixture.Db.BackupRestoreQueueItems.AnyAsync(x => x.Kind == BackupRestoreQueueKind.Restore && x.OperationId == restore.Id));
        var activeRow = await fixture.Db.BackupRestoreQueueItems.SingleAsync(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == activeBackup.Id);
        Assert.Null(activeRow.CompletedAt);

        using (var scope = fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>().EnsureBackupQueueItemsAsync(next.Id);
        }

        var nextRow = await fixture.Db.BackupRestoreQueueItems.SingleAsync(x => x.OperationId == next.Id);
        Assert.True(nextRow.Position > activeRow.Position);
    }

    [Fact]
    public async Task Schema_only_backup_does_not_leave_active_queue_rows()
    {
        await using var fixture = await TestFixture.CreateAsync(clusterMaxDop: 1, options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        fixture.ClickHouse.Inventory.Add(Table("sales", "schema_a", "MergeTree"));
        fixture.ClickHouse.Inventory.Add(Table("sales", "schema_b", "MergeTree"));
        var backupId = await fixture.CreateManualBackupAsync(schemaOnly: true);

        await fixture.RunBackupAsync(backupId);

        using var scope = fixture.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
        Assert.Empty(await queue.ListAsync(BackupRestoreQueueKind.Backup, "active", 100));
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
    public async Task Backup_resume_reclaims_started_queue_row_and_continues_known_operation()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "resume_started_queue", BackupTableStatus.Running, "backup-known-started", true)
        ]);
        fixture.ClickHouse.KnownOperations.Add("backup-known-started", "BACKUP_CREATED");
        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureBackupQueueItemsAsync(backup.Id);
        }
        var queueRow = await fixture.Db.BackupRestoreQueueItems.SingleAsync(x => x.Kind == BackupRestoreQueueKind.Backup && x.OperationId == backup.Id);
        queueRow.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        queueRow.NodeHost = "source";
        queueRow.NodePort = 9000;
        queueRow.NodeUseTls = false;
        await fixture.Db.SaveChangesAsync();

        await fixture.RunBackupAsync(backup.Id);

        fixture.Db.ChangeTracker.Clear();
        var after = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backup.Id);
        Assert.Equal(BackupRunStatus.Succeeded, after.Status);
        Assert.All(after.Tables.SelectMany(x => x.Shards), shard => Assert.Equal(BackupTableStatus.Succeeded, shard.Status));
        Assert.Empty(fixture.ClickHouse.BackupStartTables);
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
    public async Task Restore_resume_reclaims_started_queue_row_and_continues_known_operation()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "resume_started_queue", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "resume_started_queue", RestoreTableStatus.Running, "restore-known-started")
        ]);
        fixture.ClickHouse.KnownOperations.Add("restore-known-started", "RESTORED");
        using (var scope = fixture.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            await queue.EnsureRestoreQueueItemsAsync(restore.Id);
        }
        var queueRow = await fixture.Db.BackupRestoreQueueItems.SingleAsync(x => x.Kind == BackupRestoreQueueKind.Restore && x.OperationId == restore.Id);
        queueRow.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        queueRow.NodeHost = "restore";
        queueRow.NodePort = 9000;
        queueRow.NodeUseTls = false;
        await fixture.Db.SaveChangesAsync();

        await fixture.RunRestoreAsync(restore.Id);

        fixture.Db.ChangeTracker.Clear();
        var after = await fixture.Db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == restore.Id);
        Assert.Equal(RestoreRunStatus.Succeeded, after.Status);
        Assert.All(after.Tables.SelectMany(x => x.Shards), shard => Assert.Equal(RestoreTableStatus.Succeeded, shard.Status));
        Assert.Empty(fixture.ClickHouse.RestoreStartTables);
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
    public async Task Policy_delete_soft_deletes_related_schedules_and_include_deleted_lists_them()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        var service = fixture.Services.GetRequiredService<PolicyApplicationService>();

        Assert.True(await service.RemoveAsync(schedule.PolicyId));

        fixture.Db.ChangeTracker.Clear();
        var policy = await fixture.Db.BackupPolicies.SingleAsync(x => x.Id == schedule.PolicyId);
        var deletedSchedule = await fixture.Db.BackupSchedules.SingleAsync(x => x.Id == schedule.Id);
        Assert.True(policy.IsDeleted);
        Assert.NotNull(policy.DeletedAt);
        Assert.True(deletedSchedule.IsDeleted);
        Assert.NotNull(deletedSchedule.DeletedAt);
        Assert.DoesNotContain(await service.ListAsync(), x => x.Id == schedule.PolicyId);
        Assert.Contains(await service.ListAsync(includeDeleted: true), x => x.Id == schedule.PolicyId && x.IsDeleted);
        var scheduleService = fixture.Services.GetRequiredService<ScheduleApplicationService>();
        Assert.DoesNotContain(await scheduleService.ListAsync(), x => x.Id == schedule.Id);
        Assert.Contains(await scheduleService.ListAsync(includeDeleted: true), x => x.Id == schedule.Id && x.IsDeleted);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "delete" && x.EntityType == "backup-policy" && x.EntityId == schedule.PolicyId.ToString()));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "delete" && x.EntityType == "backup-schedule" && x.EntityId == schedule.Id.ToString()));
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
    public async Task Scheduler_skips_and_audits_stale_active_schedule_with_deleted_policy()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        schedule.CronExpression = "0/1 * * * * ?";
        schedule.Policy!.IsDeleted = true;
        schedule.Policy.DeletedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<BackupSchedulerDispatcherBackgroundService>().DispatchOnceAsync();

        Assert.False(await fixture.Db.Backups.AnyAsync(x => x.ScheduleId == schedule.Id));
        var audit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "schedule-skip-inactive-policy" && x.EntityId == schedule.Id.ToString());
        Assert.Contains(schedule.PolicyId.ToString(), audit.Details);
        Assert.Contains("error", audit.Details);
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
        Assert.Equal(BackupContentMode.SchemaAndData, backup.ContentMode);
        var queues = fixture.Services.GetRequiredService<IBackupRestoreQueues>();
        Assert.True(queues.Backups.Reader.TryRead(out var queuedBackupId));
        Assert.Equal(backup.Id, queuedBackupId);
        Assert.False(queues.SchemaOnlyBackups.Reader.TryRead(out _));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "scheduled-backup-enqueued" && x.EntityId == schedule.Id.ToString()));
    }

    [Fact]
    public async Task Scheduler_enqueues_schema_only_policy_independently_from_data_backups()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var schedule = await fixture.SeedPolicyAndScheduleAsync(BackupContentMode.SchemaOnly);
        schedule.CronExpression = "0/1 * * * * ?";
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<BackupSchedulerDispatcherBackgroundService>().DispatchOnceAsync();

        var backup = await fixture.Db.Backups.SingleAsync(x => x.ScheduleId == schedule.Id);
        Assert.Equal(BackupType.Full, backup.BackupType);
        Assert.Equal(BackupContentMode.SchemaOnly, backup.ContentMode);
        var queues = fixture.Services.GetRequiredService<IBackupRestoreQueues>();
        Assert.True(queues.SchemaOnlyBackups.Reader.TryRead(out var queuedBackupId));
        Assert.Equal(backup.Id, queuedBackupId);
        Assert.False(queues.Backups.Reader.TryRead(out _));
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
    public async Task Dashboard_reports_recent_missing_backups_from_missed_schedule_audits()
    {
        var now = new DateTimeOffset(2026, 5, 11, 10, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        var scheduleEntityType = AuditEntityTypes.ToStorageValue(AuditEntityType.BackupSchedule);
        var olderPlannedRun = now.AddHours(-2);
        var newerPlannedRun = now.AddMinutes(-30);

        fixture.Db.AuditEntries.AddRange(
            new AuditEntryEntity
            {
                Timestamp = now.AddMinutes(-20),
                ActorName = "system",
                Action = "scheduled-backup-missed",
                EntityType = scheduleEntityType,
                EntityId = schedule.Id.ToString(),
                Details = JsonSerializer.Serialize(new { plannedRunAt = newerPlannedRun, detectedAt = now.AddMinutes(-20), latenessSeconds = 600.0, gracePeriodSeconds = 300.0 })
            },
            new AuditEntryEntity
            {
                Timestamp = now.AddHours(-1),
                ActorName = "system",
                Action = "scheduled-backup-missed",
                EntityType = scheduleEntityType,
                EntityId = schedule.Id.ToString(),
                Details = JsonSerializer.Serialize(new { plannedRunAt = olderPlannedRun, detectedAt = now.AddHours(-1), latenessSeconds = 1800.0, gracePeriodSeconds = 300.0 })
            },
            new AuditEntryEntity
            {
                Timestamp = now.AddHours(-30),
                ActorName = "system",
                Action = "scheduled-backup-missed",
                EntityType = scheduleEntityType,
                EntityId = schedule.Id.ToString(),
                Details = JsonSerializer.Serialize(new { plannedRunAt = now.AddHours(-31), detectedAt = now.AddHours(-30), latenessSeconds = 3600.0, gracePeriodSeconds = 300.0 })
            });
        await fixture.Db.SaveChangesAsync();

        var missing = await fixture.Services.GetRequiredService<DashboardApplicationService>().GetMissingBackupsAsync(hours: 24);

        Assert.Equal(2, missing.Count);
        Assert.Equal(newerPlannedRun, missing[0].PlannedRunAt);
        Assert.Equal(olderPlannedRun, missing[1].PlannedRunAt);
        Assert.All(missing, item =>
        {
            Assert.Equal(schedule.Id, item.ScheduleId);
            Assert.Equal("hourly", item.ScheduleName);
            Assert.Equal(schedule.PolicyId, item.PolicyId);
            Assert.Equal("hourly", item.PolicyName);
            Assert.Equal(BackupType.Full, item.BackupType);
            Assert.Equal(300.0, item.GracePeriodSeconds);
        });
        Assert.Equal(600.0, missing[0].LatenessSeconds);
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
    public async Task Dashboard_future_schedules_do_not_duplicate_fixed_second_and_minute_occurrences()
    {
        var now = new DateTimeOffset(2026, 5, 11, 13, 59, 59, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        schedule.CronExpression = "0 0 14 * * ?";
        schedule.CreatedAt = now.AddDays(-1);
        await fixture.Db.SaveChangesAsync();

        var dashboard = await fixture.Services.GetRequiredService<DashboardApplicationService>().GetDashboardAsync(nextHours: 1);

        var plannedRuns = dashboard.FutureSchedules.Where(x => x.ScheduleId == schedule.Id).Select(x => x.PlannedRunAt).ToList();
        Assert.Equal([new DateTimeOffset(2026, 5, 11, 14, 0, 0, TimeSpan.Zero)], plannedRuns);
    }

    [Fact]
    public async Task Scheduler_uses_exact_fixed_second_and_minute_occurrence_for_due_cron()
    {
        var now = new DateTimeOffset(2026, 5, 11, 14, 1, 1, TimeSpan.Zero);
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
        schedule.CronExpression = "0 0 14 * * ?";
        schedule.CreatedAt = now.AddDays(-1);
        await fixture.Db.SaveChangesAsync();

        var scheduler = fixture.Services.GetRequiredService<BackupSchedulerDispatcherBackgroundService>();
        await scheduler.DispatchOnceAsync();
        await scheduler.DispatchOnceAsync();

        Assert.Equal(1, await fixture.Db.Backups.CountAsync(x => x.ScheduleId == schedule.Id));
        var audit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "scheduled-backup-enqueued" && x.EntityId == schedule.Id.ToString());
        Assert.Contains("2026-05-11T14:00:00", audit.Details);
        Assert.DoesNotContain("2026-05-11T14:01:01", audit.Details);
    }

    [Fact]
    public async Task Dashboard_reports_active_runs_schedule_history_future_runs_and_policy_freshness()
    {
        var now = new DateTimeOffset(2026, 5, 11, 10, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var schedule = await fixture.SeedPolicyAndScheduleAsync();
        var completedAt = now.AddMinutes(-5);
        var partialCompletedAt = now.AddMinutes(-3);
        var scheduleEntityType = AuditEntityTypes.ToStorageValue(AuditEntityType.BackupSchedule);
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
                Status = BackupRunStatus.PartiallySucceeded,
                SourceClusterId = fixture.SourceClusterId,
                TargetId = fixture.TargetId,
                PolicyId = schedule.PolicyId,
                ScheduleId = schedule.Id,
                CreatedAt = partialCompletedAt.AddMinutes(-1),
                StartedAt = partialCompletedAt.AddMinutes(-1),
                CompletedAt = partialCompletedAt
            },
            new BackupEntity
            {
                TriggerType = BackupTriggerType.Scheduled,
                Status = BackupRunStatus.Failed,
                SourceClusterId = fixture.SourceClusterId,
                TargetId = fixture.TargetId,
                PolicyId = schedule.PolicyId,
                ScheduleId = schedule.Id,
                CreatedAt = now.AddMinutes(-31),
                StartedAt = now.AddMinutes(-31),
                CompletedAt = now.AddMinutes(-30)
            },
            new BackupEntity
            {
                TriggerType = BackupTriggerType.Scheduled,
                Status = BackupRunStatus.Failed,
                SourceClusterId = fixture.SourceClusterId,
                TargetId = fixture.TargetId,
                PolicyId = schedule.PolicyId,
                ScheduleId = schedule.Id,
                CreatedAt = now.AddHours(-2).AddMinutes(-1),
                StartedAt = now.AddHours(-2).AddMinutes(-1),
                CompletedAt = now.AddHours(-2)
            },
            new BackupEntity
            {
                TriggerType = BackupTriggerType.Scheduled,
                Status = BackupRunStatus.Failed,
                SourceClusterId = fixture.SourceClusterId,
                TargetId = fixture.TargetId,
                PolicyId = schedule.PolicyId,
                ScheduleId = schedule.Id,
                CreatedAt = now.AddHours(-25).AddMinutes(-1),
                StartedAt = now.AddHours(-25).AddMinutes(-1),
                CompletedAt = now.AddHours(-25)
            },
            new BackupEntity
            {
                TriggerType = BackupTriggerType.Scheduled,
                Status = BackupRunStatus.Running,
                SourceClusterId = fixture.SourceClusterId,
                TargetId = fixture.TargetId,
                PolicyId = schedule.PolicyId,
                ScheduleId = schedule.Id,
                CreatedAt = now.AddSeconds(-10),
                StartedAt = now.AddSeconds(-8)
            });
        fixture.Db.AuditEntries.AddRange(
            new AuditEntryEntity
            {
                Timestamp = now.AddMinutes(-30),
                ActorName = "system",
                Action = "scheduled-backup-missed",
                EntityType = scheduleEntityType,
                EntityId = schedule.Id.ToString(),
                Details = JsonSerializer.Serialize(new { plannedRunAt = now.AddMinutes(-40) })
            },
            new AuditEntryEntity
            {
                Timestamp = now.AddHours(-2),
                ActorName = "system",
                Action = "scheduled-backup-missed",
                EntityType = scheduleEntityType,
                EntityId = schedule.Id.ToString(),
                Details = JsonSerializer.Serialize(new { plannedRunAt = now.AddHours(-2).AddMinutes(-10) })
            },
            new AuditEntryEntity
            {
                Timestamp = now.AddHours(-25),
                ActorName = "system",
                Action = "scheduled-backup-missed",
                EntityType = scheduleEntityType,
                EntityId = schedule.Id.ToString(),
                Details = JsonSerializer.Serialize(new { plannedRunAt = now.AddHours(-25).AddMinutes(-10) })
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
        Assert.Equal(completedAt, summary.LastSuccessfulRunCompletedAt.Value);
        Assert.NotEmpty(dashboard.FutureSchedules);
        Assert.All(dashboard.FutureSchedules, x => Assert.Equal(schedule.Id, x.ScheduleId));

        Assert.Equal(9, metrics.Count);
        Assert.Equal(300, metrics["Policies.TimeSecondsSinceLastPolicyBackup.hourly"]);
        Assert.Equal(1, metrics["Policies.PartialBackups.hourly"]);
        Assert.Equal(3, metrics["Policies.FailedBackups.hourly"]);
        Assert.Equal(300, metrics["TimeSecondsSinceLastPolicySuccessRun.hourly"]);
        Assert.Equal(180, metrics["TimeSecondsSinceLastPolicySemiSuccessRun.hourly"]);
        Assert.Equal(1, metrics["MissedBackupsLastHour.hourly"]);
        Assert.Equal(2, metrics["MissedBackupsLast24Hours.hourly"]);
        Assert.Equal(1, metrics["NumFailedBackupsLastHour.hourly"]);
        Assert.Equal(2, metrics["NumFailedBackupsLast24Hours.hourly"]);
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
    public async Task Retention_min_full_backups_counts_only_full_backup_runs()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync(retentionMinutes: null);
        policy.FullRetentionMinutes = 1;
        policy.IncrementalRetentionMinutes = 1;
        policy.MinBackupsToKeep = 0;
        policy.MinFullBackupsToKeep = 1;
        await fixture.Db.SaveChangesAsync();

        var protectedFull = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-3), backupType: BackupType.Full, tableName: "orders");
        var fallbackIncremental = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, now.AddHours(-2), backupType: BackupType.Incremental, tableName: "new_orders");
        foreach (var table in fallbackIncremental.Tables)
        {
            table.EffectiveBackupType = BackupType.Full;
            foreach (var shard in table.Shards)
            {
                shard.EffectiveBackupType = BackupType.Full;
            }
        }
        await fixture.Db.SaveChangesAsync();

        await fixture.Services.GetRequiredService<RetentionManagementBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == protectedFull.Id)).Status);
        Assert.Equal(BackupRunStatus.BackupExpiredDeleted, (await fixture.Db.Backups.SingleAsync(x => x.Id == fallbackIncremental.Id)).Status);
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
    public async Task Garbage_collector_cleans_failed_backups_only_when_policy_opts_in_and_keeps_partial_backups()
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
        Assert.Equal(BackupRunStatus.PartiallySucceeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == partial.Id)).Status);
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
        Assert.Contains(backup.Tables.Single().StoragePath, fixture.StorageDeletion.DeletedDirectories);
        var manifestPath = BackupStorageManifestService.ManifestPath(backup);
        Assert.Contains(manifestPath, fixture.StorageDeletion.DeletedObjects);
        Assert.Equal($"obj:{manifestPath}", fixture.StorageDeletion.StorageOperations.Last());
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "backup-cleanup-succeeded" && x.EntityId == backup.Id.ToString()));
    }

    [Fact]
    public async Task Garbage_collector_queue_lists_pending_and_policy_eligible_items()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var cleanupPolicy = await fixture.SeedPolicyAsync(failedMode: FailedBackupRetentionMode.DeleteByGarbageCollectorAfterFailure);
        var keepPolicy = await fixture.SeedPolicyAsync(name: "keep-failures");
        var manual = await fixture.SeedPolicyBackupAsync(cleanupPolicy.Id, BackupRunStatus.ManualDeleteRequested, now.AddMinutes(-30));
        manual.DeletionReason = "manual";
        manual.DeletionRequestedAt = now.AddMinutes(-20);
        var failed = await fixture.SeedPolicyBackupAsync(cleanupPolicy.Id, BackupRunStatus.Failed, now.AddMinutes(-10));
        var partial = await fixture.SeedPolicyBackupAsync(cleanupPolicy.Id, BackupRunStatus.PartiallySucceeded, now.AddMinutes(-7));
        var kept = await fixture.SeedPolicyBackupAsync(keepPolicy.Id, BackupRunStatus.Failed, now.AddMinutes(-5));
        await fixture.Db.SaveChangesAsync();

        var queue = await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().GetQueueAsync();

        Assert.Contains(queue, x => x.EntityId == manual.Id && x.Status == BackupRunStatus.ManualDeleteRequested && x.FinalStatus == BackupRunStatus.ManualDeleted && x.Reason == "manual");
        Assert.Contains(queue, x => x.EntityId == failed.Id && x.Status == BackupRunStatus.Failed && x.FinalStatus == BackupRunStatus.FailedBackupDeletedByGarbageCollector && x.Reason == "failed-backup-garbage-collector");
        Assert.DoesNotContain(queue, x => x.EntityId == partial.Id);
        Assert.DoesNotContain(queue, x => x.EntityId == kept.Id);
    }

    [Fact]
    public async Task Garbage_collector_run_one_cleans_only_requested_queue_item()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync();
        var first = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.ManualDeleteRequested, now.AddMinutes(-30), tableName: "orders");
        var second = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.ManualDeleteRequested, now.AddMinutes(-25), tableName: "invoices");
        first.DeletionReason = "manual";
        first.DeletionRequestedAt = now.AddMinutes(-20);
        second.DeletionReason = "manual";
        second.DeletionRequestedAt = now.AddMinutes(-15);
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().RunOneAsync(first.Id, "manual");

        fixture.Db.ChangeTracker.Clear();
        var cleaned = await fixture.Db.Backups.SingleAsync(x => x.Id == first.Id);
        var untouched = await fixture.Db.Backups.SingleAsync(x => x.Id == second.Id);
        Assert.Equal(BackupRunStatus.ManualDeleted, cleaned.Status);
        Assert.NotNull(cleaned.DeletedAt);
        Assert.Equal(BackupRunStatus.ManualDeleteRequested, untouched.Status);
        Assert.Null(untouched.DeletedAt);
        Assert.Equal(1, result.LastPendingCleanupCount);
        Assert.Equal(1, result.LastCleanedCount);
        Assert.Contains(first.Tables.Single().StoragePath, fixture.StorageDeletion.DeletedDirectories);
        Assert.DoesNotContain(second.Tables.Single().StoragePath, fixture.StorageDeletion.DeletedDirectories);
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
    public async Task Garbage_collector_keeps_incremental_that_depends_on_successful_shard_from_partial_parent()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync();
        var parent = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.PartiallySucceeded, now.AddHours(-2), shardCount: 2, tableName: "orders");
        var parentTable = parent.Tables.Single();
        parentTable.Status = BackupTableStatus.PartiallySucceeded;
        var parentShards = parentTable.Shards.OrderBy(x => x.SourceShardNumber).ToList();
        parentShards[0].Status = BackupTableStatus.Succeeded;
        parentShards[1].Status = BackupTableStatus.Failed;

        var incremental = await fixture.SeedDependentIncrementalAsync(policy.Id, parent, now.AddHours(-1));
        var incrementalTable = incremental.Tables.Single();
        incrementalTable.ParentFullBackupId = null;
        incrementalTable.ParentFullBackupTableId = null;
        var incrementalShards = incrementalTable.Shards.OrderBy(x => x.SourceShardNumber).ToList();
        incrementalShards[0].ParentFullBackupId = parent.Id;
        incrementalShards[0].ParentFullBackupTableShardId = parentShards[0].Id;
        incrementalShards[1].EffectiveBackupType = BackupType.Full;
        incrementalShards[1].ParentFullBackupId = null;
        incrementalShards[1].ParentFullBackupTableShardId = null;
        await fixture.Db.SaveChangesAsync();

        var queue = await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().GetQueueAsync();
        await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        Assert.DoesNotContain(queue, x => x.EntityId == incremental.Id && x.Reason == "orphaned-incremental-parent-missing");
        Assert.Equal(BackupRunStatus.PartiallySucceeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == parent.Id)).Status);
        Assert.Equal(BackupRunStatus.Succeeded, (await fixture.Db.Backups.SingleAsync(x => x.Id == incremental.Id)).Status);
        Assert.False(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "orphaned-incremental-garbage-collection-requested" && x.EntityId == incremental.Id.ToString()));
    }

    [Fact]
    public async Task Garbage_collector_keeps_incremental_that_depends_on_effective_full_table_from_incremental_parent()
    {
        var now = new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await TestFixture.CreateAsync(timeProvider: new FixedTimeProvider(now));
        var policy = await fixture.SeedPolicyAsync();
        var parent = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.PartiallySucceeded, now.AddHours(-2), backupType: BackupType.Incremental, shardCount: 2, tableName: "orders");
        var parentTable = parent.Tables.Single();
        parentTable.EffectiveBackupType = BackupType.Full;
        parentTable.Status = BackupTableStatus.Succeeded;
        foreach (var parentShard in parentTable.Shards)
        {
            parentShard.EffectiveBackupType = BackupType.Full;
            parentShard.Status = BackupTableStatus.Succeeded;
        }

        var incremental = await fixture.SeedDependentIncrementalAsync(policy.Id, parent, now.AddHours(-1));
        await fixture.Db.SaveChangesAsync();

        var queue = await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().GetQueueAsync();
        await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        Assert.DoesNotContain(queue, x => x.EntityId == incremental.Id && x.Reason == "orphaned-incremental-parent-missing");
        var persistedParent = await fixture.Db.Backups.SingleAsync(x => x.Id == parent.Id);
        var persistedIncremental = await fixture.Db.Backups.SingleAsync(x => x.Id == incremental.Id);
        Assert.Equal(BackupType.Incremental, persistedParent.BackupType);
        Assert.Equal(BackupRunStatus.PartiallySucceeded, persistedParent.Status);
        Assert.Equal(BackupRunStatus.Succeeded, persistedIncremental.Status);
        Assert.False(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "orphaned-incremental-garbage-collection-requested" && x.EntityId == incremental.Id.ToString()));
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
        await using var fixture = await TestFixture.CreateAsync(options: FastCancelOptions());
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Running, "backup-op-1", true)
        ]);
        backup.Status = BackupRunStatus.Running;
        await fixture.Db.SaveChangesAsync();

        var canceled = await fixture.Services.GetRequiredService<BackupApplicationService>().CancelAsync(backup.Id);

        Assert.NotNull(canceled);
        Assert.Equal(BackupRunStatus.Canceled, canceled.Status);
        Assert.Equal(2, fixture.ClickHouse.KilledQueries.Count(x => x == "backup-op-1"));
        Assert.All(fixture.ClickHouse.KilledQueryEndpoints, endpoint => Assert.Equal(new ClickHouseNodeEndpoint("source", 9000, false), endpoint));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "canceled" && x.EntityType == "backup" && x.EntityId == backup.Id.ToString()));

        await fixture.Services.GetRequiredService<BackupsGarbageCollectorBackgroundService>().RunOnceAsync();

        fixture.Db.ChangeTracker.Clear();
        var afterCleanup = await fixture.Db.Backups.SingleAsync(x => x.Id == backup.Id);
        Assert.Equal(BackupRunStatus.Canceled, afterCleanup.Status);
        Assert.NotNull(afterCleanup.DeletedAt);
        Assert.Contains(backup.Tables.Single().StoragePath, fixture.StorageDeletion.DeletedDirectories);
    }

    [Fact]
    public async Task Backup_cancel_stops_inflight_worker_without_marking_later_shards_successful()
    {
        await using var fixture = await TestFixture.CreateAsync(options: FastCancelOptions(maxDop: 1, pollInterval: TimeSpan.FromMilliseconds(1)));
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree"));
        fixture.ClickHouse.Topology.Clear();
        fixture.ClickHouse.Topology.AddRange([
            new ClickHouseShardReplicaInfo(1, "shard-1", 1, "source-s1", 9000, false, 0),
            new ClickHouseShardReplicaInfo(2, "shard-2", 1, "source-s2", 9000, false, 0)
        ]);
        fixture.ClickHouse.BlockOperationStatus = true;

        var backupId = await fixture.CreateManualBackupAsync();
        var runTask = fixture.RunBackupAsync(backupId);
        await fixture.ClickHouse.WaitForBlockedStatusAsync();

        fixture.Db.ChangeTracker.Clear();
        var runningBackup = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backupId);
        var startedShard = runningBackup.Tables.SelectMany(x => x.Shards).Single(x => x.Status == BackupTableStatus.Running);
        Assert.False(string.IsNullOrWhiteSpace(startedShard.ClickHouseOperationId));

        var canceled = await fixture.Services.GetRequiredService<BackupApplicationService>().CancelAsync(backupId);
        Assert.NotNull(canceled);
        Assert.Equal(BackupRunStatus.Canceled, canceled.Status);
        Assert.Equal(2, fixture.ClickHouse.KilledQueries.Count(x => x == startedShard.ClickHouseOperationId!));
        Assert.All(fixture.ClickHouse.KilledQueryEndpoints, endpoint => Assert.Equal(new ClickHouseNodeEndpoint(startedShard.Host, startedShard.Port, startedShard.UseTls), endpoint));

        fixture.ClickHouse.ReleaseBlockedStatus();
        await runTask;

        fixture.Db.ChangeTracker.Clear();
        var after = await fixture.Db.Backups.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == backupId);
        Assert.Equal(BackupRunStatus.Canceled, after.Status);
        Assert.All(after.Tables.SelectMany(x => x.Shards), shard => Assert.Equal(BackupTableStatus.Skipped, shard.Status));
        Assert.Single(fixture.ClickHouse.BackupStartTables);
        Assert.False(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-succeeded" && x.EntityType == "backup-table-shard"));
    }

    [Fact]
    public async Task Backup_cancel_kills_table_operation_when_shards_have_no_operation_ids()
    {
        await using var fixture = await TestFixture.CreateAsync(options: FastCancelOptions());
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Running, "backup-table-op", true)
        ]);
        backup.Status = BackupRunStatus.Running;
        var table = backup.Tables.Single();
        table.ClickHouseOperationId = "backup-table-op";
        foreach (var shard in table.Shards)
        {
            shard.ClickHouseOperationId = null;
        }
        await fixture.Db.SaveChangesAsync();

        var canceled = await fixture.Services.GetRequiredService<BackupApplicationService>().CancelAsync(backup.Id);

        Assert.NotNull(canceled);
        Assert.Equal(BackupRunStatus.Canceled, canceled.Status);
        Assert.Equal(2, fixture.ClickHouse.KilledQueries.Count(x => x == "backup-table-op"));
        Assert.All(fixture.ClickHouse.KilledQueryEndpoints, endpoint => Assert.Equal(new ClickHouseNodeEndpoint("source", 9000, false), endpoint));
    }

    [Fact]
    public async Task Restore_cancel_marks_run_canceled_and_kills_queries()
    {
        await using var fixture = await TestFixture.CreateAsync(options: FastCancelOptions());
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
        Assert.Equal(2, fixture.ClickHouse.KilledQueries.Count(x => x == "restore-op-1"));
        Assert.All(fixture.ClickHouse.KilledQueryEndpoints, endpoint => Assert.Equal(new ClickHouseNodeEndpoint("restore", 9000, false), endpoint));
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "canceled" && x.EntityType == "restore" && x.EntityId == restore.Id.ToString()));
    }

    [Fact]
    public async Task Restore_cancel_stops_inflight_worker_without_marking_shard_successful()
    {
        await using var fixture = await TestFixture.CreateAsync(options: FastCancelOptions(maxDop: 1, pollInterval: TimeSpan.FromMilliseconds(1)));
        fixture.ClickHouse.BlockOperationStatus = true;
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "restore_cancel_inflight", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        var restore = await fixture.SeedRestoreAsync(backup, [
            new SeedRestoreTable("sales", "restore_cancel_inflight", RestoreTableStatus.Queued, null)
        ]);

        var runTask = fixture.RunRestoreAsync(restore.Id);
        await fixture.ClickHouse.WaitForBlockedStatusAsync();

        fixture.Db.ChangeTracker.Clear();
        var runningRestore = await fixture.Db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == restore.Id);
        var startedShard = runningRestore.Tables.SelectMany(x => x.Shards).Single(x => x.Status == RestoreTableStatus.Running);
        Assert.False(string.IsNullOrWhiteSpace(startedShard.ClickHouseOperationId));

        var canceled = await fixture.Services.GetRequiredService<RestoreApplicationService>().CancelAsync(restore.Id);
        Assert.NotNull(canceled);
        Assert.Equal(RestoreRunStatus.Canceled, canceled.Status);
        Assert.Equal(2, fixture.ClickHouse.KilledQueries.Count(x => x == startedShard.ClickHouseOperationId!));
        Assert.All(fixture.ClickHouse.KilledQueryEndpoints, endpoint => Assert.Equal(new ClickHouseNodeEndpoint(startedShard.TargetHost, startedShard.TargetPort, startedShard.TargetUseTls), endpoint));

        fixture.ClickHouse.ReleaseBlockedStatus();
        await runTask;

        fixture.Db.ChangeTracker.Clear();
        var after = await fixture.Db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == restore.Id);
        Assert.Equal(RestoreRunStatus.Canceled, after.Status);
        Assert.All(after.Tables.SelectMany(x => x.Shards), shard => Assert.Equal(RestoreTableStatus.Skipped, shard.Status));
        Assert.Empty(await fixture.Db.BackupRestoreQueueItems.Where(x => x.Kind == BackupRestoreQueueKind.Restore && x.OperationId == restore.Id && x.CompletedAt == null).ToListAsync());
        Assert.False(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-succeeded" && x.EntityType == "restore-table-shard"));
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
    public async Task Restore_from_incremental_fails_clearly_when_parent_full_storage_is_missing()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var policy = await fixture.SeedPolicyAsync();
        var full = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, DateTimeOffset.UtcNow.AddMinutes(-20), tableName: "orders");
        var incremental = await fixture.SeedDependentIncrementalAsync(policy.Id, full, DateTimeOffset.UtcNow.AddMinutes(-10));
        var parentShardId = incremental.Tables.Single().Shards.Single().ParentFullBackupTableShardId!.Value;
        fixture.ClickHouse.MissingParentFullShardIds.Add(parentShardId);

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(
            new InitiateRestoreRequest(incremental.Id, fixture.TargetClusterId, null, null, null, null, false, false));

        await fixture.RunRestoreAsync(restore.Id);

        fixture.Db.ChangeTracker.Clear();
        var failed = await fixture.Db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == restore.Id);
        var failedTable = Assert.Single(failed.Tables);
        var failedShard = Assert.Single(failedTable.Shards);
        Assert.Equal(RestoreRunStatus.Failed, failed.Status);
        Assert.Equal(RestoreTableStatus.Failed, failedTable.Status);
        Assert.Equal(RestoreTableStatus.Failed, failedShard.Status);
        Assert.Contains("Parent full backup storage is missing", failed.FailureReason);
        Assert.Contains("Parent full backup storage is missing", failedShard.Error);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "failed" && x.EntityType == "restore" && x.EntityId == restore.Id.ToString()));
    }

    [Fact]
    public async Task Restore_statement_includes_cluster_and_policy_advanced_settings()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var targetCluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.TargetClusterId);
        targetCluster.ClickHouseRestoreSettingsJson = ClickHouseAdvancedSettings.Serialize(Settings(("max_restore_bandwidth", 100), ("restore_threads", 2)), ClickHouseAdvancedSettingsKind.Restore);
        var policy = await fixture.SeedPolicyAsync();
        policy.ClickHouseRestoreSettingsJson = ClickHouseAdvancedSettings.Serialize(Settings(("restore_threads", 7), ("s3_storage_class", "STANDARD_IA")), ClickHouseAdvancedSettingsKind.Restore);
        var backup = await fixture.SeedPolicyBackupAsync(policy.Id, BackupRunStatus.Succeeded, DateTimeOffset.UtcNow.AddMinutes(-1), tableName: "orders");
        await fixture.Db.SaveChangesAsync();

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(
            new InitiateRestoreRequest(backup.Id, fixture.TargetClusterId, null, null, null, null, false, false));
        await fixture.RunRestoreAsync(restore.Id);

        var sql = Assert.Single(fixture.ClickHouse.RestoreStartSql);
        Assert.Contains("RESTORE TABLE `sales`.`orders`", sql, StringComparison.Ordinal);
        Assert.Contains("SETTINGS max_restore_bandwidth = 100", sql, StringComparison.Ordinal);
        Assert.Contains("restore_threads = 7", sql, StringComparison.Ordinal);
        Assert.Contains("s3_storage_class = 'STANDARD_IA'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("restore_threads = 2", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_statement_uses_request_advanced_settings()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var targetCluster = await fixture.Db.ClickHouseClusters.SingleAsync(x => x.Id == fixture.TargetClusterId);
        targetCluster.ClickHouseRestoreSettingsJson = ClickHouseAdvancedSettings.Serialize(Settings(("max_restore_bandwidth", 100)), ClickHouseAdvancedSettingsKind.Restore);
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(
            new InitiateRestoreRequest(
                backup.Id,
                fixture.TargetClusterId,
                null,
                null,
                null,
                null,
                false,
                false,
                ClickHouseRestoreSettings: Settings(("max_restore_bandwidth", 333), ("restore_threads", 4))));
        await fixture.RunRestoreAsync(restore.Id);

        var sql = Assert.Single(fixture.ClickHouse.RestoreStartSql);
        Assert.Contains("RESTORE TABLE `sales`.`orders`", sql, StringComparison.Ordinal);
        Assert.Contains("max_restore_bandwidth = 333", sql, StringComparison.Ordinal);
        Assert.Contains("restore_threads = 4", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("max_restore_bandwidth = 100", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_shard_retries_transient_failure_and_succeeds()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions
        {
            MaxDop = 1,
            PollInterval = TimeSpan.FromMilliseconds(1),
            TransientShardRetryDelay = TimeSpan.FromMilliseconds(1),
            TransientShardMaxRetries = 3
        });
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(
            new InitiateRestoreRequest(backup.Id, fixture.TargetClusterId, null, null, null, null, false, false));
        fixture.ClickHouse.FailNextRestoreStartTransientCount = 1;

        await fixture.RunRestoreAsync(restore.Id);

        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == restore.Id);
        var shard = Assert.Single(Assert.Single(completed.Tables).Shards);
        Assert.Equal(RestoreRunStatus.Succeeded, completed.Status);
        Assert.Equal(RestoreTableStatus.Succeeded, shard.Status);
        Assert.Null(shard.Error);
        Assert.Equal(2, fixture.ClickHouse.RestoreStartTables.Count);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-retry-scheduled" && x.EntityId == shard.Id.ToString()));
    }

    [Fact]
    public async Task Restore_shard_retry_resumes_submitted_operation_after_transient_poll_failure()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions
        {
            MaxDop = 1,
            PollInterval = TimeSpan.FromMilliseconds(1),
            TransientShardRetryDelay = TimeSpan.FromMilliseconds(1),
            TransientShardMaxRetries = 3
        });
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(
            new InitiateRestoreRequest(backup.Id, fixture.TargetClusterId, null, null, null, null, false, false));
        fixture.ClickHouse.FailNextOperationStatusTransientCount = 1;

        await fixture.RunRestoreAsync(restore.Id);

        fixture.Db.ChangeTracker.Clear();
        var completed = await fixture.Db.Restores.Include(x => x.Tables).ThenInclude(x => x.Shards).SingleAsync(x => x.Id == restore.Id);
        var shard = Assert.Single(Assert.Single(completed.Tables).Shards);
        Assert.Equal(RestoreRunStatus.Succeeded, completed.Status);
        Assert.Equal(RestoreTableStatus.Succeeded, shard.Status);
        Assert.NotNull(shard.ClickHouseOperationId);
        Assert.Single(fixture.ClickHouse.RestoreStartTables);
        Assert.True(await fixture.Db.AuditEntries.AnyAsync(x => x.Action == "shard-retry-scheduled" && x.EntityId == shard.Id.ToString()));
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
    public async Task Restore_table_mapping_create_table_override_is_stored_and_audited_without_full_sql()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        var table = backup.Tables.Single();
        var overrideSql = "CREATE TABLE sales.orders (id UInt64, updated_at DateTime) ENGINE = MergeTree ORDER BY (id, updated_at)";

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false,
            Tables: [new RestoreTableMappingRequest(table.Id, "restored", "orders_copy", CreateTableSqlOverride: overrideSql)]));

        Assert.Contains(overrideSql, restore.RequestJson);
        var audit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "created" && x.EntityType == "restore" && x.EntityId == restore.Id.ToString());
        Assert.Contains("createTableSqlOverride", audit.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sha256", audit.Details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(overrideSql, audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_rejects_multi_statement_create_table_override()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        var table = backup.Tables.Single();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false,
            Tables: [new RestoreTableMappingRequest(table.Id, "restored", "orders_copy", CreateTableSqlOverride: "CREATE TABLE sales.orders (id UInt64) ENGINE = MergeTree ORDER BY id; DROP TABLE sales.orders")])));

        Assert.Contains("single CREATE TABLE", error.Message);
    }

    [Fact]
    public async Task Restore_uses_create_table_override_when_creating_missing_target_table()
    {
        await using var fixture = await TestFixture.CreateAsync(options: new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1) });
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        var table = backup.Tables.Single();
        var overrideSql = "CREATE TABLE sales.orders (id UInt64, updated_at DateTime) ENGINE = MergeTree ORDER BY (id, updated_at)";

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            false,
            false,
            Tables: [new RestoreTableMappingRequest(table.Id, "restored", "orders_copy", CreateTableSqlOverride: overrideSql)]));

        await fixture.RunRestoreAsync(restore.Id);

        Assert.Contains(fixture.ClickHouse.ExecuteSql, sql => sql == "CREATE TABLE IF NOT EXISTS `restored`.`orders_copy` (id UInt64, updated_at DateTime) ENGINE = MergeTree ORDER BY (id, updated_at)");
        Assert.DoesNotContain(fixture.ClickHouse.ExecuteSql, sql => sql.Contains("CREATE TABLE IF NOT EXISTS `restored`.`orders_copy` (id UInt64) ENGINE = MergeTree ORDER BY id", StringComparison.Ordinal));
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
    public async Task Direct_restore_passes_allow_non_empty_tables_for_precreated_target_table()
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
            "orders_copy",
            false,
            false));

        await fixture.RunRestoreAsync(restore.Id);

        Assert.Equal(["orders_copy"], fixture.ClickHouse.RestoreTargetTables);
        Assert.Equal([true], fixture.ClickHouse.RestoreAllowNonEmptyTables);
    }

    [Fact]
    public async Task Append_restore_with_matching_schema_restores_directly_to_target_table()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        var schemaHash = backup.Tables.Single().SchemaDefinition!.SchemaHash;
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree", schemaHash: schemaHash));

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            true,
            false,
            ConfirmDestructive: true));

        var table = Assert.Single(restore.Tables);
        var shard = Assert.Single(table.Shards);
        Assert.Equal("orders", shard.RestoreTableName);
        Assert.False(shard.RestoreTableName.StartsWith("__chobo_restore_", StringComparison.Ordinal));

        await fixture.RunRestoreAsync(restore.Id);

        Assert.Equal(["orders"], fixture.ClickHouse.RestoreTargetTables);
        Assert.Equal([true], fixture.ClickHouse.RestoreAllowNonEmptyTables);
        Assert.DoesNotContain(fixture.ClickHouse.ExecuteSql, sql => sql.Contains("__chobo_restore_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Append_restore_with_schema_mismatch_allowed_still_uses_temporary_table()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var backup = await fixture.SeedBackupWithTablesAsync([
            new SeedBackupTable("sales", "orders", BackupTableStatus.Succeeded, "backup-op", true)
        ]);
        backup.Status = BackupRunStatus.Succeeded;
        await fixture.Db.SaveChangesAsync();
        fixture.ClickHouse.Inventory.Add(Table("sales", "orders", "MergeTree", schemaHash: "different-schema"));

        var restore = await fixture.Services.GetRequiredService<RestoreApplicationService>().InitiateAsync(new InitiateRestoreRequest(
            backup.Id,
            fixture.TargetClusterId,
            null,
            null,
            null,
            null,
            true,
            true,
            ConfirmDestructive: true));

        var table = Assert.Single(restore.Tables);
        var shard = Assert.Single(table.Shards);
        Assert.StartsWith("__chobo_restore_", shard.RestoreTableName, StringComparison.Ordinal);
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            if (await condition())
            {
                return;
            }
            try
            {
                await Task.Delay(25, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    private static async Task<TimeSpan> MeasureAsync(Func<Task> action)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }
    private static void MarkBackupAsReplicated(BackupEntity backup)
    {
        foreach (var table in backup.Tables)
        {
            table.Engine = "ReplicatedMergeTree";
            if (table.SchemaDefinition is not null)
            {
                table.SchemaDefinition.Engine = "ReplicatedMergeTree";
            }
        }
    }

    private static BackupTargetEntity CreateS3TargetEntity(Guid id, string name, string endpoint, string region, string bucket, string? pathPrefix, bool forcePathStyle, bool includeCredentials) =>
        new()
        {
            Id = id,
            Name = name,
            Type = StorageProviderTypes.S3,
            SettingsJson = JsonSerializer.Serialize(new S3TargetSettingsDto(endpoint, region, bucket, pathPrefix, forcePathStyle), JsonOptions),
            SecretsJson = includeCredentials
                ? JsonSerializer.Serialize(new
                {
                    accessKey = new { ciphertext = "encrypted-access", keyId = Guid.NewGuid() },
                    secretKey = new { ciphertext = "encrypted-secret", keyId = Guid.NewGuid() }
                }, JsonOptions)
                : "{}"
        };
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
        private int _activeRestoreStarts;
        public List<ClickHouseTableInfo> Inventory { get; } = [];
        public List<ClickHouseShardReplicaInfo> Topology { get; } = [new(1, "single", 1, "source", 9000, false, 0)];
        public Dictionary<string, string> KnownOperations { get; } = new(StringComparer.Ordinal);
        public List<(string OperationId, string StoragePath)> BackupOperations { get; } = [];
        public List<string> BackupStartTables { get; } = [];
        public List<int> BackupStartShardNumbers { get; } = [];
        public List<ClickHouseNodeEndpoint> BackupStartEndpoints { get; } = [];
        public List<string> BackupStartSql { get; } = [];
        public List<string?> BackupBasePaths { get; } = [];
        public List<IReadOnlyDictionary<string, JsonElement>> BackupSettings { get; } = [];
        public List<string> RestoreStartTables { get; } = [];
        public List<int> RestoreStartShardNumbers { get; } = [];
        public List<ClickHouseNodeEndpoint> RestoreStartEndpoints { get; } = [];
        public List<string> RestoreStartSql { get; } = [];
        public List<string> RestoreTargetTables { get; } = [];
        public List<bool> RestoreAllowNonEmptyTables { get; } = [];
        public List<IReadOnlyDictionary<string, JsonElement>> RestoreSettings { get; } = [];
        public List<string> ExecuteSql { get; } = [];
        public List<ClickHouseNodeEndpoint> GetTablesEndpoints { get; } = [];
        public int GetTablesCallCount { get; private set; }
        public Dictionary<string, List<ClickHouseTableInfo>> InventoryByEndpoint { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> KilledQueries { get; } = [];
        public List<ClickHouseNodeEndpoint> KilledQueryEndpoints { get; } = [];
        public List<ClickHouseNodeEndpoint> ExecuteEndpoints { get; } = [];
        public List<(ClickHouseNodeEndpoint Endpoint, string Sql)> EndpointExecuteSql { get; } = [];
        public HashSet<string> UnavailableVersionEndpoints { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int MaxConcurrentBackupStarts { get; private set; }
        public int MaxConcurrentRestoreStarts { get; private set; }
        public TimeSpan StartDelay { get; set; }
        public int RequiredConcurrentBackupStartsBeforeRelease { get; set; }
        public int RequiredConcurrentRestoreStartsBeforeRelease { get; set; }
        public bool BlockOperationStatus { get; set; }
        public bool BlockTopology { get; set; }
        public Exception? GetTablesException { get; set; }
        public Exception? ExecuteException { get; set; }
        public string NextBackupStatus { get; set; } = "BACKUP_CREATED";
        public string? NextBackupError { get; set; }
        public int FailNextBackupStartTransientCount { get; set; }
        public bool CreateOperationBeforeFailingBackupStart { get; set; }
        public string NextRestoreStatus { get; set; } = "RESTORED";
        public string? NextRestoreError { get; set; }
        public int FailNextRestoreStartTransientCount { get; set; }
        public int FailNextOperationStatusTransientCount { get; set; }
        public bool CompleteCreatingBackupOperationsAfterStatusRead { get; set; }
        public bool DropStartedBackupOperation { get; set; }
        public bool DropStartedRestoreOperation { get; set; }
        public HashSet<Guid> MissingParentFullShardIds { get; } = [];
        public Dictionary<string, string> OperationErrors { get; } = new(StringComparer.Ordinal);
        private TaskCompletionSource _statusBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _releaseStatus = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _topologyBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _releaseTopology = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _backupStartGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _restoreStartGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            ExecuteSql.Add(sql);
            ExecuteEndpoints.Add(endpoint);
            EndpointExecuteSql.Add((endpoint, sql));
            var isVersionProbe = string.Equals(sql, "SELECT version()", StringComparison.OrdinalIgnoreCase);
            if (isVersionProbe && UnavailableVersionEndpoints.Contains(EndpointKey(endpoint)))
            {
                throw new TimeoutException($"simulated unavailable ClickHouse endpoint {endpoint.Host}:{endpoint.Port}");
            }
            if (!isVersionProbe && ExecuteException is not null)
            {
                throw ExecuteException;
            }

            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<ClickHouseShardReplicaInfo>> GetTopologyAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken)
        {
            if (BlockTopology)
            {
                _topologyBlocked.TrySetResult();
                await _releaseTopology.Task.WaitAsync(cancellationToken);
            }

            return Topology.ToList();
        }

        public async Task<ClickHouseOperationResult> StartBackupAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, string? baseBackupPath, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken)
        {
            var shard = new BackupTableShardEntity { SourceShardNumber = 1, StoragePath = table.StoragePath };
            return await StartBackupShardAsync(new ClickHouseNodeEndpoint("source", 9000, false), cluster, target, table, shard, baseBackupPath, settings, cancellationToken);
        }

        public async Task<ClickHouseOperationResult> StartBackupShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, BackupTableShardEntity shard, string? baseBackupPath, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _activeBackupStarts++;
                MaxConcurrentBackupStarts = Math.Max(MaxConcurrentBackupStarts, _activeBackupStarts);
                if (RequiredConcurrentBackupStartsBeforeRelease > 0 && MaxConcurrentBackupStarts >= RequiredConcurrentBackupStartsBeforeRelease)
                {
                    _backupStartGate.TrySetResult();
                }
                BackupStartTables.Add(table.Table);
                BackupStartShardNumbers.Add(shard.SourceShardNumber);
                BackupStartEndpoints.Add(endpoint);
                BackupStartSql.Add($"BACKUP TABLE {ClickHouseSql.Qualified(table.Database, table.Table)} TO <target>{ClickHouseAdvancedSettings.ToSettingsClause(settings)} ASYNC");
                BackupBasePaths.Add(baseBackupPath);
                BackupSettings.Add(settings);
            }

            if (RequiredConcurrentBackupStartsBeforeRelease > 0)
            {
                await _backupStartGate.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            else if (StartDelay > TimeSpan.Zero)
            {
                await Task.Delay(StartDelay, cancellationToken);
            }

            lock (_lock)
            {
                _activeBackupStarts--;
            }

            if (FailNextBackupStartTransientCount > 0)
            {
                FailNextBackupStartTransientCount--;
                if (CreateOperationBeforeFailingBackupStart)
                {
                    lock (_lock)
                    {
                        _ = CreateKnownBackupOperationNoLock(table, shard);
                    }
                }
                throw new TimeoutException("simulated transient backup timeout");
            }

            string operationId;
            lock (_lock)
            {
                operationId = CreateKnownBackupOperationNoLock(table, shard);
            }
            return new ClickHouseOperationResult(operationId, "CREATING_BACKUP");
        }

        private string CreateKnownBackupOperationNoLock(BackupTableEntity table, BackupTableShardEntity shard)
        {
            var operationId = $"backup-{table.Table}-s{shard.SourceShardNumber}-{Guid.NewGuid():N}";
            if (!DropStartedBackupOperation)
            {
                KnownOperations[operationId] = NextBackupStatus;
                BackupOperations.Add((operationId, shard.StoragePath));
                if (!string.IsNullOrWhiteSpace(NextBackupError))
                {
                    OperationErrors[operationId] = NextBackupError;
                }
            }
            return operationId;
        }
        public Task<ClickHouseOperationResult> StartRestoreAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, RestoreTableEntity table, BackupTableEntity backupTable, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken)
        {
            var shard = new RestoreTableShardEntity { SourceShardNumber = 1, RestoreDatabase = table.TargetDatabase, RestoreTableName = table.TargetTable };
            var backupShard = new BackupTableShardEntity { SourceShardNumber = 1, StoragePath = backupTable.StoragePath };
            return StartRestoreShardAsync(new ClickHouseNodeEndpoint("restore", 9000, false), cluster, target, shard, backupTable, backupShard, table.Append, settings, cancellationToken);
        }

        public async Task<ClickHouseOperationResult> StartRestoreShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, RestoreTableShardEntity table, BackupTableEntity backupTable, BackupTableShardEntity backupShard, bool allowNonEmptyTables, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _activeRestoreStarts++;
                MaxConcurrentRestoreStarts = Math.Max(MaxConcurrentRestoreStarts, _activeRestoreStarts);
                if (RequiredConcurrentRestoreStartsBeforeRelease > 0 && MaxConcurrentRestoreStarts >= RequiredConcurrentRestoreStartsBeforeRelease)
                {
                    _restoreStartGate.TrySetResult();
                }
                RestoreStartTables.Add(backupTable.Table);
                RestoreStartShardNumbers.Add(table.TargetShardNumber ?? table.SourceShardNumber);
                RestoreStartEndpoints.Add(endpoint);
                var choboSettings = allowNonEmptyTables ? new[] { ("allow_non_empty_tables", "1") } : [];
                RestoreStartSql.Add($"RESTORE TABLE {ClickHouseSql.Qualified(backupTable.Database, backupTable.Table)} AS {ClickHouseSql.Qualified(table.RestoreDatabase, table.RestoreTableName)} FROM <target>{ClickHouseAdvancedSettings.ToSettingsClause(settings, choboSettings)} ASYNC");
                RestoreTargetTables.Add(table.RestoreTableName);
                RestoreAllowNonEmptyTables.Add(allowNonEmptyTables);
                RestoreSettings.Add(settings);
            }

            if (RequiredConcurrentRestoreStartsBeforeRelease > 0)
            {
                await _restoreStartGate.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            else if (StartDelay > TimeSpan.Zero)
            {
                await Task.Delay(StartDelay, cancellationToken);
            }

            string operationId;
            lock (_lock)
            {
                _activeRestoreStarts--;
                if (FailNextRestoreStartTransientCount > 0)
                {
                    FailNextRestoreStartTransientCount--;
                    throw new TimeoutException("simulated transient restore timeout");
                }

                operationId = $"restore-{backupTable.Table}-s{backupShard.SourceShardNumber}-{Guid.NewGuid():N}";
                if (!DropStartedRestoreOperation)
                {
                    var missingParent = backupShard.EffectiveBackupType == BackupType.Incremental &&
                        backupShard.ParentFullBackupTableShardId is Guid parentShardId &&
                        MissingParentFullShardIds.Contains(parentShardId);
                    KnownOperations[operationId] = missingParent ? "RESTORE_FAILED" : NextRestoreStatus;
                    if (missingParent)
                    {
                        OperationErrors[operationId] = $"Parent full backup storage is missing for shard {backupShard.SourceShardNumber}.";
                    }
                    else if (!string.IsNullOrWhiteSpace(NextRestoreError))
                    {
                        OperationErrors[operationId] = NextRestoreError;
                    }
                }
            }
            return new ClickHouseOperationResult(operationId, "RESTORING");
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

            if (FailNextOperationStatusTransientCount > 0)
            {
                FailNextOperationStatusTransientCount--;
                throw new TimeoutException("simulated transient operation status timeout");
            }

            lock (_lock)
            {
                if (!KnownOperations.TryGetValue(operationId, out var status))
                {
                    return new ClickHouseOperationStatus(false, null, null);
                }

                if (CompleteCreatingBackupOperationsAfterStatusRead && string.Equals(status, "CREATING_BACKUP", StringComparison.OrdinalIgnoreCase))
                {
                    KnownOperations[operationId] = "BACKUP_CREATED";
                }

                return new ClickHouseOperationStatus(true, status, OperationErrors.GetValueOrDefault(operationId));
            }
        }

        public Task<ClickHouseDiscoveredOperation?> FindLatestBackupOperationForPathAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, string storagePath, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                var match = BackupOperations
                    .Where(operation => string.Equals(operation.StoragePath.Trim(), storagePath.Trim(), StringComparison.Ordinal) && KnownOperations.ContainsKey(operation.OperationId))
                    .OrderBy(operation => OperationPreference(KnownOperations[operation.OperationId]))
                    .ThenByDescending(operation => BackupOperations.IndexOf(operation))
                    .FirstOrDefault();
                if (match.OperationId is not null && KnownOperations.TryGetValue(match.OperationId, out var status))
                {
                    return Task.FromResult<ClickHouseDiscoveredOperation?>(new ClickHouseDiscoveredOperation(match.OperationId, status, OperationErrors.GetValueOrDefault(match.OperationId)));
                }
            }

            return Task.FromResult<ClickHouseDiscoveredOperation?>(null);
        }

        private static int OperationPreference(string status) =>
            string.Equals(status, "CREATING_BACKUP", StringComparison.OrdinalIgnoreCase) ? 0 :
            string.Equals(status, "BACKUP_CREATED", StringComparison.OrdinalIgnoreCase) ? 1 :
            status.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ? 3 :
            2;

        public Task KillBackupRestoreOperationAsync(ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken)
        {
            var node = cluster.AccessNodes.FirstOrDefault();
            var endpoint = node is null ? new ClickHouseNodeEndpoint("source", 9000, false) : new ClickHouseNodeEndpoint(node.Host, node.Port, node.UseTls);
            return KillBackupRestoreOperationAsync(endpoint, cluster, operationId, cancellationToken);
        }

        public Task KillBackupRestoreOperationAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                KilledQueries.Add(operationId);
                KilledQueryEndpoints.Add(endpoint);
                KnownOperations[operationId] = "CANCELLED";
            }
            return Task.CompletedTask;
        }

        public Task WaitForBlockedStatusAsync() => _statusBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void ReleaseBlockedStatus() => _releaseStatus.TrySetResult();

        public Task WaitForBlockedTopologyAsync() => _topologyBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void ReleaseBlockedTopology() => _releaseTopology.TrySetResult();
    }

    private sealed class FakeBackupStorageOperations : IBackupStorageOperations
    {
        public List<string> DeletedDirectories { get; } = [];
        public List<string> DeletedObjects { get; } = [];
        public List<string> StorageOperations { get; } = [];
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public bool FailNextDelete { get; set; }
        public int FailWriteCount { get; set; }
        public int WriteObjectCount { get; private set; }
        public bool DelayWritesUntilCanceled { get; set; }

        public Task DeleteDirectoryAsync(BackupTargetEntity target, string directoryPath, CancellationToken cancellationToken = default)
        {
            if (FailNextDelete)
            {
                FailNextDelete = false;
                throw new InvalidOperationException("simulated cleanup crash");
            }

            DeletedDirectories.Add(directoryPath);
            StorageOperations.Add($"dir:{directoryPath}");
            return Task.CompletedTask;
        }

        public async Task WriteObjectAsync(BackupTargetEntity target, string path, byte[] content, CancellationToken cancellationToken = default)
        {
            WriteObjectCount++;
            if (DelayWritesUntilCanceled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (FailWriteCount > 0)
            {
                FailWriteCount--;
                throw new InvalidOperationException("simulated manifest write crash");
            }

            Objects[path] = content;
        }

        public Task<byte[]> ReadObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Objects.TryGetValue(path, out var content) ? content : Array.Empty<byte>());

        public Task<IReadOnlyList<string>> ListObjectPathsAsync(BackupTargetEntity target, string rootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Objects.Keys.Where(x => x.StartsWith(rootPath, StringComparison.Ordinal)).ToList());

        public Task<IReadOnlyList<BackupStorageObjectInfo>> ListObjectsAsync(BackupTargetEntity target, string rootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackupStorageObjectInfo>>(Objects
                .Where(x => x.Key.StartsWith(rootPath, StringComparison.Ordinal))
                .Select(x => new BackupStorageObjectInfo(x.Key, x.Value.LongLength))
                .ToList());

        public Task DeleteObjectAsync(BackupTargetEntity target, string path, CancellationToken cancellationToken = default)
        {
            DeletedObjects.Add(path);
            StorageOperations.Add($"obj:{path}");
            Objects.Remove(path);
            return Task.CompletedTask;
        }

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

        private static class TestOptionsMonitor
        {
            public static IOptionsMonitor<T> Create<T>(T value) => new TestOptionsMonitorValue<T>(value);
        }

        private sealed class TestOptionsMonitorValue<T>(T value) : IOptionsMonitor<T>
        {
            public T CurrentValue => value;
            public T Get(string? name) => value;
            public IDisposable? OnChange(Action<T, string?> listener) => null;

            public static IOptionsMonitor<T> Create(T value) => new TestOptionsMonitorValue<T>(value);
        }
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
                .AddScoped<IBackupStorageProviderRegistry, BackupStorageProviderRegistry>()
                .AddScoped<PolicySelectorEvaluationService>()
                .AddMemoryCache()
                .AddScoped<ActorContext>()
                .AddScoped<IActorContext>(provider => provider.GetRequiredService<ActorContext>())
                .AddScoped<IAuditService, AuditService>()
                .AddSingleton(Options.Create(new ChoboTestHooksOptions()))
                .AddSingleton(Options.Create(new ChoboEndpointRewriteOptions()))
                .AddSingleton<IEndpointRewriteService, EndpointRewriteService>()
                .AddSingleton<ITestHookCoordinator, TestHookCoordinator>()
                .AddScoped<DashboardApplicationService>()
                .AddScoped<BackupApplicationService>()
                .AddScoped<BackupPreparationService>()
                .AddScoped<IClusterRepository, ClusterRepository>()
                .AddScoped<ITargetRepository, TargetRepository>()
                .AddScoped<IPolicyRepository, PolicyRepository>()
                .AddScoped<IScheduleRepository, ScheduleRepository>()
                .AddScoped<IUnitOfWork, EfUnitOfWork>()
                .AddScoped<PolicyApplicationService>()
                .AddScoped<TargetApplicationService>()
                .AddScoped<ScheduleApplicationService>()
                .AddScoped<SchemaBrowserApplicationService>()
                .AddScoped<SystemDefaultBackupPolicyService>()
                .AddScoped<IBackupStorageManifestService, BackupStorageManifestService>()
                .AddScoped<RestoreApplicationService>()
                .AddScoped<BackupRestoreQueueApplicationService>()
                .AddSingleton<IBackupRestoreConcurrencyCoordinator, BackupRestoreConcurrencyCoordinator>()
                .AddScoped<BackupRunnerService>()
                .AddScoped<RestoreRunnerService>()
                .AddScoped<BackupCleanupService>()
                .AddSingleton<IBackupRestoreQueues, BackupRestoreQueues>()
                .AddSingleton(TestOptionsMonitor.Create(options ?? new ChoboBackupRestoreOptions { MaxDop = 1, PollInterval = TimeSpan.FromMilliseconds(1), SchedulerInterval = TimeSpan.FromSeconds(1) }))
                .AddSingleton(TestOptionsMonitor.Create(new RetentionManagementOptions { Interval = TimeSpan.FromSeconds(1), MaxDop = 2 }))
                .AddSingleton(TestOptionsMonitor.Create(new BackupsGarbageCollectorOptions { Interval = TimeSpan.FromSeconds(1), MaxDop = 2 }))
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
                    BackupRestoreMaxDop = clusterMaxDop ?? options?.MaxDop ?? 3,
                    AccessNodes = [new ClickHouseAccessNodeEntity { Host = "source", Port = 9000 }]
                },
                new ClickHouseClusterEntity
                {
                    Id = targetClusterId,
                    Name = "restore",
                    Mode = ClusterMode.SingleInstance,
                    AccessNodes = [new ClickHouseAccessNodeEntity { Host = "restore", Port = 9000 }]
                });
            db.BackupTargets.Add(CreateS3TargetEntity(targetId, "minio", "http://minio:9000", "us-east-1", "data-bucket", null, true, false));
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
                    StoragePath = $"backups/{seed.Database}/{seed.Table}/manual/full/seed/{Guid.NewGuid():N}",
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
                            StoragePath = shardCount == 1 ? backupTable.StoragePath : $"{backupTable.StoragePath}/shards/shard-{shardNumber:0000}",
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
                        TargetShardNumber = backupShard.SourceShardNumber,
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

        public async Task<BackupScheduleEntity> SeedPolicyAndScheduleAsync(BackupContentMode contentMode = BackupContentMode.SchemaAndData)
        {
            var policy = NewPolicy("hourly");
            policy.ContentMode = contentMode;
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
                backup.Tables.Any(table => table.BackupSizeBytes.HasValue) ? backup.Tables.Sum(table => table.BackupSizeBytes ?? 0) : null,
                ClickHouseAdvancedSettings.Deserialize(backup.ClickHouseBackupSettingsJson, ClickHouseAdvancedSettingsKind.Backup),
                backup.Tables.Select(table => table.ParentFullBackupId)
                    .Concat(backup.Tables.SelectMany(table => table.Shards).Select(shard => shard.ParentFullBackupId))
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList(),
                [],
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
                    table.StoragePath,
                    table.BackupSizeBytes,
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
                        shard.StoragePath,
                        shard.BackupSizeBytes,
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
                ClickHouseAdvancedSettings.Deserialize(restore.ClickHouseRestoreSettingsJson, ClickHouseAdvancedSettingsKind.Restore),
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
