using Chobo.Contracts;
using ChoboCli.Cli;
using System.Text.Json;

namespace ChoboCli.Commands;

public sealed class RestoreCommand : CliSubject
{
    public RestoreCommand()
    {
        Verb("initiate", "Start a restore.", InitiateAsync);
        Verb("plan", "Preview an entity-perspective restore plan.", PlanAsync);
        Verb("initiate-from-plan", "Start a restore from a restore plan JSON file.", InitiateFromPlanAsync);
        Verb("initiate-from-policy", "Start a restore from the latest policy/entity plan defaults.", InitiateFromPolicyAsync);
    }

    public override string Name => "restore";
    public override string Description => "Start restore operations.";

    private static async Task<object?> InitiateAsync(CommandContext context)
    {
        var required = context.Command.Options.Require("--backup-id", "--target-cluster-id");
        var tableMappings = ParseTableMappings(context.Command.Options);
        var sourceShards = ParseShards(context.Command.Options.Optional("--source-shards"));
        var targetShards = ParseShards(context.Command.Options.Optional("--target-shards"));
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? settings = null;
        if (HasClickHouseSettingsOptions(context.Command.Options, "restore"))
        {
            using var previewClient = await context.CreateClientAsync();
            var preview = await previewClient.PostAsync<ClickHouseSettingsPreviewDto>("restores/settings-preview", new RestoreSettingsPreviewRequest(Guid.Parse(required["--backup-id"]), Guid.Parse(required["--target-cluster-id"])))
                ?? new ClickHouseSettingsPreviewDto(new Dictionary<string, System.Text.Json.JsonElement>(), []);
            settings = CommandHelpers.ClickHouseSettingsFromOptions(context.Command.Options, "restore", preview.Settings);
        }
        var request = new InitiateRestoreRequest(
            Guid.Parse(required["--backup-id"]),
            Guid.Parse(required["--target-cluster-id"]),
            context.Command.Options.Optional("--database"),
            context.Command.Options.Optional("--table"),
            context.Command.Options.Optional("--target-database"),
            context.Command.Options.Optional("--target-table"),
            context.Command.Options.Has("--append"),
            context.Command.Options.Has("--allow-schema-mismatch"),
            context.Command.Options.Optional("--layout") is { } layout ? ParseLayout(layout) : null,
            context.Command.Options.Optional("--source-shard") is { } sourceShard ? int.Parse(sourceShard) : null,
            context.Command.Options.Optional("--target-shard") is { } targetShard ? int.Parse(targetShard) : null,
            tableMappings,
            context.Command.Options.Has("--schema-only"),
            sourceShards,
            targetShards,
            context.Command.Options.Has("--confirm-destructive"),
            settings);
        return await CommandHelpers.WithClient(context, client => client.PostAsync("restores/initiate", request));
    }


    private static async Task<object?> PlanAsync(CommandContext context)
    {
        var request = BuildEntityRestorePlanRequest(context, confirmDestructive: context.Command.Options.Has("--confirm-destructive"));
        return await CommandHelpers.WithClient(context, client => client.PostAsync("restores/plan", request));
    }

    private static async Task<object?> InitiateFromPlanAsync(CommandContext context)
    {
        var path = context.Command.Options.Required("--file");
        var json = File.ReadAllText(path);
        var request = ReadEntityRestorePlanRequest(json);
        return await CommandHelpers.WithClient(context, client => client.PostAsync("restores/initiate-from-plan", request));
    }

    private static async Task<object?> InitiateFromPolicyAsync(CommandContext context)
    {
        var request = BuildEntityRestorePlanRequest(context, confirmDestructive: context.Command.Options.Has("--confirm-destructive"));
        return await CommandHelpers.WithClient(context, client => client.PostAsync("restores/initiate-from-plan", request));
    }


    private static EntityRestorePlanRequest ReadEntityRestorePlanRequest(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("cliJson", out var cliJson) && cliJson.ValueKind == JsonValueKind.String)
        {
            json = cliJson.GetString() ?? throw new InvalidOperationException("Restore plan cliJson was empty.");
        }

