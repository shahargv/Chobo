using Chobo.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ChoboServer.Data;

public sealed class ChoboDbContext(DbContextOptions<ChoboDbContext> options) : DbContext(options)
{
    public DbSet<SchemaStateEntity> SchemaStates => Set<SchemaStateEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<AccessTokenEntity> AccessTokens => Set<AccessTokenEntity>();
    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();
    public DbSet<ApplicationLogEntryEntity> ApplicationLogEntries => Set<ApplicationLogEntryEntity>();
    public DbSet<ClickHouseClusterEntity> ClickHouseClusters => Set<ClickHouseClusterEntity>();
    public DbSet<ClickHouseAccessNodeEntity> ClickHouseAccessNodes => Set<ClickHouseAccessNodeEntity>();
    public DbSet<BackupTargetEntity> BackupTargets => Set<BackupTargetEntity>();
    public DbSet<BackupPolicyEntity> BackupPolicies => Set<BackupPolicyEntity>();
    public DbSet<BackupScheduleEntity> BackupSchedules => Set<BackupScheduleEntity>();
    public DbSet<SchemaDefinitionEntity> SchemaDefinitions => Set<SchemaDefinitionEntity>();
    public DbSet<BackupEntity> Backups => Set<BackupEntity>();
    public DbSet<BackupTableEntity> BackupTables => Set<BackupTableEntity>();
    public DbSet<RestoreEntity> Restores => Set<RestoreEntity>();
    public DbSet<RestoreTableEntity> RestoreTables => Set<RestoreTableEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        UseUnixMillisecondTimestamps(modelBuilder);

        modelBuilder.Entity<SchemaStateEntity>().HasKey(x => x.Id);
        modelBuilder.Entity<UserEntity>().HasIndex(x => x.UserName).IsUnique();
        modelBuilder.Entity<AccessTokenEntity>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<AccessTokenEntity>().HasIndex(x => x.TokenLookupHash);
        modelBuilder.Entity<AccessTokenEntity>().HasIndex(x => new { x.IsActive, x.TokenLookupHash });
        modelBuilder.Entity<AccessTokenEntity>().HasIndex(x => new { x.UserId, x.IsActive });
        modelBuilder.Entity<AuditEntryEntity>().HasIndex(x => x.Timestamp);
        modelBuilder.Entity<AuditEntryEntity>().HasIndex(x => new { x.ActorUserId, x.Timestamp });
        modelBuilder.Entity<AuditEntryEntity>().HasIndex(x => new { x.EntityType, x.Timestamp });
        modelBuilder.Entity<ApplicationLogEntryEntity>().HasIndex(x => x.Timestamp);
        modelBuilder.Entity<ApplicationLogEntryEntity>().HasIndex(x => new { x.Level, x.Timestamp });
        modelBuilder.Entity<ClickHouseClusterEntity>().HasIndex(x => new { x.IsDeleted, x.Name });
        modelBuilder.Entity<ClickHouseAccessNodeEntity>().HasIndex(x => x.ClusterId);
        modelBuilder.Entity<BackupTargetEntity>().HasIndex(x => new { x.IsDeleted, x.Name });
        modelBuilder.Entity<BackupPolicyEntity>().HasIndex(x => new { x.IsDeleted, x.Name });
        modelBuilder.Entity<BackupPolicyEntity>().HasIndex(x => x.SourceClusterId);
        modelBuilder.Entity<BackupPolicyEntity>().HasIndex(x => x.TargetId);
        modelBuilder.Entity<BackupScheduleEntity>().HasIndex(x => new { x.IsDeleted, x.Name });
        modelBuilder.Entity<BackupScheduleEntity>().HasIndex(x => new { x.PolicyId, x.IsDeleted });
        modelBuilder.Entity<SchemaDefinitionEntity>().HasIndex(x => x.SchemaHash).IsUnique();
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.Status);
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.PolicyId);
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.ScheduleId);
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.SourceClusterId);
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.TargetId);
        modelBuilder.Entity<BackupEntity>().HasIndex(x => x.CreatedAt);
        modelBuilder.Entity<BackupTableEntity>().HasIndex(x => x.BackupId);
        modelBuilder.Entity<BackupTableEntity>().HasIndex(x => new { x.Database, x.Table });
        modelBuilder.Entity<BackupTableEntity>().HasIndex(x => x.Status);
        modelBuilder.Entity<RestoreEntity>().HasIndex(x => x.Status);
        modelBuilder.Entity<RestoreEntity>().HasIndex(x => x.BackupId);
        modelBuilder.Entity<RestoreEntity>().HasIndex(x => x.TargetClusterId);
        modelBuilder.Entity<RestoreTableEntity>().HasIndex(x => x.RestoreId);
        modelBuilder.Entity<RestoreTableEntity>().HasIndex(x => x.BackupTableId);
        modelBuilder.Entity<ClickHouseClusterEntity>().HasMany(x => x.AccessNodes).WithOne(x => x.Cluster).HasForeignKey(x => x.ClusterId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BackupPolicyEntity>().HasOne(x => x.SourceCluster).WithMany().HasForeignKey(x => x.SourceClusterId);
        modelBuilder.Entity<BackupPolicyEntity>().HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId);
        modelBuilder.Entity<BackupScheduleEntity>().HasOne(x => x.Policy).WithMany().HasForeignKey(x => x.PolicyId);
        modelBuilder.Entity<BackupEntity>().HasOne(x => x.SourceCluster).WithMany().HasForeignKey(x => x.SourceClusterId);
        modelBuilder.Entity<BackupEntity>().HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId);
        modelBuilder.Entity<BackupEntity>().HasOne(x => x.Policy).WithMany().HasForeignKey(x => x.PolicyId);
        modelBuilder.Entity<BackupEntity>().HasOne(x => x.Schedule).WithMany().HasForeignKey(x => x.ScheduleId);
        modelBuilder.Entity<BackupEntity>().HasMany(x => x.Tables).WithOne(x => x.Backup).HasForeignKey(x => x.BackupId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BackupTableEntity>().HasOne(x => x.SchemaDefinition).WithMany().HasForeignKey(x => x.SchemaDefinitionId);
        modelBuilder.Entity<RestoreEntity>().HasOne(x => x.Backup).WithMany().HasForeignKey(x => x.BackupId);
        modelBuilder.Entity<RestoreEntity>().HasOne(x => x.TargetCluster).WithMany().HasForeignKey(x => x.TargetClusterId);
        modelBuilder.Entity<RestoreEntity>().HasMany(x => x.Tables).WithOne(x => x.Restore).HasForeignKey(x => x.RestoreId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RestoreTableEntity>().HasOne(x => x.BackupTable).WithMany().HasForeignKey(x => x.BackupTableId);
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
}
