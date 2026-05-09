using ChoboServer.Application;
using ChoboServer.Services;

namespace ChoboServer.BackgroundServices;

public sealed class RestoreExecutorBackgroundService(
    IServiceProvider services,
    BackupRestoreQueues queues,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<RestoreExecutorBackgroundService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var restoreId in queues.Restores.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = services.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<RestoreRunnerService>();
                await runner.RunAsync(restoreId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Restore execution failed for {RestoreId}.", restoreId);
            }
        }
    }
}
