using System.Text;
using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class DashboardCommands : CliSubject
{
    public DashboardCommands()
    {
        Verb("show", "Show backup dashboard.", ShowAsync);
    }

    public override string Name => "dashboard";
    public override string Description => "Show backup dashboard.";

    private static async Task<object?> ShowAsync(CommandContext context)
    {
        using var client = await context.CreateClientAsync();
        var nextHours = context.Command.Options.Int("--next-hours", 6);
        var dashboard = await client.GetAsync<DashboardDto>($"dashboard?nextHours={nextHours}");
        return dashboard is null ? null : Format(dashboard);
    }

    private static string Format(DashboardDto dashboard)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Chobo dashboard at {FormatTime(dashboard.GeneratedAt)}");
        builder.AppendLine();

        builder.AppendLine("Running backups");
        if (dashboard.RunningBackups.Count == 0)
        {
            builder.AppendLine("  none");
        }
        else
        {
            foreach (var backup in dashboard.RunningBackups)
            {
                var trigger = backup.TriggerType == BackupTriggerType.Scheduled
                    ? backup.ScheduleName ?? backup.ScheduleId?.ToString() ?? "scheduled"
                    : "manual";
                builder.AppendLine($"  {backup.BackupId}  {backup.Status,-32}  pinned={backup.IsPinned}  policy={DisplayName(backup.PolicyName, backup.PolicyId)}  trigger={trigger}  started={FormatOptionalTime(backup.StartedAt)}  deleteRequested={FormatOptionalTime(backup.DeletionRequestedAt)}  tables={backup.TableCount}  table-shards={backup.SucceededShardCount}/{backup.ShardCount} ok failed={backup.FailedShardCount} running={backup.RunningShardCount}{FormatFailure(backup.FailureReason)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Schedules");
        if (dashboard.Schedules.Count == 0)
        {
            builder.AppendLine("  none");
        }
        else
        {
            foreach (var schedule in dashboard.Schedules)
            {
                builder.AppendLine($"  {schedule.ScheduleName}  enabled={schedule.IsEnabled}  policy={DisplayName(schedule.PolicyName, schedule.PolicyId)}  last={FormatOptionalTime(schedule.LastRunAt)}  status={schedule.LastRunStatus?.ToString() ?? "never"}  pinned={schedule.LastRunIsPinned}  deleteRequested={FormatOptionalTime(schedule.LastRunDeletionRequestedAt)}{FormatFailure(schedule.LastRunFailureReason)}  lastSuccess={FormatOptionalTime(schedule.LastSuccessfulRunCompletedAt)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Upcoming backups, next {dashboard.FutureWindowHours} hour(s)");
        if (dashboard.FutureSchedules.Count == 0)
        {
            builder.AppendLine("  none");
        }
        else
        {
            foreach (var planned in dashboard.FutureSchedules)
            {
                builder.AppendLine($"  {FormatTime(planned.PlannedRunAt)}  schedule={DisplayNameAndId(planned.ScheduleName, planned.ScheduleId)}  policy={DisplayNameAndId(planned.PolicyName, planned.PolicyId)}  type={planned.BackupType}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string DisplayName(string? name, Guid? id) =>
        !string.IsNullOrWhiteSpace(name) ? name : id?.ToString() ?? "none";

    private static string DisplayNameAndId(string? name, Guid id) =>
        !string.IsNullOrWhiteSpace(name) ? $"{name} ({id})" : id.ToString();

    private static string FormatOptionalTime(DateTimeOffset? value) =>
        value is null ? "never" : FormatTime(value.Value);

    private static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    private static string FormatFailure(string? failureReason) =>
        string.IsNullOrWhiteSpace(failureReason) ? "" : $"  failure={failureReason}";
}

