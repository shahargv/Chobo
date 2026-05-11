using ChoboServer.Application;
using ChoboServer.Services;

namespace ChoboServer.BackgroundServices;

public sealed class BackupExecutorBackgroundService(
    IServiceProvider services,
    BackupRestoreQueues queues,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<BackupExecutorBackgroundService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var backupId in queues.Backups.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = services.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<BackupRunnerService>();
                await runner.RunAsync(backupId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Backup execution failed for {BackupId}.", backupId);
            }
        }
    }
}
