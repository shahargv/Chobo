namespace ChoboServer;

public static class ChoboConfiguration
{
    public const string AppSettingsPathEnvironmentVariable = "CHOBO_APPSETTINGS_PATH";

    public static void AddChoboConfigurationSources(IConfigurationBuilder configuration, string[] args, bool addStandardEnvironmentAndCommandLine = false)
    {
        var appSettingsPath = Environment.GetEnvironmentVariable(AppSettingsPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(appSettingsPath))
        {
            configuration.AddJsonFile(appSettingsPath, optional: false, reloadOnChange: true);
        }

        if (addStandardEnvironmentAndCommandLine || !string.IsNullOrWhiteSpace(appSettingsPath))
        {
            configuration.AddEnvironmentVariables();
            if (args.Length > 0)
            {
                configuration.AddCommandLine(args);
            }
        }

        AddChoboEnvironmentAliases(configuration);
    }

    public static IConfiguration BuildLocalCommandConfiguration(string[] args)
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            builder.AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false);
        }

        AddChoboConfigurationSources(builder, args, addStandardEnvironmentAndCommandLine: true);
        AddLocalCommandAliases(builder, args);
        return builder.Build();
    }

    private static void AddChoboEnvironmentAliases(IConfigurationBuilder configuration)
    {
        var values = new Dictionary<string, string?>();
        AddAlias(values, "CHOBO_DATA_DIRECTORY", "Chobo:DataDirectory");
        AddAlias(values, "CHOBO_ENCRYPTION_KEY_BASE64", "Chobo:EncryptionKeyBase64");
        AddAlias(values, "CHOBO_INIT_ADMIN_USER", "Chobo:Init:AdminUser");
        AddAlias(values, "CHOBO_INIT_ACCESS_TOKEN", "Chobo:Init:AccessToken");
        AddAlias(values, "CHOBO_DATA_RETENTION_INTERVAL", "Chobo:DataRetention:Interval");
        AddAlias(values, "CHOBO_DATA_RETENTION_LOGS_BEFORE", "Chobo:DataRetention:LogsBefore");
        AddAlias(values, "CHOBO_DATA_RETENTION_AUDITS_BEFORE", "Chobo:DataRetention:AuditsBefore");
        AddAlias(values, "CHOBO_BACKUP_RESTORE_MAX_DOP", "Chobo:BackupRestore:MaxDop");
        AddAlias(values, "CHOBO_BACKUP_RESTORE_QUEUE_CAPACITY", "Chobo:BackupRestore:QueueCapacity");
        AddAlias(values, "CHOBO_BACKUP_RESTORE_SCHEDULER_INTERVAL", "Chobo:BackupRestore:SchedulerInterval");
        AddAlias(values, "CHOBO_BACKUP_RESTORE_SCHEDULER_MISSED_RUN_GRACE_PERIOD", "Chobo:BackupRestore:SchedulerMissedRunGracePeriod");
        AddAlias(values, "CHOBO_BACKUP_RESTORE_POLL_INTERVAL", "Chobo:BackupRestore:PollInterval");
        AddAlias(values, "CHOBO_TEST_HOOKS_ENABLED", "Chobo:TestHooks:Enabled");
        if (values.Count > 0)
        {
            configuration.AddInMemoryCollection(values);
        }
    }

    private static void AddLocalCommandAliases(IConfigurationBuilder configuration, string[] args)
    {
        var values = new Dictionary<string, string?>();
        AddOptionAlias(values, args, "--data-directory", "Chobo:DataDirectory");
        AddOptionAlias(values, args, "--encryption-key-base64", "Chobo:EncryptionKeyBase64");
        AddOptionAlias(values, args, "--admin-user", "Chobo:Init:AdminUser");
        AddOptionAlias(values, args, "--access-token", "Chobo:Init:AccessToken");
        if (values.Count > 0)
        {
            configuration.AddInMemoryCollection(values);
        }
    }

    private static void AddAlias(IDictionary<string, string?> values, string environmentName, string configurationKey)
    {
        var value = Environment.GetEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[configurationKey] = value;
        }
    }

    private static void AddOptionAlias(IDictionary<string, string?> values, string[] args, string optionName, string configurationKey)
    {
        var value = GetOption(args, optionName);
        if (value is not null)
        {
            values[configurationKey] = value;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
