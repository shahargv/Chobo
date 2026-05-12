using Chobo.Contracts;
using ChoboServer.Data;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Application;

public sealed class DashboardApplicationService(ChoboDbContext db)
{
    public async Task<DashboardDto> GetDashboardAsync(int nextHours = 6, CancellationToken cancellationToken = default)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var boundedHours = Math.Clamp(nextHours, 1, 168);

        var runningBackups = await db.Backups
            .Include(x => x.Policy)
            .Include(x => x.Schedule)
            .Include(x => x.Tables).ThenInclude(x => x.Shards)
            .Where(x => x.Status == BackupRunStatus.Running)
            .OrderBy(x => x.StartedAt ?? x.CreatedAt)
            .ToListAsync(cancellationToken);
        var runningBackupDtos = runningBackups
            .Select(x =>
            {
                var shards = x.Tables.SelectMany(t => t.Shards).ToList();
                return new DashboardRunningBackupDto(
                    x.Id,
                    x.Status,
                    x.TriggerType,
                    x.PolicyId,
                    x.Policy == null ? null : x.Policy.Name,
                    x.ScheduleId,
                    x.Schedule == null ? null : x.Schedule.Name,
                    x.CreatedAt,
                    x.StartedAt,
                    x.Tables.Count,
                    shards.Count,
                    shards.Count(s => s.Status == BackupTableStatus.Succeeded),
                    shards.Count(s => s.Status == BackupTableStatus.Failed),
                    shards.Count(s => s.Status == BackupTableStatus.Running));
            })
            .ToList();

        var schedules = await db.BackupSchedules
            .Include(x => x.Policy)
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var scheduleSummaries = new List<DashboardScheduleDto>();
        foreach (var schedule in schedules)
        {
            var lastRun = await db.Backups
                .Where(x => x.ScheduleId == schedule.Id)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.CreatedAt, x.Status })
                .FirstOrDefaultAsync(cancellationToken);

            var lastSuccessfulRunCompletedAt = await db.Backups
                .Where(x => x.ScheduleId == schedule.Id && x.Status == BackupRunStatus.Succeeded && x.CompletedAt != null)
                .OrderByDescending(x => x.CompletedAt)
                .Select(x => x.CompletedAt)
                .FirstOrDefaultAsync(cancellationToken);

            scheduleSummaries.Add(new DashboardScheduleDto(
                schedule.Id,
                schedule.Name,
                schedule.PolicyId,
                schedule.Policy?.Name,
                schedule.BackupType,
                schedule.CronExpression,
                schedule.TimeZoneId,
                schedule.IsEnabled,
                schedule.MissedRunGracePeriod,
                lastRun?.CreatedAt,
                lastRun?.Status,
                lastSuccessfulRunCompletedAt));
        }

        var futureSchedules = ProjectFutureSchedules(schedules, generatedAt, generatedAt.AddHours(boundedHours));

        return new DashboardDto(generatedAt, boundedHours, runningBackupDtos, scheduleSummaries, futureSchedules);
    }

    public async Task<IReadOnlyDictionary<string, double?>> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var policies = await db.BackupPolicies
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var metrics = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var policy in policies)
        {
            var lastSuccessfulBackupEndedAt = await db.Backups
                .Where(x => x.PolicyId == policy.Id && x.Status == BackupRunStatus.Succeeded && x.CompletedAt != null)
                .OrderByDescending(x => x.CompletedAt)
                .Select(x => x.CompletedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var secondsSinceLastSuccessfulBackupEnded = lastSuccessfulBackupEndedAt is null
                ? (double?)null
                : Math.Max(0, (generatedAt - lastSuccessfulBackupEndedAt.Value).TotalSeconds);

            metrics[$"Policies.TimeSecondsSinceLastPolicyBackup.{policy.Name}"] = secondsSinceLastSuccessfulBackupEnded;
            metrics[$"Policies.PartialBackups.{policy.Name}"] = await db.Backups.CountAsync(x => x.PolicyId == policy.Id && x.Status == BackupRunStatus.PartiallySucceeded, cancellationToken);
            metrics[$"Policies.FailedBackups.{policy.Name}"] = await db.Backups.CountAsync(x => x.PolicyId == policy.Id && x.Status == BackupRunStatus.Failed, cancellationToken);
        }

        return metrics;
    }

    private static IReadOnlyList<DashboardFutureScheduleDto> ProjectFutureSchedules(
        IReadOnlyList<BackupScheduleEntity> schedules,
        DateTimeOffset fromUtc,
        DateTimeOffset untilUtc)
    {
        var results = new List<DashboardFutureScheduleDto>();
        foreach (var schedule in schedules.Where(x => x.IsEnabled && x.Policy is not null))
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(schedule.TimeZoneId, out var timeZone))
            {
                continue;
            }

            IReadOnlyList<DateTimeOffset> occurrences;
            try
            {
                occurrences = QuartzCronProjection.GetOccurrences(schedule.CronExpression, timeZone, fromUtc, untilUtc);
            }
            catch (FormatException)
            {
                continue;
            }

            foreach (var occurrence in occurrences)
            {
                results.Add(new DashboardFutureScheduleDto(
                    schedule.Id,
                    schedule.Name,
                    schedule.PolicyId,
                    schedule.Policy?.Name,
                    schedule.BackupType,
                    occurrence,
                    schedule.TimeZoneId));
            }
        }

        return results
            .OrderBy(x => x.PlannedRunAt)
            .ThenBy(x => x.ScheduleName)
            .Take(500)
            .ToList();
    }
}

