using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Repositories;
using ChoboServer.Services;

namespace ChoboServer.Application;

public sealed class ScheduleApplicationService(
    IScheduleRepository schedules,
    IUnitOfWork unitOfWork,
    AuditService audit)
{
    public async Task<IReadOnlyList<BackupScheduleDto>> ListAsync() =>
        (await schedules.ListActiveAsync()).Select(ToDto).ToList();

    public async Task<BackupScheduleDto> AddAsync(UpsertScheduleRequest request)
    {
        await Validate(request);
        var schedule = new BackupScheduleEntity
        {
            Name = request.Name.Trim(),
            PolicyId = request.PolicyId,
            BackupType = request.BackupType,
            CronExpression = request.CronExpression,
            TimeZoneId = request.TimeZoneId,
            IsEnabled = request.IsEnabled,
            Description = request.Description
        };

        await schedules.AddAsync(schedule);
        await unitOfWork.SaveChangesAsync();

        var current = ToDto(schedule);
        await audit.RecordAsync("create", "backup-schedule", schedule.Id.ToString(), AuditDetails.Change(null, current));
        return current;
    }

    public async Task<BackupScheduleDto?> UpdateAsync(Guid id, UpsertScheduleRequest request)
    {
        var schedule = await schedules.FindActiveAsync(id);
        if (schedule is null)
        {
            return null;
        }

        await Validate(request);
        var previous = ToDto(schedule);
        schedule.Name = request.Name.Trim();
        schedule.PolicyId = request.PolicyId;
        schedule.BackupType = request.BackupType;
        schedule.CronExpression = request.CronExpression;
        schedule.TimeZoneId = request.TimeZoneId;
        schedule.IsEnabled = request.IsEnabled;
        schedule.Description = request.Description;
        schedule.UpdatedAt = DateTimeOffset.UtcNow;
        await unitOfWork.SaveChangesAsync();

        var current = ToDto(schedule);
        await audit.RecordAsync("update", "backup-schedule", id.ToString(), AuditDetails.Change(previous, current));
        return current;
    }

    public async Task<bool> SetEnabledAsync(Guid id, bool enabled)
    {
        var schedule = await schedules.FindActiveAsync(id);
        if (schedule is null)
        {
            return false;
        }

        var previous = ToDto(schedule);
        schedule.IsEnabled = enabled;
        schedule.UpdatedAt = DateTimeOffset.UtcNow;
        await unitOfWork.SaveChangesAsync();

        await audit.RecordAsync(enabled ? "enable" : "disable", "backup-schedule", id.ToString(), AuditDetails.Change(previous, ToDto(schedule)));
        return true;
    }

    public async Task<bool> RemoveAsync(Guid id)
    {
        var schedule = await schedules.FindAsync(id);
        if (schedule is null)
        {
            return false;
        }

        var previous = ToDto(schedule);
        schedule.IsDeleted = true;
        schedule.DeletedAt = DateTimeOffset.UtcNow;
        await unitOfWork.SaveChangesAsync();

        await audit.RecordAsync("delete", "backup-schedule", id.ToString(), AuditDetails.Deactivation(previous, ToDto(schedule)));
        return true;
    }

    private async Task Validate(UpsertScheduleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Name is required.");
        }
        if (string.IsNullOrWhiteSpace(request.CronExpression))
        {
            throw new ArgumentException("CronExpression is required.");
        }
        if (request.CronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 6)
        {
            throw new ArgumentException("Quartz cron expressions must include at least 6 fields.");
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        }
        catch
        {
            throw new ArgumentException("TimeZoneId is invalid.");
        }

        if (!await schedules.PolicyExistsAsync(request.PolicyId))
        {
            throw new ArgumentException("PolicyId does not reference an active policy.");
        }
    }

    private static BackupScheduleDto ToDto(BackupScheduleEntity x) =>
        new(
            x.Id,
            x.Name,
            x.PolicyId,
            x.BackupType,
            x.CronExpression,
            x.TimeZoneId,
            x.IsEnabled,
            x.Description,
            x.IsDeleted,
            x.CreatedAt,
            x.UpdatedAt);
}
