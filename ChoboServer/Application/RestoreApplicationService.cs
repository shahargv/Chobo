using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Application;

public sealed class RestoreApplicationService(
    ChoboDbContext db,
    BackupRestoreQueues queues,
    AuditService audit,
    ActorContext actor,
    Serilog.ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Serilog.ILogger _logger = logger.ForContext<RestoreApplicationService>();

    public async Task<RestoreDto> InitiateAsync(InitiateRestoreRequest request, CancellationToken cancellationToken = default)
    {
        var backup = await db.Backups
            .Include(x => x.Tables)
            .FirstOrDefaultAsync(x => x.Id == request.BackupId, cancellationToken);
        if (backup is null)
        {
            throw new ArgumentException("Backup was not found.");
        }
        if (backup.Status != BackupRunStatus.Succeeded)
        {
            throw new ArgumentException("Only succeeded backups can be restored.");
        }
        if (!await db.ClickHouseClusters.AnyAsync(x => x.Id == request.TargetClusterId && !x.IsDeleted, cancellationToken))
        {
            throw new ArgumentException("Target cluster was not found.");
        }

        var selected = backup.Tables
            .Where(x => string.IsNullOrWhiteSpace(request.Database) || x.Database == request.Database)
            .Where(x => string.IsNullOrWhiteSpace(request.Table) || x.Table == request.Table)
            .ToList();
        if (selected.Count == 0)
        {
            throw new ArgumentException("No backup tables match the restore request.");
        }
        if (selected.Count > 1 && (!string.IsNullOrWhiteSpace(request.TargetDatabase) || !string.IsNullOrWhiteSpace(request.TargetTable)))
        {
            throw new ArgumentException("Target database/table overrides are supported only for a single table restore.");
        }

        var restore = new RestoreEntity
        {
            BackupId = request.BackupId,
            TargetClusterId = request.TargetClusterId,
            Append = request.Append,
            AllowSchemaMismatch = request.AllowSchemaMismatch,
            RequestJson = JsonSerializer.Serialize(request, JsonOptions),
            RequestedByUserId = actor.UserId,
            RequestedByName = actor.ActorName
        };
        foreach (var table in selected)
        {
            restore.Tables.Add(new RestoreTableEntity
            {
                BackupTableId = table.Id,
                SourceDatabase = table.Database,
                SourceTable = table.Table,
                TargetDatabase = request.TargetDatabase ?? table.Database,
                TargetTable = request.TargetTable ?? table.Table
            });
        }

        db.Restores.Add(restore);
        await db.SaveChangesAsync(cancellationToken);
        _logger.Information("Restore {RestoreId} created by {ActorName} for backup {BackupId} into cluster {TargetClusterId} with {TableCount} table(s).", restore.Id, actor.ActorName, restore.BackupId, restore.TargetClusterId, restore.Tables.Count);
        await audit.RecordAsync("created", "restore", restore.Id.ToString(), new { restore.BackupId, restore.TargetClusterId, tableCount = restore.Tables.Count });
        await queues.QueueRestoreAsync(restore.Id, cancellationToken);
        _logger.Information("Restore {RestoreId} queued.", restore.Id);
        await audit.RecordAsync("queued", "restore", restore.Id.ToString(), new { reason = "user" });
        return BackupRestoreMapping.ToDto(await LoadAsync(restore.Id, cancellationToken) ?? restore);
    }

    public async Task<IReadOnlyList<RestoreDto>> ListAsync(CancellationToken cancellationToken = default) =>
        (await db.Restores.Include(x => x.Tables).OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(cancellationToken))
        .Select(BackupRestoreMapping.ToDto)
        .ToList();

    public async Task<RestoreDto?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await LoadAsync(id, cancellationToken) is { } restore ? BackupRestoreMapping.ToDto(restore) : null;

    private Task<RestoreEntity?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        db.Restores.Include(x => x.Tables).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
