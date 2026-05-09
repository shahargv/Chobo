using Chobo.Contracts;
using ChoboServer.Data;

namespace ChoboServer.Application;

internal static class BackupRestoreMapping
{
    public static BackupDto ToDto(BackupEntity x) =>
        new(
            x.Id,
            x.TriggerType,
            x.Status,
            x.SourceClusterId,
            x.TargetId,
            x.PolicyId,
            x.ScheduleId,
            x.RequestedByUserId,
            x.RequestedByName,
            x.ManualRequestJson,
            x.CreatedAt,
            x.StartedAt,
            x.CompletedAt,
            x.Error,
            x.Tables.OrderBy(t => t.Database).ThenBy(t => t.Table).Select(ToDto).ToList());

    public static BackupTableDto ToDto(BackupTableEntity x) =>
        new(
            x.Id,
            x.BackupId,
            x.Database,
            x.Table,
            x.Engine,
            x.DataBackedUp,
            x.SchemaDefinitionId,
            x.S3Path,
            x.Status,
            x.ClickHouseOperationId,
            x.ClickHouseStatus,
            x.StartedAt,
            x.CompletedAt,
            x.Error);

    public static RestoreDto ToDto(RestoreEntity x) =>
        new(
            x.Id,
            x.BackupId,
            x.TargetClusterId,
            x.Status,
            x.Append,
            x.AllowSchemaMismatch,
            x.RequestedByUserId,
            x.RequestedByName,
            x.RequestJson,
            x.CreatedAt,
            x.StartedAt,
            x.CompletedAt,
            x.Error,
            x.Tables.OrderBy(t => t.TargetDatabase).ThenBy(t => t.TargetTable).Select(ToDto).ToList());

    public static RestoreTableDto ToDto(RestoreTableEntity x) =>
        new(
            x.Id,
            x.RestoreId,
            x.BackupTableId,
            x.SourceDatabase,
            x.SourceTable,
            x.TargetDatabase,
            x.TargetTable,
            x.Status,
            x.ClickHouseOperationId,
            x.ClickHouseStatus,
            x.Warning,
            x.StartedAt,
            x.CompletedAt,
            x.Error);
}
