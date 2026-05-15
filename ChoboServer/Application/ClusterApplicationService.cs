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
    IAuditService audit)
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

    public async Task<ClusterDto> AddAsync(UpsertClusterRequest request)
    {
        Validate(request);
        var userName = await protector.EncryptAsync(request.UserName);
        var password = await protector.EncryptAsync(request.Password);
        var cluster = new ClickHouseClusterEntity
        {
            Name = request.Name.Trim(),
            Mode = request.Mode,
            BackupRestoreMaxDop = NormalizeMaxDop(request.BackupRestoreMaxDop),
            ClickHouseClusterName = NormalizeClusterName(request.ClickHouseClusterName),
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
        cluster.BackupRestoreMaxDop = NormalizeMaxDop(request.BackupRestoreMaxDop);
        cluster.ClickHouseClusterName = NormalizeClusterName(request.ClickHouseClusterName);
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
            x.ClickHouseClusterName,
            x.IsDeleted,
            x.CreatedAt,
            x.UpdatedAt);

    private static int? NormalizeMaxDop(int? maxDop) =>
        maxDop is null or <= 0 ? null : maxDop;

    private static string? NormalizeClusterName(string? clusterName) =>
        string.IsNullOrWhiteSpace(clusterName) ? null : clusterName.Trim();
}
