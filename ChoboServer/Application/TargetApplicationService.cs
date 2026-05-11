using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Repositories;
using ChoboServer.Services;

namespace ChoboServer.Application;

public sealed class TargetApplicationService(
    ITargetRepository targets,
    IUnitOfWork unitOfWork,
    CredentialProtector protector,
    AuditService audit)
{
    public async Task<IReadOnlyList<BackupTargetDto>> ListAsync() =>
        (await targets.ListActiveAsync()).Select(ToDto).ToList();

    public async Task<BackupTargetDto> AddS3Async(UpsertS3TargetRequest request)
    {
        Validate(request);
        var target = new BackupTargetEntity
        {
            Name = request.Name.Trim(),
            Endpoint = request.Endpoint,
            Region = request.Region,
            Bucket = request.Bucket,
            PathPrefix = request.PathPrefix,
            ForcePathStyle = request.ForcePathStyle,
            EncryptedAccessKey = protector.Protect(request.AccessKey),
            EncryptedSecretKey = protector.Protect(request.SecretKey)
        };

        await targets.AddAsync(target);
        await unitOfWork.SaveChangesAsync();

        var current = ToDto(target);
        await audit.RecordAsync("create", AuditEntityType.BackupTarget, target.Id.ToString(), AuditDetails.Change(null, current));
        return current;
    }

    public async Task<BackupTargetDto?> UpdateS3Async(Guid id, UpsertS3TargetRequest request)
    {
        var target = await targets.FindActiveAsync(id);
        if (target is null)
        {
            return null;
        }

        Validate(request);
        var previous = ToDto(target);
        target.Name = request.Name.Trim();
        target.Endpoint = request.Endpoint;
        target.Region = request.Region;
        target.Bucket = request.Bucket;
        target.PathPrefix = request.PathPrefix;
        target.ForcePathStyle = request.ForcePathStyle;
        target.UpdatedAt = DateTimeOffset.UtcNow;
        if (request.AccessKey is not null)
        {
            target.EncryptedAccessKey = protector.Protect(request.AccessKey);
        }
        if (request.SecretKey is not null)
        {
            target.EncryptedSecretKey = protector.Protect(request.SecretKey);
        }

        await unitOfWork.SaveChangesAsync();
        var current = ToDto(target);
        await audit.RecordAsync("update", AuditEntityType.BackupTarget, id.ToString(), AuditDetails.Change(previous, current));
        return current;
    }

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

    private static void Validate(UpsertS3TargetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Name is required.");
        }
        if (string.IsNullOrWhiteSpace(request.Endpoint))
        {
            throw new ArgumentException("Endpoint is required.");
        }
        if (string.IsNullOrWhiteSpace(request.Bucket))
        {
            throw new ArgumentException("Bucket is required.");
        }
    }

    private static BackupTargetDto ToDto(BackupTargetEntity x) =>
        new(
            x.Id,
            x.Name,
            x.Type,
            new S3TargetSettingsDto(x.Endpoint, x.Region, x.Bucket, x.PathPrefix, x.ForcePathStyle),
            x.IsDeleted,
            x.CreatedAt,
            x.UpdatedAt);
}
