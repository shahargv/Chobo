using Chobo.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ChoboServer.Data;

public sealed class ChoboDbContext(DbContextOptions<ChoboDbContext> options) : DbContext(options)
{
    private const int SqliteBusyRetryCount = 3;
    private static readonly TimeSpan SqliteBusyRetryDelay = TimeSpan.FromSeconds(1);

    public DbSet<SchemaStateEntity> SchemaStates => Set<SchemaStateEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<AccessTokenEntity> AccessTokens => Set<AccessTokenEntity>();
    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();
    public DbSet<ClickHouseClusterEntity> ClickHouseClusters => Set<ClickHouseClusterEntity>();
    public DbSet<ClickHouseAccessNodeEntity> ClickHouseAccessNodes => Set<ClickHouseAccessNodeEntity>();
    public DbSet<BackupTargetEntity> BackupTargets => Set<BackupTargetEntity>();
    public DbSet<BackupPolicyEntity> BackupPolicies => Set<BackupPolicyEntity>();
    public DbSet<BackupScheduleEntity> BackupSchedules => Set<BackupScheduleEntity>();
    public DbSet<SchemaDefinitionEntity> SchemaDefinitions => Set<SchemaDefinitionEntity>();
    public DbSet<BackupEntity> Backups => Set<BackupEntity>();
    public DbSet<BackupTableEntity> BackupTables => Set<BackupTableEntity>();
    public DbSet<BackupTableShardEntity> BackupTableShards => Set<BackupTableShardEntity>();
    public DbSet<RestoreEntity> Restores => Set<RestoreEntity>();
    public DbSet<RestoreTableEntity> RestoreTables => Set<RestoreTableEntity>();
    public DbSet<RestoreTableShardEntity> RestoreTableShards => Set<RestoreTableShardEntity>();
    public DbSet<BackupRestoreQueueItemEntity> BackupRestoreQueueItems => Set<BackupRestoreQueueItemEntity>();
    public DbSet<SqliteSelfBackupStateEntity> SqliteSelfBackupStates => Set<SqliteSelfBackupStateEntity>();

