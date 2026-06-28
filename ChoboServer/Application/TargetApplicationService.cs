using System.Text.Json;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Repositories;
using ChoboServer.Services;

namespace ChoboServer.Application;

public sealed class TargetApplicationService(
    ITargetRepository targets,
    IUnitOfWork unitOfWork,
    IBackupStorageProviderRegistry storageProviders,
    IAuditService audit)
{
    public async Task<IReadOnlyList<BackupTargetDto>> ListAsync(bool includeDeleted = false) =>
        (await targets.ListAsync(includeDeleted)).Select(ToDto).ToList();

    public async Task<BackupTargetDto> AddAsync(UpsertBackupTargetRequest request, CancellationToken cancellationToken = default)
    {
        var provider = storageProviders.Get(request.Type);
        var target = new BackupTargetEntity
        {
            Name = NormalizeName(request.Name),
            Type = provider.Type
        };
        await provider.ConfigureNewTargetAsync(target, request, cancellationToken);

        await targets.AddAsync(target);
        await unitOfWork.SaveChangesAsync();

        var current = ToDto(target);
        await audit.RecordAsync("create", AuditEntityType.BackupTarget, target.Id.ToString(), AuditDetails.Change(null, current));
        return current;
    }

    public async Task<BackupTargetDto?> UpdateAsync(Guid id, UpsertBackupTargetRequest request, CancellationToken cancellationToken = default)
    {
        var target = await targets.FindActiveAsync(id);
        if (target is null)
        {
            return null;
        }

        if (!string.Equals(target.Type, request.Type, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Backup target storage type cannot be changed.");
        }

        var provider = storageProviders.Get(target);
        var previous = ToDto(target);
        target.Name = NormalizeName(request.Name);
        target.UpdatedAt = DateTimeOffset.UtcNow;
        await provider.ConfigureExistingTargetAsync(target, request, cancellationToken);

        await unitOfWork.SaveChangesAsync();
        var current = ToDto(target);
        await audit.RecordAsync("update", AuditEntityType.BackupTarget, id.ToString(), AuditDetails.Change(previous, current));
        return current;
    }

    public Task<BackupTargetDto> AddS3Async(UpsertS3TargetRequest request, CancellationToken cancellationToken = default) =>
        AddAsync(ToGenericS3Request(request), cancellationToken);

    public Task<BackupTargetDto?> UpdateS3Async(Guid id, UpsertS3TargetRequest request, bool updateSecrets = true, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, ToGenericS3Request(request, updateSecrets), cancellationToken);
    public async Task<bool> RemoveAsync(Guid id)
    {
        var target = await targets.FindAsync(id);
        if (target is null)
        {
            return false;
        }

        var previous = ToDto(target);
        target.IsDeleted = true;
        target.DeletedAt = DateTimeOffset.UtcNow;
        await unitOfWork.SaveChangesAsync();

        await audit.RecordAsync("delete", AuditEntityType.BackupTarget, id.ToString(), AuditDetails.Deactivation(previous, ToDto(target)));
        return true;
    }

    public async Task<StorageConnectionTestResult?> TestConnectionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var target = await targets.FindActiveAsync(id);
        if (target is null)
        {
            return null;
        }

        return await storageProviders.Get(target).TestConnectionAsync(target, cancellationToken);
    }

    private BackupTargetDto ToDto(BackupTargetEntity target) => storageProviders.Get(target).ToDto(target);

    private static UpsertBackupTargetRequest ToGenericS3Request(UpsertS3TargetRequest request, bool updateSecrets = true) =>
        new(
            request.Name,
            StorageProviderTypes.S3,
            ToDictionary(new S3TargetSettingsDto(request.Endpoint, request.Region, request.Bucket, request.PathPrefix, request.ForcePathStyle)),
            ToDictionary(new S3TargetSecretsDto(request.AccessKey, request.SecretKey)),
            updateSecrets);

    private static IReadOnlyDictionary<string, JsonElement> ToDictionary<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return document.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
    }
    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.");
        }

        return name.Trim();
    }
}
