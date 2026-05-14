using Chobo.Contracts;
using ChoboServer.Application;
using ChoboServer.BackgroundServices;
using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace Chobo.Tests;

public sealed class BackupRestoreExecutionTests
{
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
            Assert.StartsWith("backups/analytics/orders/manual/full/", path);
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
        Assert.Contains("Destination ClickHouse instance is not reachable", completed.FailureReason);
        Assert.Contains("sales.orders", completed.FailureReason);
        var audit = await fixture.Db.AuditEntries.SingleAsync(x => x.Action == "failed" && x.EntityType == "restore" && x.EntityId == restore.Id.ToString());
        Assert.Contains("Destination ClickHouse instance is not reachable", audit.Details);
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

    private static ClickHouseTableInfo Table(string database, string table, string engine, string columnsJson = "[]", string? schemaHash = null) =>
        new(database, table, engine, $"CREATE TABLE {database}.{table} (id UInt64) ENGINE = {engine} ORDER BY id", columnsJson, schemaHash ?? $"{database}.{table}.{engine}.{columnsJson}");

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
        public List<string> RestoreStartTables { get; } = [];
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
            if (GetTablesException is not null)
            {
                throw GetTablesException;
            }

            return Task.FromResult<IReadOnlyList<ClickHouseTableInfo>>(Inventory.ToList());
        }

        public Task<ClickHouseTableInfo?> GetTableAsync(ClickHouseClusterEntity cluster, string database, string table, CancellationToken cancellationToken) =>
            Task.FromResult<ClickHouseTableInfo?>(null);

        public Task<ClickHouseTableInfo?> GetTableAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string database, string table, CancellationToken cancellationToken) =>
            GetTableAsync(cluster, database, table, cancellationToken);

        public Task ExecuteAsync(ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken)
        {
            if (ExecuteException is not null)
            {
                throw ExecuteException;
            }

            return Task.CompletedTask;
        }

        public Task ExecuteAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken)
        {
            if (ExecuteException is not null)
            {
                throw ExecuteException;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ClickHouseShardReplicaInfo>> GetTopologyAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClickHouseShardReplicaInfo>>(Topology.ToList());

        public async Task<ClickHouseOperationResult> StartBackupAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, CancellationToken cancellationToken)
        {
            var shard = new BackupTableShardEntity { SourceShardNumber = 1, S3Path = table.S3Path };
            return await StartBackupShardAsync(new ClickHouseNodeEndpoint("source", 9000, false), cluster, target, table, shard, cancellationToken);
        }

        public async Task<ClickHouseOperationResult> StartBackupShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, BackupTableShardEntity shard, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _activeBackupStarts++;
                MaxConcurrentBackupStarts = Math.Max(MaxConcurrentBackupStarts, _activeBackupStarts);
                BackupStartTables.Add(table.Table);
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
            if (!DropStartedBackupOperation)
            {
                KnownOperations[operationId] = NextBackupStatus;
                if (!string.IsNullOrWhiteSpace(NextBackupError))
                {
                    OperationErrors[operationId] = NextBackupError;
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
            RestoreStartTables.Add(backupTable.Table);
            var operationId = $"restore-{backupTable.Table}-s{backupShard.SourceShardNumber}-{Guid.NewGuid():N}";
            if (!DropStartedRestoreOperation)
            {
                KnownOperations[operationId] = NextRestoreStatus;
                if (!string.IsNullOrWhiteSpace(NextRestoreError))
                {
                    OperationErrors[operationId] = NextRestoreError;
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

            return KnownOperations.TryGetValue(operationId, out var status)
                ? new ClickHouseOperationStatus(true, status, OperationErrors.GetValueOrDefault(operationId))
                : new ClickHouseOperationStatus(false, null, null);
        }

        public Task WaitForBlockedStatusAsync() => _statusBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void ReleaseBlockedStatus() => _releaseStatus.TrySetResult();
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public IServiceProvider Services { get; }
        public ChoboDbContext Db { get; }
        public FakeClickHouseAdapter ClickHouse { get; }
        public Guid SourceClusterId { get; }
        public Guid TargetClusterId { get; }
        public Guid TargetId { get; }

        private TestFixture(SqliteConnection connection, IServiceProvider services, ChoboDbContext db, FakeClickHouseAdapter clickHouse, Guid sourceClusterId, Guid targetClusterId, Guid targetId)
        {
            _connection = connection;
            Services = services;
            Db = db;
            ClickHouse = clickHouse;
            SourceClusterId = sourceClusterId;
            TargetClusterId = targetClusterId;
            TargetId = targetId;
        }

        public static async Task<TestFixture> CreateAsync(int? clusterMaxDop = null, ChoboBackupRestoreOptions? options = null, TimeProvider? timeProvider = null)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var fake = new FakeClickHouseAdapter();
            var services = new ServiceCollection()
                .AddSingleton(connection)
                .AddDbContext<ChoboDbContext>((provider, builder) => builder.UseSqlite(provider.GetRequiredService<SqliteConnection>()))
                .AddSingleton(fake)
                .AddScoped<IClickHouseAdapter>(provider => provider.GetRequiredService<FakeClickHouseAdapter>())
                .AddScoped<PolicySelectorEvaluationService>()
                .AddScoped<ActorContext>()
                .AddScoped<AuditService>()
                .AddSingleton(Options.Create(new ChoboTestHooksOptions()))
                .AddSingleton<TestHookCoordinator>()
                .AddScoped<DashboardApplicationService>()
                .AddScoped<RestoreApplicationService>()
                .AddScoped<BackupRunnerService>()
                .AddScoped<RestoreRunnerService>()
                .AddSingleton<BackupRestoreQueues>()
                .AddSingleton(Options.Create(options ?? new ChoboBackupRestoreOptions { MaxDop = 3, PollInterval = TimeSpan.FromMilliseconds(1), SchedulerInterval = TimeSpan.FromSeconds(1) }))
                .AddSingleton(timeProvider ?? TimeProvider.System)
                .AddSingleton<BackupSchedulerDispatcherBackgroundService>()
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

            return new TestFixture(connection, services, db, fake, sourceClusterId, targetClusterId, targetId);
        }

        public async Task<Guid> CreateManualBackupAsync(PolicySelector? selector = null, string actorName = "system", Guid? actorUserId = null)
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
                ManualRequestJson = System.Text.Json.JsonSerializer.Serialize(new ManualBackupRequest(SourceClusterId, TargetId, selector ?? PolicySelector.Empty), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
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
            var policy = new BackupPolicyEntity
            {
                Name = "hourly",
                SourceClusterId = SourceClusterId,
                TargetId = TargetId,
                SelectorJson = """{"version":1,"rules":[{"action":"Include","database":{"kind":"All","value":"*"},"table":{"kind":"All","value":"*"}}]}"""
            };
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
            await _connection.DisposeAsync();
        }
    }

    private static class BackupRestoreMappingAccessor
    {
        public static BackupDto ToDto(BackupEntity backup) =>
            new(
                backup.Id,
                backup.TriggerType,
                backup.Status,
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
                backup.Tables.Select(table => new BackupTableDto(
                    table.Id,
                    table.BackupId,
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
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
