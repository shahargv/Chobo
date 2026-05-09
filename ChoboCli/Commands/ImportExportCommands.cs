using System.Text.Json;
using Chobo.Contracts;
using ChoboCli.Cli;
using ChoboCli.Infrastructure;

namespace ChoboCli.Commands;

public sealed class ImportExportCommands : CliSubject
{
    public ImportExportCommands(string name)
    {
        Name = name;
        Verb("export", $"Export {name}.", ExportAsync);
        Verb("import", $"Import {name}.", ImportAsync);
    }

    public override string Name { get; }
    public override string Description => Name == "data" ? "Full data import/export." : "Configuration import/export.";

    private Task<object?> ExportAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, async client =>
        {
            var value = await client.GetAsync($"{Name}/export");
            if (context.Command.Options.Optional("--output") is { } path)
            {
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOutputWriter.JsonOptions));
            }

            return value;
        });

    private Task<object?> ImportAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, async client =>
        {
            var path = context.Command.Options.Required("--file");
            var envelope = JsonSerializer.Deserialize<ExportEnvelope>(await File.ReadAllTextAsync(path), JsonOutputWriter.JsonOptions)
                ?? throw new InvalidOperationException("Invalid export file.");
            return await client.PostAsync($"{Name}/import", envelope);
        });
}
