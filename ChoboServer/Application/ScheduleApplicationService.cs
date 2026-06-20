using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Repositories;
using ChoboServer.Services;

namespace ChoboServer.Application;

public sealed class ScheduleApplicationService(
    IScheduleRepository schedules,
    IUnitOfWork unitOfWork,
    IAuditService audit)
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
            MissedRunGracePeriod = request.MissedRunGracePeriod,
            Description = request.Description
        };

        await schedules.AddAsync(schedule);
        await unitOfWork.SaveChangesAsync();

        var current = ToDto(schedule);
        await audit.RecordAsync("create", AuditEntityType.BackupSchedule, schedule.Id.ToString(), AuditDetails.Change(null, current));
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
        schedule.MissedRunGracePeriod = request.MissedRunGracePeriod;
        schedule.Description = request.Description;
        schedule.UpdatedAt = DateTimeOffset.UtcNow;
        await unitOfWork.SaveChangesAsync();

        var current = ToDto(schedule);
        await audit.RecordAsync("update", AuditEntityType.BackupSchedule, id.ToString(), AuditDetails.Change(previous, current));
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

        await audit.RecordAsync(enabled ? "enable" : "disable", AuditEntityType.BackupSchedule, id.ToString(), AuditDetails.Change(previous, ToDto(schedule)));
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

        await audit.RecordAsync("delete", AuditEntityType.BackupSchedule, id.ToString(), AuditDetails.Deactivation(previous, ToDto(schedule)));
        return true;
    }

    public ValidateScheduleCronResponse ValidateCron(ValidateScheduleCronRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.CronExpression))
            {
                return new(false, "CronExpression is required.", []);
            }

            if (!TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZoneId, out var timeZone))
            {
                return new(false, "TimeZoneId is invalid.", []);
            }

            QuartzCronProjection.ValidateExpression(request.CronExpression);
            var now = DateTimeOffset.UtcNow;
            var nextRuns = QuartzCronProjection.GetOccurrences(request.CronExpression, timeZone, now, now.AddDays(30), maxOccurrences: 5);
            return new(true, null, nextRuns);
        }
        catch (FormatException ex)
        {
            return new(false, ex.Message, []);
        }
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
            QuartzCronProjection.ValidateExpression(request.CronExpression);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"CronExpression is invalid: {ex.Message}");
        }
        if (request.MissedRunGracePeriod is not null && request.MissedRunGracePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentException("MissedRunGracePeriod must be greater than zero when specified.");
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
            x.MissedRunGracePeriod,
            x.Description,
            x.IsDeleted,
            x.CreatedAt,
            x.UpdatedAt);
}
