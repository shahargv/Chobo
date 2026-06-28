using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class TargetCommands : CliSubject
{
    public TargetCommands()
    {
        Verb("list", "List backup targets.", ListAsync);
        Verb("add-s3", "Add an S3 backup target.", AddS3Async);
        Verb("update-s3", "Update an S3 backup target.", UpdateS3Async);
        Verb("remove", "Soft-delete a backup target.", RemoveAsync);
        Verb("test-connection", "Test a backup target connection.", TestConnectionAsync);
    }

    public override string Name => "targets";
    public override string Description => "Backup target configuration.";

    private static Task<object?> ListAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("targets" + IncludeDeletedQuery(context)));

    private static Task<object?> AddS3Async(CommandContext context)
    {
        var request = S3Request(context.Command.Options);
        return CommandHelpers.WithClient(context, client => client.PostAsync("targets/s3", request));
    }

    private static Task<object?> UpdateS3Async(CommandContext context)
    {
        var required = context.Command.Options.Require("--id");
        var request = S3Request(context.Command.Options);
        return CommandHelpers.WithClient(context, client => client.PutAsync($"targets/{required["--id"]}/s3", request));
    }

    private static Task<object?> RemoveAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.DeleteAsync($"targets/{context.Command.Options.Required("--id")}"));

    private static Task<object?> TestConnectionAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync($"targets/{context.Command.Options.Required("--id")}/test-connection", new { }));

    private static UpsertS3TargetRequest S3Request(OptionBag options)
    {
        var required = options.Require("--name", "--endpoint", "--bucket");
        return new UpsertS3TargetRequest(
            required["--name"],
            required["--endpoint"],
            options.Optional("--region") ?? "us-east-1",
            required["--bucket"],
            options.Optional("--path-prefix"),
            options.Has("--force-path-style"),
            options.Optional("--access-key"),
            options.Optional("--secret-key"));
    }
    private static string IncludeDeletedQuery(CommandContext context) =>
        context.Command.Options.Has("--include-deleted") ? "?includeDeleted=true" : "";
}
