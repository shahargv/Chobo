using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Repositories;
using ChoboServer.Services;
using ChoboServer.Options;
using Microsoft.Extensions.Options;

namespace ChoboServer.Application;

public sealed class PolicyApplicationService(
    IPolicyRepository policies,
    IScheduleRepository schedules,
    IClusterRepository clusters,
    ITargetRepository targets,
    IClickHouseClusterMetadataService metadata,
    IUnitOfWork unitOfWork,
    IAuditService audit,
    PolicySelectorEvaluationService selectorEvaluation,
    IOptionsMonitor<ChoboBackupRestoreOptions> backupRestoreOptions,
    ICredentialProtector protector,
    IAesKeyRepository aesKeys)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<IReadOnlyList<BackupPolicyDto>> ListAsync(bool includeDeleted = false)
    {
        var rows = await policies.ListAsync(includeDeleted);
        var availability = await aesKeys.GetAvailabilitiesAsync(rows.Select(x => x.EncryptedBackupPasswordKeyId).OfType<Guid>());
        return rows.Select(x => ToDto(x, availability)).ToList();
    }

    public async Task<BackupPolicyDto> AddAsync(UpsertPolicyRequest request)
    {
        await ValidateAsync(request);
        if (request.PasswordMode == BackupPasswordMode.Constant && request.BackupPassword is null)
        {
            throw new ArgumentException("Backup password is required for constant password mode.");
        }
        var protectedPassword = request.PasswordMode == BackupPasswordMode.Constant
            ? await protector.EncryptAsync(request.BackupPassword)
            : null;
        var policy = new BackupPolicyEntity
        {
            Name = request.Name.Trim(),
            SourceClusterId = request.SourceClusterId,
            TargetId = request.TargetId,
            ContentMode = request.ContentMode,
            SelectorJsonVersion = request.Selector.Version,
            SelectorJson = JsonSerializer.Serialize(request.Selector, JsonOptions),
            FullRetentionMinutes = request.Retention?.FullRetentionMinutes,
            IncrementalRetentionMinutes = request.Retention?.IncrementalRetentionMinutes,
            MinBackupsToKeep = request.Retention?.MinBackupsToKeep ?? 0,
            MinFullBackupsToKeep = request.Retention?.MinFullBackupsToKeep ?? 0,
            FailedBackupRetentionMode = request.FailedBackupRetentionMode,
            MaxAgeHoursForBaseBackup = request.MaxAgeHoursForBaseBackup,
            ClickHouseBackupSettingsJson = ClickHouseAdvancedSettings.Serialize(request.ClickHouseBackupSettings, ClickHouseAdvancedSettingsKind.Backup),
            ClickHouseRestoreSettingsJson = ClickHouseAdvancedSettings.Serialize(request.ClickHouseRestoreSettings, ClickHouseAdvancedSettingsKind.Restore),
            PasswordMode = request.PasswordMode,
            EncryptedBackupPassword = protectedPassword?.Ciphertext,
            EncryptedBackupPasswordKeyId = protectedPassword?.KeyId,
            CompressionMethod = request.CompressionMethod,
            CompressionLevel = request.CompressionLevel
        };

        await policies.AddAsync(policy);
        await unitOfWork.SaveChangesAsync();

        var currentAvailability = await aesKeys.GetAvailabilitiesAsync(
            protectedPassword is null ? [] : [protectedPassword.KeyId]);
        var current = ToDto(policy, currentAvailability);
        await audit.RecordAsync("create", AuditEntityType.BackupPolicy, policy.Id.ToString(), AuditDetails.Change(null, current));
        return current;
    }

    public async Task<BackupPolicyDto?> UpdateAsync(Guid id, UpsertPolicyRequest request)
    {
        var policy = await policies.FindActiveAsync(id);
        if (policy is null)
        {
            return null;
        }

        await ValidateAsync(request);
        if (request.PasswordMode == BackupPasswordMode.Constant && request.BackupPassword is null && string.IsNullOrEmpty(policy.EncryptedBackupPassword))
        {
            throw new ArgumentException("Backup password is required when switching to constant password mode.");
        }
        var previous = ToDto(policy);
        policy.Name = request.Name.Trim();
        policy.SourceClusterId = request.SourceClusterId;
        policy.TargetId = request.TargetId;
        policy.ContentMode = request.ContentMode;
        policy.SelectorJsonVersion = request.Selector.Version;
        policy.SelectorJson = JsonSerializer.Serialize(request.Selector, JsonOptions);
        policy.FullRetentionMinutes = request.Retention?.FullRetentionMinutes;
        policy.IncrementalRetentionMinutes = request.Retention?.IncrementalRetentionMinutes;
        policy.MinBackupsToKeep = request.Retention?.MinBackupsToKeep ?? 0;
        policy.MinFullBackupsToKeep = request.Retention?.MinFullBackupsToKeep ?? 0;
        policy.FailedBackupRetentionMode = request.FailedBackupRetentionMode;
        policy.MaxAgeHoursForBaseBackup = request.MaxAgeHoursForBaseBackup;
        policy.PasswordMode = request.PasswordMode;
        if (request.PasswordMode == BackupPasswordMode.Constant && request.BackupPassword is not null)
        {
            var protectedPassword = await protector.EncryptAsync(request.BackupPassword);
            policy.EncryptedBackupPassword = protectedPassword?.Ciphertext;
            policy.EncryptedBackupPasswordKeyId = protectedPassword?.KeyId;
        }
        else if (request.PasswordMode != BackupPasswordMode.Constant)
        {
            policy.EncryptedBackupPassword = null;
            policy.EncryptedBackupPasswordKeyId = null;
        }
        policy.CompressionMethod = request.CompressionMethod;
        policy.CompressionLevel = request.CompressionLevel;
        if (request.ClickHouseBackupSettings is not null)
        {
            policy.ClickHouseBackupSettingsJson = ClickHouseAdvancedSettings.Serialize(request.ClickHouseBackupSettings, ClickHouseAdvancedSettingsKind.Backup);
        }
        if (request.ClickHouseRestoreSettings is not null)
        {
            policy.ClickHouseRestoreSettingsJson = ClickHouseAdvancedSettings.Serialize(request.ClickHouseRestoreSettings, ClickHouseAdvancedSettingsKind.Restore);
        }
        policy.UpdatedAt = DateTimeOffset.UtcNow;
        await unitOfWork.SaveChangesAsync();

        var currentAvailability = await aesKeys.GetAvailabilitiesAsync(
            policy.EncryptedBackupPasswordKeyId is { } keyId ? [keyId] : []);
        var current = ToDto(policy, currentAvailability);
        await audit.RecordAsync("update", AuditEntityType.BackupPolicy, id.ToString(), AuditDetails.Change(previous, current));
        return current;
    }

    public async Task<PolicyEvaluationDto?> EvaluateAsync(Guid id, PolicyEvaluationRequest request)
    {
        var policy = await policies.FindActiveAsync(id);
        if (policy is null)
        {
            return null;
        }

        var selector = Deserialize(policy.SelectorJson);
        var selectedTables = selectorEvaluation.Evaluate(policy.SelectorJsonVersion, selector, request.Inventory);
        return new PolicyEvaluationDto(
            policy.Id,
            policy.Name,
            policy.SourceClusterId,
            policy.SelectorJsonVersion,
            selector,
            selectedTables);
    }

    public async Task<PolicyInventory?> ListInventoryAsync(Guid sourceClusterId, CancellationToken cancellationToken = default)
    {
        var cluster = await clusters.FindActiveAsync(sourceClusterId);
        if (cluster is null)
        {
            return null;
        }

        var snapshot = await metadata.GetAsync(cluster, cancellationToken);
        if (snapshot.NodeFailures.Count > 0)
        {
            throw new InvalidOperationException($"Could not read ClickHouse table inventory from every source node. Failed node count: {snapshot.NodeFailures.Count}. First error: {snapshot.NodeFailures.First().Error}");
        }

        return new PolicyInventory(snapshot.Placements
            .Select(x => x.Table)
            .DistinctBy(x => ClickHouseBackupIdentity.Table(x.Database, x.Table))
            .Select(x => new PolicyInventoryTable(x.Database, x.Table))
            .ToList());
    }

    public async Task<PolicySimulationDto?> SimulateAsync(PolicySimulationRequest request, CancellationToken cancellationToken = default)
    {
        var inventory = await ListInventoryAsync(request.SourceClusterId, cancellationToken);
        if (inventory is null)
        {
            return null;
        }

        var selectedTables = selectorEvaluation.Evaluate(request.Selector, inventory);
        return new PolicySimulationDto(request.SourceClusterId, request.Selector, inventory.Tables, selectedTables);
    }

    public async Task<bool> RemoveAsync(Guid id)
    {
        var policy = await policies.FindAsync(id);
        if (policy is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var previous = ToDto(policy);
        var relatedSchedules = await schedules.ListActiveByPolicyAsync(id);
        var previousSchedules = relatedSchedules.Select(ToScheduleAuditDto).ToList();
        policy.IsDeleted = true;
        policy.DeletedAt = now;
        foreach (var schedule in relatedSchedules)
        {
            schedule.IsDeleted = true;
            schedule.DeletedAt = now;
        }
        await unitOfWork.SaveChangesAsync();

        await audit.RecordAsync("delete", AuditEntityType.BackupPolicy, id.ToString(), new
        {
            change = AuditDetails.Deactivation(previous, ToDto(policy)),
            softDeletedScheduleIds = relatedSchedules.Select(x => x.Id).ToList()
        });
        foreach (var schedule in relatedSchedules.Zip(previousSchedules))
        {
            await audit.RecordAsync("delete", AuditEntityType.BackupSchedule, schedule.First.Id.ToString(), new
            {
                reason = "policy-deleted",
                policyId = id,
                change = AuditDetails.Deactivation(schedule.Second, ToScheduleAuditDto(schedule.First))
            });
        }
        return true;
    }

    private async Task ValidateAsync(UpsertPolicyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Name is required.");
        }
        if (request.SourceClusterId == Guid.Empty)
        {
            throw new ArgumentException("Source cluster id is required.");
        }        if (await clusters.FindActiveAsync(request.SourceClusterId) is null)
        {
            throw new ArgumentException("Source cluster was not found.");
        }
        if (request.ContentMode == BackupContentMode.SchemaAndData)
        {
            if (request.TargetId is null || request.TargetId == Guid.Empty)
            {
                throw new ArgumentException("Target id is required.");
            }
            if (await targets.FindActiveAsync(request.TargetId.Value) is null)
            {
                throw new ArgumentException("Target was not found.");
            }
        }
        else if (request.TargetId is { } targetId && targetId != Guid.Empty && await targets.FindActiveAsync(targetId) is null)
        {
            throw new ArgumentException("Target was not found.");
        }
        if (request.Selector.Version != 1)
        {
            throw new ArgumentException("Only selector version 1 is supported.");
        }
        if (request.MaxAgeHoursForBaseBackup is not null and <= 0)
        {
            throw new ArgumentException("Max age hours for base backup must be greater than zero.");
        }
        if (!Enum.IsDefined(request.PasswordMode))
        {
            throw new ArgumentException("Password mode is invalid.");
        }
        if (request.ContentMode == BackupContentMode.SchemaOnly && request.PasswordMode != BackupPasswordMode.None)
        {
            throw new ArgumentException("Schema-only policies cannot enable password protection.");
        }
        if (request.PasswordMode == BackupPasswordMode.Constant && request.BackupPassword is not null && request.BackupPassword.Length == 0)
        {
            throw new ArgumentException("Backup password cannot be empty.");
        }
        if (request.BackupPassword is { Length: > 4096 })
        {
            throw new ArgumentException("Backup password cannot exceed 4096 characters.");
        }
        if (request.PasswordMode != BackupPasswordMode.Constant && request.BackupPassword is not null)
        {
            throw new ArgumentException("Backup password can only be supplied for constant password mode.");
        }
        if (request.CompressionLevel is not null && request.CompressionMethod is null)
        {
            throw new ArgumentException("Compression method is required when compression level is supplied.");
        }
        if (request.CompressionMethod is { } compressionMethod && !Enum.IsDefined(compressionMethod))
        {
            throw new ArgumentException("Compression method is invalid.");
        }
        if (request.CompressionLevel is < 0)
        {
            throw new ArgumentException("Compression level cannot be negative.");
        }
        if (request.CompressionMethod == BackupCompressionMethod.Store && request.CompressionLevel is not null)
        {
            throw new ArgumentException("Compression level is not supported with Store compression.");
        }
        if (request.CompressionMethod is not null && request.ClickHouseBackupSettings?.Keys.Any(x => x.Equals("compression_method", StringComparison.OrdinalIgnoreCase) || x.Equals("compression_level", StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new ArgumentException("Use the first-class compression fields instead of policy advanced settings for compression.");
        }
        if (backupRestoreOptions.CurrentValue.DefaultMaxAgeHoursForBaseBackup <= 0)
        {
            throw new ArgumentException("Default max age hours for base backup must be greater than zero.");
        }
        if (request.Retention is not null)
        {
            if (request.Retention.FullRetentionMinutes is not null and <= 0)
            {
                throw new ArgumentException("Full retention minutes must be greater than zero.");
            }
            if (request.Retention.IncrementalRetentionMinutes is not null and <= 0)
            {
                throw new ArgumentException("Incremental retention minutes must be greater than zero.");
            }
            if (request.Retention.MinBackupsToKeep < 0)
            {
                throw new ArgumentException("MinBackupsToKeep must be zero or greater.");
            }
            if (request.Retention.MinFullBackupsToKeep < 0)
            {
                throw new ArgumentException("MinFullBackupsToKeep must be zero or greater.");
            }
        }
        ClickHouseAdvancedSettings.Normalize(request.ClickHouseBackupSettings, ClickHouseAdvancedSettingsKind.Backup);
        ClickHouseAdvancedSettings.Normalize(request.ClickHouseRestoreSettings, ClickHouseAdvancedSettingsKind.Restore);
        foreach (var rule in request.Selector.Rules)
        {
            ValidatePattern(rule.Database, "Database");
            ValidatePattern(rule.Table, "Table");
        }
    }

    private static object ToScheduleAuditDto(BackupScheduleEntity x) =>
        new
        {
            x.Id,
            x.Name,
            x.PolicyId,
            x.BackupType,
            x.CronExpression,
            x.TimeZoneId,
            x.IsEnabled,
            x.MissedRunGracePeriod,
            x.Description,
            x.IsSystemDefault,
            x.IsDeleted,
            x.CreatedAt,
            x.UpdatedAt
        };

    private static PolicySelector Deserialize(string json) =>
        JsonSerializer.Deserialize<PolicySelector>(json, JsonOptions) ?? PolicySelector.Empty;

    private BackupPolicyDto ToDto(BackupPolicyEntity x, IReadOnlyDictionary<Guid, AesKeyAvailability>? availability = null) =>
        new(
            x.Id,
            x.Name,
            x.SourceClusterId,
            x.TargetId,
            x.ContentMode,
            x.SelectorJsonVersion,
            Deserialize(x.SelectorJson),
            x.FullRetentionMinutes is null && x.IncrementalRetentionMinutes is null && x.MinBackupsToKeep == 0 && x.MinFullBackupsToKeep == 0
                ? null
                : new BackupRetentionDto(x.FullRetentionMinutes, x.IncrementalRetentionMinutes, x.MinBackupsToKeep, x.MinFullBackupsToKeep),
            x.FailedBackupRetentionMode,
            ClickHouseAdvancedSettings.Deserialize(x.ClickHouseBackupSettingsJson, ClickHouseAdvancedSettingsKind.Backup),
            ClickHouseAdvancedSettings.Deserialize(x.ClickHouseRestoreSettingsJson, ClickHouseAdvancedSettingsKind.Restore),
            x.IsSystemDefault,
            x.IsDeleted,
            x.CreatedAt,
            x.UpdatedAt,
            x.MaxAgeHoursForBaseBackup,
            x.MaxAgeHoursForBaseBackup ?? backupRestoreOptions.CurrentValue.DefaultMaxAgeHoursForBaseBackup,
            x.PasswordMode,
            !string.IsNullOrEmpty(x.EncryptedBackupPassword),
            x.PasswordMode == BackupPasswordMode.Constant && x.EncryptedBackupPasswordKeyId is { } keyId
                ? availability is null || !availability.TryGetValue(keyId, out var state) ? null : state == AesKeyAvailability.Available
                : null,
            x.CompressionMethod,
            x.CompressionLevel);

    private static void ValidatePattern(SelectorPattern pattern, string name)
    {
        if (pattern.Kind != PolicyMatchKind.All && string.IsNullOrWhiteSpace(pattern.Value))
        {
            throw new ArgumentException($"{name} selector value is required.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
