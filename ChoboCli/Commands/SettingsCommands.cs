using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class SettingsCommands : CliSubject
{
    public SettingsCommands()
    {
        Verb("list", "List runtime settings. Options: --section <section> [--client-overrides-only]", ListAsync);
        Verb("get", "Show one runtime setting. Options: --key <key>", GetAsync);
        Verb("set", "Set a runtime setting overlay value. Options: --key <key> --value <value> [--confirm-restart-required]", SetAsync);
        Verb("unset", "Remove a runtime setting client override and restore the default or server configuration. Options: --key <key> [--confirm-restart-required]", UnsetAsync);
        Verb("reload", "Reload runtime settings from configuration providers.", ReloadAsync);
    }

    public override string Name => "settings";
    public override string Description => "Runtime server settings.";

    private static Task<object?> ListAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, async client =>
        {
            var settings = await client.GetAsync<RuntimeSettingsListDto>("settings") ?? new RuntimeSettingsListDto([]);
            if (context.Command.Options.Optional("--section") is { } section)
            {
                settings = new RuntimeSettingsListDto(settings.Items.Where(x => string.Equals(x.Section, section, StringComparison.OrdinalIgnoreCase)).ToList());
            }
            if (context.Command.Options.Has("--client-overrides-only"))
            {
                settings = new RuntimeSettingsListDto(settings.Items.Where(x => x.IsClientOverrideEffective).ToList());
            }

            return settings;
        });

    private static Task<object?> GetAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync($"settings/{Uri.EscapeDataString(context.Command.Options.Required("--key"))}"));

    private static Task<object?> SetAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, async client =>
        {
            var required = context.Command.Options.Require("--key", "--value");
            var key = required["--key"];
            await RequireRestartConfirmationAsync(client, key, context.Command.Options.Has("--confirm-restart-required"));
            return await client.PutAsync($"settings/{Uri.EscapeDataString(key)}", new UpdateRuntimeSettingRequest(required["--value"]));
        });

    private static Task<object?> UnsetAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, async client =>
        {
            var key = context.Command.Options.Required("--key");
            await RequireRestartConfirmationAsync(client, key, context.Command.Options.Has("--confirm-restart-required"));
            return await client.DeleteAsync($"settings/{Uri.EscapeDataString(key)}");
        });

    private static Task<object?> ReloadAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.PostAsync("settings/reload", new { }));

    private static async Task RequireRestartConfirmationAsync(Infrastructure.ChoboApiClient client, string key, bool confirmed)
    {
        var setting = await client.GetAsync<RuntimeSettingDto>($"settings/{Uri.EscapeDataString(key)}")
            ?? throw new InvalidOperationException($"Runtime setting '{key}' was not found.");
        if (setting.ApplyMode == RuntimeSettingApplyMode.RestartRequired && !confirmed)
        {
            throw new InvalidOperationException($"Setting {key} requires a server restart. Re-run with --confirm-restart-required to persist the change.");
        }
    }
}