        return JsonSerializer.Deserialize<EntityRestorePlanRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Restore plan file was empty or invalid.");
    }
    private static EntityRestorePlanRequest BuildEntityRestorePlanRequest(CommandContext context, bool confirmDestructive)
    {
        var options = context.Command.Options;
        var policyIdText = options.Optional("--policy-id");
        var anchorBackupIdText = options.Optional("--anchor-backup-id") ?? options.Optional("--backup-id");
        if (string.IsNullOrWhiteSpace(policyIdText) && string.IsNullOrWhiteSpace(anchorBackupIdText))
        {
            throw new InvalidOperationException("Use --policy-id or --anchor-backup-id.");
        }
        var targetClusterId = Guid.Parse(options.Required("--target-cluster-id"));
        return new EntityRestorePlanRequest(
            string.IsNullOrWhiteSpace(policyIdText) ? null : Guid.Parse(policyIdText),
            string.IsNullOrWhiteSpace(anchorBackupIdText) ? null : Guid.Parse(anchorBackupIdText),
            targetClusterId,
            options.Optional("--database"),
            options.Optional("--table"),
            options.Optional("--target-database"),
            options.Optional("--target-table"),
            options.Has("--append"),
            options.Has("--allow-schema-mismatch"),
            options.Optional("--layout") is { } layout ? ParseLayout(layout) : null,
            options.Optional("--source-shard") is { } sourceShard ? int.Parse(sourceShard) : null,
            options.Optional("--target-shard") is { } targetShard ? int.Parse(targetShard) : null,
            ParseTableMappings(options),
            options.Has("--schema-only"),
            ParseShards(options.Optional("--source-shards")),
            ParseShards(options.Optional("--target-shards")),
            confirmDestructive,
            null);
    }
    private static IReadOnlyList<int>? ParseShards(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse)
                .ToArray();

    private static IReadOnlyList<RestoreTableMappingRequest>? ParseTableMappings(OptionBag options)
    {
        var json = options.Optional("--table-mappings-json");
        if (options.Optional("--table-mappings-file") is { } path)
        {
            json = File.ReadAllText(path);
        }

        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<IReadOnlyList<RestoreTableMappingRequest>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static RestoreLayout ParseLayout(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "preserve" => RestoreLayout.Preserve,
            "single-node" or "singlenode" => RestoreLayout.SingleNode,
            "redistribute" => RestoreLayout.Redistribute,
            _ => Enum.Parse<RestoreLayout>(value, ignoreCase: true)
        };

    private static bool HasClickHouseSettingsOptions(OptionBag options, string prefix) =>
        options.Has($"--clickhouse-{prefix}-settings-json") ||
        options.Has($"--clickhouse-{prefix}-settings-file") ||
        options.Has($"--clickhouse-{prefix}-setting") ||
        options.Has($"--remove-clickhouse-{prefix}-setting");}

public sealed class RestoresCommands : CliSubject
{
    public RestoresCommands()
    {
        Verb("list", "List restores.", ListAsync);
        Verb("show", "Show one restore.", ShowAsync);
        Verb("wait", "Wait for a restore to finish.", WaitAsync);
        Verb("cancel", "Cancel a queued or running restore.", CancelAsync);
    }

    public override string Name => "restores";
    public override string Description => "Inspect restore operations.";

    private static Task<object?> ListAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("restores"));

    private static Task<object?> ShowAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync($"restores/{context.Command.Options.Required("--id")}"));

    private static Task<object?> CancelAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync($"restores/{context.Command.Options.Required("--id")}/cancel", new { }));
    private static async Task<object?> WaitAsync(CommandContext context)
    {
        using var client = await context.CreateClientAsync();
        var id = context.Command.Options.Required("--id");
        var timeout = TimeSpan.FromSeconds(context.Command.Options.Int("--timeout-seconds", 300));
        var interval = TimeSpan.FromSeconds(context.Command.Options.Int("--poll-seconds", 2));
        var deadline = DateTimeOffset.UtcNow + timeout;
        RestoreDto? current = null;
        Exception? lastError = null;
        do
        {
            try
            {
                current = await client.GetOptionalAsync<RestoreDto>($"restores/{id}");
                lastError = null;
                if (current is not null && IsTerminal(current.Status))
                {
                    return current;
                }
            }
            catch (Exception ex) when (DateTimeOffset.UtcNow < deadline)
            {
                lastError = ex;
            }

            await Task.Delay(interval);
        } while (DateTimeOffset.UtcNow < deadline);

        if (current is null && lastError is not null)
        {
            throw lastError;
        }

        return current;
    }

    private static bool IsTerminal(RestoreRunStatus status) =>
        status is RestoreRunStatus.Succeeded or RestoreRunStatus.PartiallySucceeded or RestoreRunStatus.Failed or RestoreRunStatus.Canceled;
}
