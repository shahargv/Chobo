using Chobo.Contracts;
using ChoboCli.Cli;

namespace ChoboCli.Commands;

public sealed class SchemaCommands : CliSubject
{
    public SchemaCommands()
    {
        Verb("backups", "List backups with retained schema metadata.", ListBackupsAsync);
        Verb("show", "Show schema captured by a backup.", ShowAsync);
        Verb("export", "Export schema SQL captured by a backup.", ExportAsync);
    }

    public override string Name => "schema";
    public override string Description => "Browse schema captured by backup runs.";

    private static Task<object?> ListBackupsAsync(CommandContext context) =>
        CommandHelpers.WithClient(context, client => client.GetAsync("schema/backups"));

    private static Task<object?> ShowAsync(CommandContext context)
    {
        var backupId = context.Command.Options.Required("--backup-id");
        var database = context.Command.Options.Optional("--database");
        var table = context.Command.Options.Optional("--table");
        return CommandHelpers.WithClient(context, async client =>
        {
            var schema = await client.GetAsync<SchemaBackupDto>($"schema/backups/{backupId}")
                ?? throw new InvalidOperationException("Backup schema was not found.");
            if (string.IsNullOrWhiteSpace(database) && string.IsNullOrWhiteSpace(table))
            {
                return schema;
            }

            var databases = schema.Databases
                .Where(db => string.IsNullOrWhiteSpace(database) || string.Equals(db.Database, database, StringComparison.Ordinal))
                .Select(db => db with
                {
                    Tables = db.Tables
                        .Where(t => string.IsNullOrWhiteSpace(table) || string.Equals(t.Table, table, StringComparison.Ordinal) || string.Equals($"{t.Database}.{t.Table}", table, StringComparison.Ordinal))
                        .ToList()
                })
                .Where(db => db.Tables.Count > 0)
                .ToList();
            return schema with { Databases = databases };
        });
    }

    private static Task<object?> ExportAsync(CommandContext context)
    {
        var backupId = context.Command.Options.Required("--backup-id");
        var database = context.Command.Options.Optional("--database");
        var query = string.IsNullOrWhiteSpace(database) ? "" : $"?database={Uri.EscapeDataString(database)}";
        return CommandHelpers.WithClient(context, async client => await client.GetTextAsync($"schema/backups/{backupId}/export{query}"));
    }
}

