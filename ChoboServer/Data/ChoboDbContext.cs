using Chobo.Contracts;
using Microsoft.EntityFrameworkCore;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
        modelBuilder.Entity<BackupScheduleEntity>().HasIndex(x => new { x.IsDeleted, x.Name });
        modelBuilder.Entity<BackupScheduleEntity>().HasIndex(x => new { x.PolicyId, x.IsDeleted });
        modelBuilder.Entity<ClickHouseClusterEntity>().HasMany(x => x.AccessNodes).WithOne(x => x.Cluster).HasForeignKey(x => x.ClusterId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BackupPolicyEntity>().HasOne(x => x.SourceCluster).WithMany().HasForeignKey(x => x.SourceClusterId);
        modelBuilder.Entity<BackupScheduleEntity>().HasOne(x => x.Policy).WithMany().HasForeignKey(x => x.PolicyId);
    }
}
