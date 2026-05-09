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
    }

    public override string Name => "targets";
    public override string Description => "Backup target configuration.";

    private static Task<object?> ListAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("targets"));

    private static Task<object?> AddS3Async(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync("targets/s3", S3Request(context.Command.Options)));

    private static Task<object?> UpdateS3Async(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PutAsync($"targets/{context.Command.Options.Required("--id")}/s3", S3Request(context.Command.Options)));

    private static Task<object?> RemoveAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.DeleteAsync($"targets/{context.Command.Options.Required("--id")}"));

    private static UpsertS3TargetRequest S3Request(OptionBag options) =>
        new(
            options.Required("--name"),
            options.Required("--endpoint"),
            options.Optional("--region") ?? "us-east-1",
            options.Required("--bucket"),
            options.Optional("--path-prefix"),
            options.Has("--force-path-style"),
            options.Optional("--access-key"),
            options.Optional("--secret-key"));
}

