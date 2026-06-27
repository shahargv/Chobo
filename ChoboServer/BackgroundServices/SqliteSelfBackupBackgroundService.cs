using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.BackgroundServices;

public sealed class SqliteSelfBackupBackgroundService(
    IServiceProvider services,
    IOptionsMonitor<ChoboSqliteSelfBackupOptions> options,
    IOptions<ChoboStorageOptions> storageOptions,
    TimeProvider timeProvider,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<SqliteSelfBackupBackgroundService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "SQLite self-backup check failed.");
            }

            var interval = options.CurrentValue.PollInterval <= TimeSpan.Zero
                ? TimeSpan.FromMinutes(5)
                : options.CurrentValue.PollInterval;
            await Task.Delay(interval, stoppingToken);
        }
    }

    public async Task<string?> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var selfBackup = options.CurrentValue;
        if (!selfBackup.Enabled)
        {
            return null;
        }

        var backupInterval = selfBackup.BackupInterval <= TimeSpan.Zero
            ? TimeSpan.FromDays(1)
            : selfBackup.BackupInterval;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var state = await GetOrCreateStateAsync(db, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (state.LastBackupAt is not null && now - state.LastBackupAt.Value < backupInterval)
        {
            return null;
        }

        state.LastAttemptAt = now;
        state.LastError = null;
        await db.SaveChangesAsync(cancellationToken);

        var dataDirectory = ChoboPaths.GetDataDirectory(storageOptions.Value.DataDirectory);
        var sourcePath = Path.Combine(dataDirectory, "chobo.db");
        var destinationDirectory = ResolveBackupDirectory(selfBackup.Directory, dataDirectory);
        var destinationPath = Path.Combine(destinationDirectory, $"chobo-{now:yyyyMMdd-HHmmssfff}Z.db");

        try
        {
            _logger.Information("Creating SQLite self-backup from {SourcePath} to {DestinationPath}.", sourcePath, destinationPath);
            Directory.CreateDirectory(destinationDirectory);
            BackupSqliteDatabase(sourcePath, destinationPath);

            state.LastBackupAt = now;
            state.LastBackupPath = destinationPath;
            state.LastError = null;
            await db.SaveChangesAsync(cancellationToken);

            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            await audit.RecordAsync("sqlite-self-backup-created", AuditEntityType.SqliteSelfBackup, null, new
            {
                path = destinationPath,
                backupInterval,
                sourcePath
            });

            _logger.Information("Created SQLite self-backup at {DestinationPath}.", destinationPath);
            return destinationPath;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SQLite self-backup failed for destination {DestinationPath}.", destinationPath);
            state.LastError = ex.Message;
            await db.SaveChangesAsync(cancellationToken);

            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            await audit.RecordAsync("sqlite-self-backup-failed", AuditEntityType.SqliteSelfBackup, null, new
            {
                path = destinationPath,
                backupInterval,
                sourcePath,
                error = ex.Message
            });

            throw;
        }
    }

    private static async Task<SqliteSelfBackupStateEntity> GetOrCreateStateAsync(ChoboDbContext db, CancellationToken cancellationToken)
    {
        var state = await db.SqliteSelfBackupStates.SingleOrDefaultAsync(cancellationToken);
        if (state is not null)
        {
            return state;
        }

        state = new SqliteSelfBackupStateEntity();
        db.SqliteSelfBackupStates.Add(state);
        await db.SaveChangesAsync(cancellationToken);
        return state;
    }

    private static string ResolveBackupDirectory(string? configuredDirectory, string dataDirectory) =>
        string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(dataDirectory, "sqlite-backups")
            : Path.GetFullPath(configuredDirectory);

    private static void BackupSqliteDatabase(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("SQLite database file was not found.", sourcePath);
        }

        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath
        }.ToString();

        using var source = new SqliteConnection(sourceConnectionString);
        using var destination = new SqliteConnection(destinationConnectionString);
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }
}
