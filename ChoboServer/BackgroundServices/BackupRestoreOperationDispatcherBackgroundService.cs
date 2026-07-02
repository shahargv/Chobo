using Chobo.Contracts;
using ChoboServer.Application;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.Extensions.Options;

namespace ChoboServer.BackgroundServices;

public sealed class BackupRestoreOperationDispatcherBackgroundService(
    IServiceProvider services,
    IBackupRestoreQueues queues,
    IOptionsMonitor<ChoboBackupRestoreOptions> options,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupRestoreOperationDispatcherBackgroundService>();
    private readonly List<Task> _workers = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = EffectiveWorkerCount(options.CurrentValue);
        for (var i = 0; i < workerCount; i++)
        {
            var workerId = i + 1;
            _workers.Add(Task.Run(() => RunWorkerAsync(workerId, stoppingToken), stoppingToken));
        }

        try
        {
            await Task.WhenAll(_workers);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in queues.Operations.Reader.ReadAllAsync(stoppingToken))
            {
                await WaitForQueueCapacityAsync(item, stoppingToken);
                var result = await RunOperationAsync(workerId, item, stoppingToken);
                if (result == BackupRestoreOperationRunResult.Deferred && !stoppingToken.IsCancellationRequested)
                {
                    ScheduleRequeueAfterDelay(item, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task<BackupRestoreOperationRunResult> RunOperationAsync(int workerId, BackupRestoreOperationWorkItem item, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = services.CreateScope();
            _logger.Information("Backup/restore dispatcher worker {WorkerId} starting {Kind} operation {OperationId} from {Reason}.", workerId, item.Kind, item.OperationId, item.Reason);
            return item.Kind switch
            {
                BackupRestoreQueueKind.Backup => await scope.ServiceProvider.GetRequiredService<BackupRunnerService>().RunAsync(item.OperationId, stoppingToken),
                BackupRestoreQueueKind.Restore => await scope.ServiceProvider.GetRequiredService<RestoreRunnerService>().RunAsync(item.OperationId, stoppingToken),
                _ => BackupRestoreOperationRunResult.Completed
            };
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return BackupRestoreOperationRunResult.Completed;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Backup/restore dispatcher worker {WorkerId} failed while running {Kind} operation {OperationId}.", workerId, item.Kind, item.OperationId);
            return BackupRestoreOperationRunResult.Completed;
        }
    }

    private void ScheduleRequeueAfterDelay(BackupRestoreOperationWorkItem item, CancellationToken stoppingToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(EffectivePollInterval(options.CurrentValue), stoppingToken);
                await queues.Operations.Writer.WriteAsync(item with { Reason = "deferred-retry" }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to requeue deferred {Kind} operation {OperationId}.", item.Kind, item.OperationId);
            }
        }, CancellationToken.None);
    }

    private async Task WaitForQueueCapacityAsync(BackupRestoreOperationWorkItem item, CancellationToken stoppingToken)
    {
        var maxActiveQueueItems = options.CurrentValue.MaxActiveQueueItems;
        if (maxActiveQueueItems <= 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = services.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<BackupRestoreQueueApplicationService>();
            if (await queue.HasForcedQueuedOperationWorkAsync(item.Kind, item.OperationId, stoppingToken) ||
                await queue.HasActiveQueueCapacityAsync(maxActiveQueueItems, stoppingToken))
            {
                return;
            }

            await Task.Delay(EffectivePollInterval(options.CurrentValue), stoppingToken);
        }
    }

    private static int EffectiveWorkerCount(ChoboBackupRestoreOptions value) =>
        Math.Max(1, value.WorkerCount <= 0 ? 1 : value.WorkerCount);

    private static TimeSpan EffectivePollInterval(ChoboBackupRestoreOptions value) =>
        value.PollInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : value.PollInterval;
}
