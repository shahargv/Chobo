using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Application;

public sealed class SystemDefaultBackupPolicyService(ChoboDbContext db, IAuditService audit)
{
    public const string PolicyNamePrefix = "Daily schema snapshot";
    public const string ScheduleNamePrefix = "Daily schema snapshot";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task EnsureForClusterAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken = default)
    {
        var policyId = DeterministicGuid("chobo-default-schema-policy", cluster.Id);
        var scheduleId = DeterministicGuid("chobo-default-schema-schedule", cluster.Id);
        var now = DateTimeOffset.UtcNow;
        var selectorJson = JsonSerializer.Serialize(PolicySelector.Empty, JsonOptions);

        var policy = await db.BackupPolicies.FirstOrDefaultAsync(x => x.Id == policyId, cancellationToken);
        if (policy is null)
        {
            policy = new BackupPolicyEntity
            {
                Id = policyId,
                Name = $"{PolicyNamePrefix} - {cluster.Name}",
                SourceClusterId = cluster.Id,
                TargetId = null,
                ContentMode = BackupContentMode.SchemaOnly,
                SelectorJsonVersion = PolicySelector.Empty.Version,
                SelectorJson = selectorJson,
                IsSystemDefault = true,
                CreatedAt = now
            };
            db.BackupPolicies.Add(policy);
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync("create-system-default", AuditEntityType.BackupPolicy, policy.Id.ToString(), new { clusterId = cluster.Id, policy.ContentMode });
        }
        else
        {
            policy.Name = $"{PolicyNamePrefix} - {cluster.Name}";
            policy.SourceClusterId = cluster.Id;
            policy.TargetId = null;
            policy.ContentMode = BackupContentMode.SchemaOnly;
            policy.PasswordMode = BackupPasswordMode.None;
            policy.EncryptedBackupPassword = null;
            policy.EncryptedBackupPasswordKeyId = null;
            policy.CompressionMethod = null;
            policy.CompressionLevel = null;
            policy.SelectorJsonVersion = PolicySelector.Empty.Version;
            policy.SelectorJson = selectorJson;
            policy.IsSystemDefault = true;
            policy.IsDeleted = false;
            policy.DeletedAt = null;
            policy.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        var schedule = await db.BackupSchedules.FirstOrDefaultAsync(x => x.Id == scheduleId, cancellationToken);
        if (schedule is null)
        {
            schedule = new BackupScheduleEntity
            {
                Id = scheduleId,
                Name = $"{ScheduleNamePrefix} - {cluster.Name}",
                PolicyId = policyId,
                BackupType = BackupType.Full,
                CronExpression = "0 0 2 * * ?",
                TimeZoneId = "UTC",
                IsEnabled = true,
                Description = "Automatic daily schema-only backup.",
                IsSystemDefault = true,
                CreatedAt = now
            };
            db.BackupSchedules.Add(schedule);
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync("create-system-default", AuditEntityType.BackupSchedule, schedule.Id.ToString(), new { clusterId = cluster.Id, policyId, schedule.BackupType });
        }
        else
        {
            schedule.Name = $"{ScheduleNamePrefix} - {cluster.Name}";
            schedule.PolicyId = policyId;
            schedule.BackupType = BackupType.Full;
            schedule.CronExpression = "0 0 2 * * ?";
            schedule.TimeZoneId = "UTC";
            schedule.IsEnabled = true;
            schedule.Description = "Automatic daily schema-only backup.";
            schedule.IsSystemDefault = true;
            schedule.IsDeleted = false;
            schedule.DeletedAt = null;
            schedule.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static Guid DeterministicGuid(string scope, Guid clusterId)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"{scope}:{clusterId:N}"));
        return new Guid(bytes);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