    public override int SaveChanges()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return base.SaveChanges();
            }
            catch (SqliteException ex) when (IsTransientSqliteLock(ex) && attempt < SqliteBusyRetryCount)
            {
                Thread.Sleep(SqliteBusyRetryDelay);
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return base.SaveChanges(acceptAllChangesOnSuccess);
            }
            catch (SqliteException ex) when (IsTransientSqliteLock(ex) && attempt < SqliteBusyRetryCount)
            {
                Thread.Sleep(SqliteBusyRetryDelay);
            }
        }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await SaveChangesAsync(true, cancellationToken);

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            }
            catch (SqliteException ex) when (IsTransientSqliteLock(ex) && attempt < SqliteBusyRetryCount)
            {
                await Task.Delay(SqliteBusyRetryDelay, cancellationToken);
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        UseUnixMillisecondTimestamps(modelBuilder);

        modelBuilder.Entity<SchemaStateEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<SqliteSelfBackupStateEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<UserEntity>().HasIndex(x => x.UserName).IsUnique();
        modelBuilder.Entity<AccessTokenEntity>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<AccessTokenEntity>().HasIndex(x => x.TokenLookupHash);
        modelBuilder.Entity<AccessTokenEntity>().HasIndex(x => new { x.IsActive, x.TokenLookupHash });
        modelBuilder.Entity<AccessTokenEntity>().HasIndex(x => new { x.UserId, x.IsActive });
        modelBuilder.Entity<AuditEntryEntity>().HasIndex(x => x.Timestamp);
        modelBuilder.Entity<AuditEntryEntity>().HasIndex(x => new { x.ActorUserId, x.Timestamp });
        modelBuilder.Entity<AuditEntryEntity>().HasIndex(x => new { x.EntityType, x.Timestamp });
        modelBuilder.Entity<AuditEntryEntity>().HasIndex(x => new { x.OperationId, x.Timestamp });
        modelBuilder.Entity<AuditEntryEntity>().HasIndex(x => new { x.Timestamp, x.Id });
        modelBuilder.Entity<AuditEntryEntity>().HasIndex(x => new { x.OperationId, x.Timestamp, x.Id });
        modelBuilder.Entity<ClickHouseClusterEntity>().HasIndex(x => new { x.IsDeleted, x.Name });
        modelBuilder.Entity<ClickHouseAccessNodeEntity>().HasIndex(x => x.ClusterId);
        modelBuilder.Entity<BackupTargetEntity>().HasIndex(x => new { x.IsDeleted, x.Name });
        modelBuilder.Entity<BackupPolicyEntity>().HasIndex(x => new { x.IsDeleted, x.Name });
        modelBuilder.Entity<BackupPolicyEntity>().HasIndex(x => x.SourceClusterId);
        modelBuilder.Entity<BackupPolicyEntity>().HasIndex(x => x.TargetId);
        modelBuilder.Entity<BackupPolicyEntity>().Property(x => x.FailedBackupRetentionMode).HasConversion<int>();
        modelBuilder.Entity<BackupPolicyEntity>().Property(x => x.ContentMode).HasConversion<int>();
        modelBuilder.Entity<BackupScheduleEntity>().HasIndex(x => new { x.IsDeleted, x.Name });
        modelBuilder.Entity<BackupScheduleEntity>().HasIndex(x => new { x.IsEnabled, x.IsDeleted });
        modelBuilder.Entity<BackupScheduleEntity>().HasIndex(x => new { x.PolicyId, x.IsDeleted });
        modelBuilder.Entity<SchemaDefinitionEntity>().HasIndex(x => x.SchemaHash).IsUnique();
        modelBuilder.Entity<BackupRestoreQueueItemEntity>().Property(x => x.Kind).HasConversion<int>();
        modelBuilder.Entity<BackupRestoreQueueItemEntity>().HasIndex(x => new { x.IsForced, x.Position });
        modelBuilder.Entity<BackupRestoreQueueItemEntity>().HasIndex(x => x.Position);
        modelBuilder.Entity<BackupRestoreQueueItemEntity>().HasIndex(x => x.CompletedAt);
        modelBuilder.Entity<BackupRestoreQueueItemEntity>().HasIndex(x => new { x.StartedAt, x.CompletedAt, x.IsForced, x.Position, x.CreatedAt });
        modelBuilder.Entity<BackupRestoreQueueItemEntity>().HasIndex(x => new { x.Kind, x.OperationId });
        modelBuilder.Entity<BackupRestoreQueueItemEntity>().HasIndex(x => x.ShardId).IsUnique();
        modelBuilder.Entity<BackupRestoreQueueItemEntity>().HasIndex(x => new { x.ClusterId, x.StartedAt, x.CompletedAt });
        modelBuilder.Entity<BackupRestoreQueueItemEntity>().HasIndex(x => new { x.ClusterId, x.LogicalShardNumber, x.StartedAt, x.CompletedAt });
        modelBuilder.Entity<BackupRestoreQueueItemEntity>().HasIndex(x => new { x.ClusterId, x.NodeHost, x.NodePort, x.NodeUseTls, x.StartedAt, x.CompletedAt });
        modelBuilder.Entity<BackupEntity>().Property(x => x.ContentMode).HasConversion<int>();
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.Status);
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.PolicyId);
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.ScheduleId);
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.SourceClusterId);
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.TargetId);
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.CreatedAt);
        modelBuilder.Entity<BackupEntity>().HasIndex(x => new { x.PolicyId, x.BackupType, x.Status, x.CompletedAt });
        modelBuilder.Entity<BackupEntity>().HasIndex(x => new { x.PolicyId, x.Status, x.CompletedAt });
        modelBuilder.Entity<BackupEntity>().HasIndex(x => new { x.ScheduleId, x.CreatedAt });
        modelBuilder.Entity<BackupEntity>().HasIndex(x => new { x.ScheduleId, x.Status, x.CompletedAt });
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.IsPinned);
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.DeletionRequestedAt);
        modelBuilder.Entity<BackupTableEntity>().HasIndex(x => x.BackupId);
        modelBuilder.Entity<BackupTableEntity>().HasIndex(x => new { x.Database, x.Table });
        modelBuilder.Entity<BackupTableEntity>().HasIndex(x => x.Table);
        modelBuilder.Entity<BackupTableEntity>().HasIndex(x => new { x.EffectiveBackupType, x.ParentFullBackupTableId });
        modelBuilder.Entity<BackupTableEntity>().HasIndex(x => x.ParentFullBackupId);
        modelBuilder.Entity<BackupTableEntity>().HasIndex(x => new { x.EffectiveBackupType, x.Database, x.Table });
        modelBuilder.Entity<BackupTableEntity>().HasIndex(x => x.Status);
        modelBuilder.Entity<BackupTableEntity>().HasIndex(x => x.ParentFullBackupTableId);
        modelBuilder.Entity<BackupTableShardEntity>().HasIndex(x => x.BackupTableId);
        modelBuilder.Entity<BackupTableShardEntity>().HasIndex(x => new { x.BackupTableId, x.SourceShardNumber });
        modelBuilder.Entity<BackupTableShardEntity>().HasIndex(x => new { x.EffectiveBackupType, x.ParentFullBackupTableShardId });
        modelBuilder.Entity<BackupTableShardEntity>().HasIndex(x => x.ParentFullBackupId);
        modelBuilder.Entity<BackupTableShardEntity>().HasIndex(x => x.Status);
        modelBuilder.Entity<BackupTableShardEntity>().HasIndex(x => x.ParentFullBackupTableShardId);
        modelBuilder.Entity<RestoreEntity>().HasIndex(x => x.Status);
        modelBuilder.Entity<RestoreEntity>().HasIndex(x => x.BackupId);
        modelBuilder.Entity<RestoreEntity>().HasIndex(x => x.TargetClusterId);
        modelBuilder.Entity<RestoreEntity>().HasIndex(x => x.CreatedAt);
        modelBuilder.Entity<RestoreTableEntity>().HasIndex(x => x.RestoreId);
        modelBuilder.Entity<RestoreTableEntity>().HasIndex(x => x.BackupTableId);
        modelBuilder.Entity<RestoreTableShardEntity>().HasIndex(x => x.RestoreTableId);
        modelBuilder.Entity<RestoreTableShardEntity>().HasIndex(x => x.BackupTableShardId);
        modelBuilder.Entity<RestoreTableShardEntity>().HasIndex(x => x.Status);
        modelBuilder.Entity<ClickHouseClusterEntity>().HasMany(x => x.AccessNodes).WithOne(x => x.Cluster).HasForeignKey(x => x.ClusterId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BackupPolicyEntity>().HasOne(x => x.SourceCluster).WithMany().HasForeignKey(x => x.SourceClusterId);
        modelBuilder.Entity<BackupPolicyEntity>().HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<BackupScheduleEntity>().HasOne(x => x.Policy).WithMany().HasForeignKey(x => x.PolicyId);
        modelBuilder.Entity<BackupEntity>().HasOne(x => x.SourceCluster).WithMany().HasForeignKey(x => x.SourceClusterId);
        modelBuilder.Entity<BackupEntity>().HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<BackupEntity>().HasOne(x => x.Policy).WithMany().HasForeignKey(x => x.PolicyId);
        modelBuilder.Entity<BackupEntity>().HasOne(x => x.Schedule).WithMany().HasForeignKey(x => x.ScheduleId);
        modelBuilder.Entity<BackupEntity>().HasMany(x => x.Tables).WithOne(x => x.Backup).HasForeignKey(x => x.BackupId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BackupTableEntity>().HasOne(x => x.SchemaDefinition).WithMany().HasForeignKey(x => x.SchemaDefinitionId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<BackupTableEntity>().HasOne(x => x.ParentFullBackupTable).WithMany().HasForeignKey(x => x.ParentFullBackupTableId);
        modelBuilder.Entity<BackupTableEntity>().HasMany(x => x.Shards).WithOne(x => x.BackupTable).HasForeignKey(x => x.BackupTableId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BackupTableShardEntity>().HasOne(x => x.ParentFullBackupTableShard).WithMany().HasForeignKey(x => x.ParentFullBackupTableShardId);
        modelBuilder.Entity<RestoreEntity>().HasOne(x => x.Backup).WithMany().HasForeignKey(x => x.BackupId);
        modelBuilder.Entity<RestoreEntity>().HasOne(x => x.TargetCluster).WithMany().HasForeignKey(x => x.TargetClusterId);
        modelBuilder.Entity<RestoreEntity>().HasMany(x => x.Tables).WithOne(x => x.Restore).HasForeignKey(x => x.RestoreId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RestoreTableEntity>().HasOne(x => x.BackupTable).WithMany().HasForeignKey(x => x.BackupTableId);
        modelBuilder.Entity<RestoreTableEntity>().HasMany(x => x.Shards).WithOne(x => x.RestoreTable).HasForeignKey(x => x.RestoreTableId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RestoreTableShardEntity>().HasOne(x => x.BackupTableShard).WithMany().HasForeignKey(x => x.BackupTableShardId);
    }

    private static void UseUnixMillisecondTimestamps(ModelBuilder modelBuilder)
    {
        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
            value => value.ToUniversalTime().ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.ToUniversalTime().ToUnixTimeMilliseconds() : null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.ClrType.GetProperties())
            {
                if (property.PropertyType == typeof(DateTimeOffset))
                {
                    modelBuilder.Entity(entity.ClrType)
                        .Property(property.Name)
                        .HasConversion(dateTimeOffsetConverter)
                        .HasColumnType("INTEGER");
                }
                else if (property.PropertyType == typeof(DateTimeOffset?))
                {
                    modelBuilder.Entity(entity.ClrType)
                        .Property(property.Name)
                        .HasConversion(nullableDateTimeOffsetConverter)
                        .HasColumnType("INTEGER");
                }
            }
        }
    }

    private static bool IsTransientSqliteLock(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6;
}


