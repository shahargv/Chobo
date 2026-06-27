using System.Text.Json;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Repositories;
using ChoboServer.Services;

namespace ChoboServer.Application;

public sealed class ClusterApplicationService(
    IClusterRepository clusters,
    IUnitOfWork unitOfWork,
    ICredentialProtector protector,
    IClickHouseAdapter clickHouse,
    IAuditService audit,
    SystemDefaultBackupPolicyService systemDefaults)
{
    public async Task<IReadOnlyList<ClusterDto>> ListAsync() =>
        (await clusters.ListActiveAsync()).Select(ToDto).ToList();

    public async Task<ClickHouseClusterNamesDto?> ListClickHouseClusterNamesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var cluster = await clusters.FindActiveAsync(id);
        if (cluster is null)
        {
            return null;
        }

        return new ClickHouseClusterNamesDto(id, await clickHouse.GetClusterNamesAsync(cluster, cancellationToken));
    }
    public async Task<ClickHouseClusterTopologyDto?> GetTopologyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var cluster = await clusters.FindActiveAsync(id);
        if (cluster is null)
        {
            return null;
        }

        var topology = await clickHouse.GetTopologyAsync(cluster, cancellationToken);
        return new ClickHouseClusterTopologyDto(
            id,
            topology
                .OrderBy(x => x.ShardNumber)
                .ThenBy(x => x.ReplicaNumber)
                .ThenBy(x => x.Host, StringComparer.Ordinal)
                .ThenBy(x => x.Port)
                .Select(x => new ClickHouseClusterShardDto(x.ShardNumber, x.ShardName, x.ReplicaNumber, x.Host, x.Port, x.UseTls, x.ErrorsCount))
                .ToList());
    }

    public async Task<ClusterDto> AddAsync(UpsertClusterRequest request)
    {
        Validate(request);
        var userName = await protector.EncryptAsync(request.UserName);
        var password = await protector.EncryptAsync(request.Password);
        var cluster = new ClickHouseClusterEntity
        {
            Name = request.Name.Trim(),
            Mode = request.Mode,
            BackupRestoreMaxDop = NormalizeRequiredMaxDop(request.BackupRestoreMaxDop),
            NodeMaxDopDefault = NormalizeRequiredMaxDop(request.NodeMaxDopDefault),
            NodeMaxDopOverridesJson = SerializeNodeOverrides(request.NodeMaxDopOverrides),
            ShardMaxDopDefault = NormalizeRequiredMaxDop(request.ShardMaxDopDefault),
            ShardMaxDopOverridesJson = SerializeShardOverrides(request.ShardMaxDopOverrides),
            ClickHouseClusterName = NormalizeClusterName(request.ClickHouseClusterName),
            ClickHouseBackupSettingsJson = ClickHouseAdvancedSettings.Serialize(request.ClickHouseBackupSettings, ClickHouseAdvancedSettingsKind.Backup),
            ClickHouseRestoreSettingsJson = ClickHouseAdvancedSettings.Serialize(request.ClickHouseRestoreSettings, ClickHouseAdvancedSettingsKind.Restore),
            EncryptedUserName = userName?.Ciphertext,
            EncryptedUserNameKeyId = userName?.KeyId,
            EncryptedPassword = password?.Ciphertext,
            EncryptedPasswordKeyId = password?.KeyId,
            AccessNodes = request.AccessNodes.Select(ToEntity).ToList()
        };

        await clusters.AddAsync(cluster);
        await unitOfWork.SaveChangesAsync();

        var current = ToDto(cluster);
        await audit.RecordAsync("create", AuditEntityType.Cluster, cluster.Id.ToString(), AuditDetails.Change(null, current));
        await systemDefaults.EnsureForClusterAsync(cluster);
        return current;
    }

    public async Task<ClusterDto?> UpdateAsync(Guid id, UpsertClusterRequest request)
    {
        var cluster = await clusters.FindActiveAsync(id);
        if (cluster is null)
        {
            return null;
        }

        Validate(request);
        var previous = ToDto(cluster);
        cluster.Name = request.Name.Trim();
        cluster.Mode = request.Mode;
        cluster.BackupRestoreMaxDop = NormalizeRequiredMaxDop(request.BackupRestoreMaxDop);
        cluster.NodeMaxDopDefault = NormalizeRequiredMaxDop(request.NodeMaxDopDefault);
        cluster.NodeMaxDopOverridesJson = SerializeNodeOverrides(request.NodeMaxDopOverrides);
        cluster.ShardMaxDopDefault = NormalizeRequiredMaxDop(request.ShardMaxDopDefault);
        cluster.ShardMaxDopOverridesJson = SerializeShardOverrides(request.ShardMaxDopOverrides);
        cluster.ClickHouseClusterName = NormalizeClusterName(request.ClickHouseClusterName);
        if (request.ClickHouseBackupSettings is not null)
        {
            cluster.ClickHouseBackupSettingsJson = ClickHouseAdvancedSettings.Serialize(request.ClickHouseBackupSettings, ClickHouseAdvancedSettingsKind.Backup);
        }
        if (request.ClickHouseRestoreSettings is not null)
        {
            cluster.ClickHouseRestoreSettingsJson = ClickHouseAdvancedSettings.Serialize(request.ClickHouseRestoreSettings, ClickHouseAdvancedSettingsKind.Restore);
        }
        cluster.UpdatedAt = DateTimeOffset.UtcNow;
        if (request.UserName is not null)
        {
            var userName = await protector.EncryptAsync(request.UserName);
            cluster.EncryptedUserName = userName?.Ciphertext;
            cluster.EncryptedUserNameKeyId = userName?.KeyId;
        }
        if (request.Password is not null)
        {
            var password = await protector.EncryptAsync(request.Password);
            cluster.EncryptedPassword = password?.Ciphertext;
            cluster.EncryptedPasswordKeyId = password?.KeyId;
        }

        clusters.RemoveNodes(cluster.AccessNodes.ToList());
        var replacementNodes = request.AccessNodes
            .Select(n => new ClickHouseAccessNodeEntity { ClusterId = id, Host = n.Host, Port = n.Port, UseTls = n.UseTls })
            .ToList();
        await clusters.AddNodesAsync(replacementNodes);

        await unitOfWork.SaveChangesAsync();
        cluster.AccessNodes = replacementNodes;
        var current = ToDto(cluster);
        await audit.RecordAsync("update", AuditEntityType.Cluster, id.ToString(), AuditDetails.Change(previous, current));
        return current;
    }

    public async Task<ClusterDto?> UpdateCredentialsAsync(Guid id, UpdateClusterCredentialsRequest request)
    {
        var cluster = await clusters.FindActiveAsync(id);
        if (cluster is null)
        {
            return null;
        }

        var previous = new
        {
            hasUserName = !string.IsNullOrWhiteSpace(cluster.EncryptedUserName),
            hasPassword = !string.IsNullOrWhiteSpace(cluster.EncryptedPassword)
        };
        if (request.UserName is not null)
        {
            var userName = await protector.EncryptAsync(request.UserName);
            cluster.EncryptedUserName = userName?.Ciphertext;
            cluster.EncryptedUserNameKeyId = userName?.KeyId;
        }
        if (request.Password is not null)
        {
            var password = await protector.EncryptAsync(request.Password);
            cluster.EncryptedPassword = password?.Ciphertext;
            cluster.EncryptedPasswordKeyId = password?.KeyId;
        }

        cluster.UpdatedAt = DateTimeOffset.UtcNow;
        await unitOfWork.SaveChangesAsync();
        var current = ToDto(cluster);
        await audit.RecordAsync("update-credentials", AuditEntityType.Cluster, id.ToString(), new
        {
            previous,
            current = new
            {
                hasUserName = !string.IsNullOrWhiteSpace(cluster.EncryptedUserName),
                hasPassword = !string.IsNullOrWhiteSpace(cluster.EncryptedPassword)
            }
        });
        return current;
    }

    public async Task<bool> RemoveAsync(Guid id)
    {
        var cluster = await clusters.FindAsync(id);
        if (cluster is null)
        {
            return false;
        }

        var previous = ToDto(cluster);
        cluster.IsDeleted = true;
        cluster.DeletedAt = DateTimeOffset.UtcNow;
        await unitOfWork.SaveChangesAsync();

        await audit.RecordAsync("delete", AuditEntityType.Cluster, id.ToString(), AuditDetails.Deactivation(previous, ToDto(cluster)));
        return true;
    }

    public async Task<ClusterConnectionTestResult?> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var cluster = await clusters.FindActiveAsync(id);
        if (cluster is null)
        {
            return null;
        }

        try
        {
            await clickHouse.ExecuteAsync(cluster, "SELECT 1", cancellationToken);
            return new ClusterConnectionTestResult(cluster.Id, true, "ClickHouse connection succeeded.");
        }
        catch (Exception ex)
        {
            return new ClusterConnectionTestResult(cluster.Id, false, ex.Message);
        }
    }

    private static void Validate(UpsertClusterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Name is required.");
        }
        if (request.AccessNodes.Count == 0)
        {
            throw new ArgumentException("At least one access node is required.");
        }
        if (request.AccessNodes.Any(x => string.IsNullOrWhiteSpace(x.Host) || x.Port < 1))
        {
            throw new ArgumentException("Access nodes require host and valid port.");
        }
        if (request.BackupRestoreMaxDop <= 0)
        {
            throw new ArgumentException("BackupRestoreMaxDop is required and must be positive.");
        }
        if (request.NodeMaxDopDefault <= 0 || request.ShardMaxDopDefault <= 0)
        {
            throw new ArgumentException("NodeMaxDopDefault and ShardMaxDopDefault must be positive.");
        }
        if (request.NodeMaxDopOverrides?.Any(x => string.IsNullOrWhiteSpace(x.Host) || x.Port < 1 || x.MaxDop <= 0) == true)
        {
            throw new ArgumentException("Node MaxDop overrides require host, valid port, and positive MaxDop.");
        }
        if (request.ShardMaxDopOverrides?.Any(x => x.ShardNumber <= 0 || x.MaxDop <= 0) == true)
        {
            throw new ArgumentException("Shard MaxDop overrides require positive shard number and MaxDop.");
        }
        ClickHouseAdvancedSettings.Normalize(request.ClickHouseBackupSettings, ClickHouseAdvancedSettingsKind.Backup);
        ClickHouseAdvancedSettings.Normalize(request.ClickHouseRestoreSettings, ClickHouseAdvancedSettingsKind.Restore);
    }

    private static ClickHouseAccessNodeEntity ToEntity(UpsertAccessNodeRequest request) =>
        new() { Host = request.Host, Port = request.Port, UseTls = request.UseTls };

    private static ClusterDto ToDto(ClickHouseClusterEntity x) =>
        new(
            x.Id,
            x.Name,
            x.Mode,
            x.AccessNodes.Select(n => new AccessNodeDto(n.Id, n.Host, n.Port, n.UseTls)).ToList(),
            x.BackupRestoreMaxDop,
            x.NodeMaxDopDefault,
            DeserializeNodeOverrides(x.NodeMaxDopOverridesJson),
            x.ShardMaxDopDefault,
            DeserializeShardOverrides(x.ShardMaxDopOverridesJson),
            x.ClickHouseClusterName,
            ClickHouseAdvancedSettings.Deserialize(x.ClickHouseBackupSettingsJson, ClickHouseAdvancedSettingsKind.Backup),
            ClickHouseAdvancedSettings.Deserialize(x.ClickHouseRestoreSettingsJson, ClickHouseAdvancedSettingsKind.Restore),
            x.IsDeleted,
            x.CreatedAt,
            x.UpdatedAt);

    private static int NormalizeRequiredMaxDop(int maxDop) =>
        maxDop <= 0 ? throw new ArgumentException("MaxDop must be positive.") : maxDop;

    private static string SerializeNodeOverrides(IReadOnlyList<ClusterNodeMaxDopOverrideDto>? overrides) =>
        JsonSerializer.Serialize((overrides ?? []).OrderBy(x => x.Host, StringComparer.Ordinal).ThenBy(x => x.Port).ThenBy(x => x.UseTls).ToList());

    private static string SerializeShardOverrides(IReadOnlyList<ClusterShardMaxDopOverrideDto>? overrides) =>
        JsonSerializer.Serialize((overrides ?? []).OrderBy(x => x.ShardNumber).ThenBy(x => x.ShardName, StringComparer.Ordinal).ToList());

    private static IReadOnlyList<ClusterNodeMaxDopOverrideDto> DeserializeNodeOverrides(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<IReadOnlyList<ClusterNodeMaxDopOverrideDto>>(json) ?? [];

    private static IReadOnlyList<ClusterShardMaxDopOverrideDto> DeserializeShardOverrides(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<IReadOnlyList<ClusterShardMaxDopOverrideDto>>(json) ?? [];

    private static string? NormalizeClusterName(string? clusterName) =>
        string.IsNullOrWhiteSpace(clusterName) ? null : clusterName.Trim();
}