internal static class QuartzCronProjection
{
    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JAN"] = 1, ["FEB"] = 2, ["MAR"] = 3, ["APR"] = 4, ["MAY"] = 5, ["JUN"] = 6,
        ["JUL"] = 7, ["AUG"] = 8, ["SEP"] = 9, ["OCT"] = 10, ["NOV"] = 11, ["DEC"] = 12
    };

    private static readonly Dictionary<string, int> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SUN"] = 1, ["MON"] = 2, ["TUE"] = 3, ["WED"] = 4, ["THU"] = 5, ["FRI"] = 6, ["SAT"] = 7
    };

    public static IReadOnlyList<DateTimeOffset> GetOccurrences(string expression, TimeZoneInfo timeZone, DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        var matcher = CreateMatcher(expression);
        return GetOccurrences(matcher, timeZone, fromUtc, untilUtc, maxOccurrences: 500).ToList();
    }

    public static DateTimeOffset? GetLatestOccurrence(string expression, TimeZoneInfo timeZone, DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        var matcher = CreateMatcher(expression);
        DateTimeOffset? latest = null;
        foreach (var occurrence in GetOccurrences(matcher, timeZone, fromUtc, untilUtc, maxOccurrences: null))
        {
            latest = occurrence;
        }

        return latest;
    }

    private static CronMatcher CreateMatcher(string expression)
    {
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6)
        {
            throw new FormatException("Quartz cron expression must include at least six fields.");
        }

        return new CronMatcher(
            CronField.Parse(parts[0], 0, 59),
            CronField.Parse(parts[1], 0, 59),
            CronField.Parse(parts[2], 0, 23),
            CronField.Parse(parts[3], 1, 31),
            CronField.Parse(parts[4], 1, 12, MonthNames),
            CronField.Parse(parts[5], 0, 7, DayNames),
            parts.Length > 6 ? CronField.Parse(parts[6], 1970, 2199) : CronField.Any(1970, 2199));
    }

    private static IEnumerable<DateTimeOffset> GetOccurrences(CronMatcher matcher, TimeZoneInfo timeZone, DateTimeOffset fromUtc, DateTimeOffset untilUtc, int? maxOccurrences)
    {
        var cursorUtc = fromUtc.ToUniversalTime().AddSeconds(1);
        cursorUtc = new DateTimeOffset(cursorUtc.Year, cursorUtc.Month, cursorUtc.Day, cursorUtc.Hour, cursorUtc.Minute, cursorUtc.Second, TimeSpan.Zero);
        var endUtc = untilUtc.ToUniversalTime();
        var occurrenceCount = 0;

        while (cursorUtc <= endUtc && (maxOccurrences is null || occurrenceCount < maxOccurrences.Value))
        {
            var local = TimeZoneInfo.ConvertTime(cursorUtc, timeZone);
            if (matcher.Matches(local))
            {
                occurrenceCount++;
                yield return cursorUtc;
            }

            cursorUtc = cursorUtc.AddSeconds(1);
        }
    }

    private static int ToQuartzDayOfWeek(DayOfWeek dayOfWeek) =>
        dayOfWeek == DayOfWeek.Sunday ? 1 : (int)dayOfWeek + 1;

    private sealed record CronMatcher(
        CronField Seconds,
        CronField Minutes,
        CronField Hours,
        CronField Days,
        CronField Months,
        CronField DaysOfWeek,
        CronField Years)
    {
        public bool Matches(DateTimeOffset local) =>
            Seconds.Matches(local.Second) &&
            Minutes.Matches(local.Minute) &&
            Hours.Matches(local.Hour) &&
            Days.Matches(local.Day) &&
            Months.Matches(local.Month) &&
            DaysOfWeek.Matches(ToQuartzDayOfWeek(local.DayOfWeek)) &&
            Years.Matches(local.Year);
    }

    private sealed class CronField
    {
        private readonly HashSet<int> _values;

        private CronField(HashSet<int> values) => _values = values;

        public static CronField Any(int min, int max) => new(Enumerable.Range(min, max - min + 1).ToHashSet());

        public static CronField Parse(string value, int min, int max, IReadOnlyDictionary<string, int>? aliases = null)
        {
            if (value is "*" or "?")
            {
                return Any(min, max);
            }

            var values = new HashSet<int>();
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddPart(values, part, min, max, aliases);
            }

            return new CronField(values);
        }

        public bool Matches(int value) => _values.Contains(value) || (value == 1 && _values.Contains(0));

        private static void AddPart(HashSet<int> values, string part, int min, int max, IReadOnlyDictionary<string, int>? aliases)
        {
            var step = 1;
            var rangePart = part;
            var slashIndex = part.IndexOf('/', StringComparison.Ordinal);
            if (slashIndex >= 0)
            {
                rangePart = part[..slashIndex];
                step = int.Parse(part[(slashIndex + 1)..]);
                if (step <= 0)
                {
                    throw new FormatException("Cron step must be greater than zero.");
                }
            }

            var range = ParseRange(rangePart, min, max, aliases);
            if (slashIndex >= 0 && range.Start == range.End && rangePart is not "*" and not "?")
            {
                range = (range.Start, max);
            }

            for (var i = range.Start; i <= range.End; i += step)
            {
                if (i >= min && i <= max)
                {
                    values.Add(i);
                }
            }
        }

        private static (int Start, int End) ParseRange(string value, int min, int max, IReadOnlyDictionary<string, int>? aliases)
        {
            if (string.IsNullOrWhiteSpace(value) || value is "*" or "?")
            {
                return (min, max);
            }

            var dashIndex = value.IndexOf('-', StringComparison.Ordinal);
            if (dashIndex < 0)
            {
                var single = ParseNumber(value, aliases);
                return (single, single);
            }

            return (ParseNumber(value[..dashIndex], aliases), ParseNumber(value[(dashIndex + 1)..], aliases));
        }

        private static int ParseNumber(string value, IReadOnlyDictionary<string, int>? aliases) =>
            aliases is not null && aliases.TryGetValue(value, out var aliasValue)
                ? aliasValue
                : int.Parse(value);
    }
}
