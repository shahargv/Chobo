using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class ScheduleCommands : CliSubject
{
    public ScheduleCommands()
    {
        Verb("list", "List backup schedules.", ListAsync);
        Verb("add", "Add a backup schedule.", AddAsync);
        Verb("update", "Update a backup schedule.", UpdateAsync);
        Verb("remove", "Soft-delete a backup schedule.", RemoveAsync);
        Verb("enable", "Enable a backup schedule.", EnableAsync);
        Verb("disable", "Disable a backup schedule.", DisableAsync);
    }

    public override string Name => "schedules";
    public override string Description => "Backup schedule configuration.";

    private static Task<object?> ListAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("schedules"));

    private static Task<object?> AddAsync(CommandContext context)
    {
        var request = Request(context.Command.Options);
        return CommandHelpers.WithClient(context, client => client.PostAsync("schedules", request));
    }

    private static Task<object?> UpdateAsync(CommandContext context)
    {
        var required = context.Command.Options.Require("--id");
        var request = Request(context.Command.Options);
        return CommandHelpers.WithClient(context, client => client.PutAsync($"schedules/{required["--id"]}", request));
    }

    private static Task<object?> RemoveAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.DeleteAsync($"schedules/{context.Command.Options.Required("--id")}"));

    private static Task<object?> EnableAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync($"schedules/{context.Command.Options.Required("--id")}/enable", new { }));

    private static Task<object?> DisableAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync($"schedules/{context.Command.Options.Required("--id")}/disable", new { }));

    private static UpsertScheduleRequest Request(OptionBag options)
    {
        var required = options.Require("--name", "--policy-id", "--cron");
        return new UpsertScheduleRequest(
            required["--name"],
            Guid.Parse(required["--policy-id"]),
            options.Enum("--backup-type", BackupType.Full),
            required["--cron"],
            options.Optional("--timezone") ?? "UTC",
            !options.Has("--disabled"),
            options.Optional("--missed-run-grace-period") is { } gracePeriod ? TimeSpan.Parse(gracePeriod) : null,
            options.Optional("--description"));
    }
}
