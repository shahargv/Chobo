using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Services;

public sealed class ExportImportService(ChoboDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<ExportEnvelope> ExportAsync(bool configOnly)
    {
        var users = await db.Users.ToListAsync();
        var tokens = await db.AccessTokens.ToListAsync();
        var clusters = await db.ClickHouseClusters.Include(x => x.AccessNodes).ToListAsync();
        var targets = await db.BackupTargets.ToListAsync();
        var policies = await db.BackupPolicies.ToListAsync();
        var schedules = await db.BackupSchedules.ToListAsync();
        var audits = configOnly ? [] : await db.AuditEntries.ToListAsync();
        var logs = configOnly ? [] : await db.ApplicationLogEntries.ToListAsync();

        var payload = new ExportPayload(
            users.Select(x => new UserExport(x.Id, x.UserName, x.IsActive, x.CreatedAt, x.DeactivatedAt)).ToList(),
            tokens.Select(x => new AccessTokenExport(x.Id, x.UserId, x.Name, x.TokenHash, x.TokenLookupHash, x.Salt, x.IsActive, x.CreatedAt, x.DeactivatedAt)).ToList(),
            clusters.Select(x => new ClusterExport(x.Id, x.Name, x.Mode, x.AccessNodes.Select(n => new AccessNodeDto(n.Id, n.Host, n.Port, n.UseTls)).ToList(), x.EncryptedUserName, x.EncryptedPassword, x.BackupRestoreMaxDop, x.IsDeleted, x.CreatedAt, x.UpdatedAt, x.DeletedAt)).ToList(),
            targets.Select(x => new BackupTargetExport(x.Id, x.Name, x.Type, new S3TargetSettingsDto(x.Endpoint, x.Region, x.Bucket, x.PathPrefix, x.ForcePathStyle), x.EncryptedAccessKey, x.EncryptedSecretKey, x.IsDeleted, x.CreatedAt, x.UpdatedAt, x.DeletedAt)).ToList(),
            policies.Select(x => new BackupPolicyExport(x.Id, x.Name, x.SourceClusterId, x.TargetId, x.SelectorJsonVersion, JsonSerializer.Deserialize<PolicySelector>(x.SelectorJson, JsonOptions)!, x.IsDeleted, x.CreatedAt, x.UpdatedAt, x.DeletedAt)).ToList(),
            schedules.Select(x => new BackupScheduleExport(x.Id, x.Name, x.PolicyId, x.BackupType, x.CronExpression, x.TimeZoneId, x.IsEnabled, x.Description, x.IsDeleted, x.CreatedAt, x.UpdatedAt, x.DeletedAt)).ToList(),
            audits.Select(x => new AuditEntryDto(x.Id, x.Timestamp, x.ActorUserId, x.ActorName, x.Action, x.EntityType, x.EntityId, AuditDetails.ToJsonElement(x.Details))).ToList(),
            logs.Select(x => new LogEntryDto(x.Id, x.Timestamp, x.Level, ExtractSourceContext(x.Properties), x.RenderedMessage, x.Exception)).ToList());

        return new ExportEnvelope(ChoboApi.ExportVersion, ChoboApi.SchemaVersion, DateTimeOffset.UtcNow, ChoboApi.ServerVersion, payload);
    }

    public async Task ImportAsync(ExportEnvelope envelope, bool configOnly)
    {
        if (envelope.ExportVersion != ChoboApi.ExportVersion)
        {
            throw new InvalidOperationException($"Unsupported export version {envelope.ExportVersion}.");
        }
        if (envelope.SchemaVersion > ChoboApi.SchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported schema version {envelope.SchemaVersion}.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await db.BackupSchedules.ExecuteDeleteAsync();
            await db.BackupPolicies.ExecuteDeleteAsync();
            await db.BackupTargets.ExecuteDeleteAsync();
            await db.ClickHouseAccessNodes.ExecuteDeleteAsync();
            await db.ClickHouseClusters.ExecuteDeleteAsync();
            await db.AccessTokens.ExecuteDeleteAsync();
            await db.Users.ExecuteDeleteAsync();
            if (!configOnly)
            {
                await db.AuditEntries.ExecuteDeleteAsync();
                await db.ApplicationLogEntries.ExecuteDeleteAsync();
            }

            db.Users.AddRange(envelope.Data.Users.Select(x => new UserEntity { Id = x.Id, UserName = x.UserName, IsActive = x.IsActive, CreatedAt = x.CreatedAt, DeactivatedAt = x.DeactivatedAt }));
            db.AccessTokens.AddRange(envelope.Data.AccessTokens.Select(x => new AccessTokenEntity { Id = x.Id, UserId = x.UserId, Name = x.Name, TokenHash = x.TokenHash, TokenLookupHash = x.TokenLookupHash, Salt = x.Salt, IsActive = x.IsActive, CreatedAt = x.CreatedAt, DeactivatedAt = x.DeactivatedAt }));
            foreach (var cluster in envelope.Data.Clusters)
            {
                db.ClickHouseClusters.Add(new ClickHouseClusterEntity
                {
                    Id = cluster.Id, Name = cluster.Name, Mode = cluster.Mode, EncryptedUserName = cluster.EncryptedUserName, EncryptedPassword = cluster.EncryptedPassword, BackupRestoreMaxDop = cluster.BackupRestoreMaxDop,
                    IsDeleted = cluster.IsDeleted, CreatedAt = cluster.CreatedAt, UpdatedAt = cluster.UpdatedAt, DeletedAt = cluster.DeletedAt,
                    AccessNodes = cluster.AccessNodes.Select(n => new ClickHouseAccessNodeEntity { Id = n.Id, Host = n.Host, Port = n.Port, UseTls = n.UseTls }).ToList()
                });
            }
            db.BackupTargets.AddRange(envelope.Data.BackupTargets.Select(x => new BackupTargetEntity { Id = x.Id, Name = x.Name, Type = x.Type, Endpoint = x.S3.Endpoint, Region = x.S3.Region, Bucket = x.S3.Bucket, PathPrefix = x.S3.PathPrefix, ForcePathStyle = x.S3.ForcePathStyle, EncryptedAccessKey = x.EncryptedAccessKey, EncryptedSecretKey = x.EncryptedSecretKey, IsDeleted = x.IsDeleted, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt, DeletedAt = x.DeletedAt }));
            db.BackupPolicies.AddRange(envelope.Data.BackupPolicies.Select(x => new BackupPolicyEntity { Id = x.Id, Name = x.Name, SourceClusterId = x.SourceClusterId, TargetId = x.TargetId, SelectorJsonVersion = x.SelectorJsonVersion, SelectorJson = JsonSerializer.Serialize(x.Selector, JsonOptions), IsDeleted = x.IsDeleted, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt, DeletedAt = x.DeletedAt }));
            db.BackupSchedules.AddRange(envelope.Data.BackupSchedules.Select(x => new BackupScheduleEntity { Id = x.Id, Name = x.Name, PolicyId = x.PolicyId, BackupType = x.BackupType, CronExpression = x.CronExpression, TimeZoneId = x.TimeZoneId, IsEnabled = x.IsEnabled, Description = x.Description, IsDeleted = x.IsDeleted, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt, DeletedAt = x.DeletedAt }));
            if (!configOnly)
            {
                db.AuditEntries.AddRange(envelope.Data.Audits.Select(x => new AuditEntryEntity { Id = x.Id, Timestamp = x.Timestamp, ActorUserId = x.ActorUserId, ActorName = x.ActorName, Action = x.Action, EntityType = x.EntityType, EntityId = x.EntityId, Details = AuditDetails.ToJsonString(x.Details) }));
                db.ApplicationLogEntries.AddRange(envelope.Data.Logs.Select(x => new ApplicationLogEntryEntity { Id = x.Id, Timestamp = x.Timestamp, Level = x.Level, RenderedMessage = x.Message, Exception = x.Exception, Properties = JsonSerializer.Serialize(new { SourceContext = x.Category }, JsonOptions) }));
            }
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static string ExtractSourceContext(string? propertiesJson)
    {
        if (string.IsNullOrWhiteSpace(propertiesJson))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(propertiesJson);
            if (!document.RootElement.TryGetProperty("SourceContext", out var sourceContext))
            {
                return "";
            }

            return sourceContext.ValueKind == JsonValueKind.String
                ? sourceContext.GetString() ?? ""
                : sourceContext.ToString();
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
