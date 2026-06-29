using ChoboServer.Application;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.BackgroundServices;

public sealed class BackupExecutorBackgroundService(
    IServiceProvider services,
    IBackupRestoreQueues queues,
    IOptionsMonitor<ChoboBackupRestoreOptions> options,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupExecutorBackgroundService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var activeRuns = new List<Task>();
        try
        {
            await foreach (var backupId in queues.Backups.Reader.ReadAllAsync(stoppingToken))
            {
                await WaitForQueueCapacityAsync(stoppingToken);
                activeRuns.RemoveAll(x => x.IsCompleted);
                activeRuns.Add(RunBackupAsync(backupId, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        if (activeRuns.Count > 0)
        {
            await Task.WhenAll(activeRuns);
        }
    }

    private async Task WaitForQueueCapacityAsync(CancellationToken stoppingToken)
    {
        var maxActiveQueueItems = options.CurrentValue.MaxActiveQueueItems;
        if (maxActiveQueueItems <= 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChoboServer.Data.ChoboDbContext>();
            var activeQueueItems = await db.BackupRestoreQueueItems
                .AsNoTracking()
                .CountAsync(x => x.StartedAt != null && x.CompletedAt == null, stoppingToken);
            if (activeQueueItems < maxActiveQueueItems)
            {
                return;
            }

            await Task.Delay(options.CurrentValue.PollInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : options.CurrentValue.PollInterval, stoppingToken);
        }
    }

    private async Task RunBackupAsync(Guid backupId, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<BackupRunnerService>();
            await runner.RunAsync(backupId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Backup execution failed for {BackupId}.", backupId);
        }
    }
}
