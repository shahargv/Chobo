using ChoboServer.Data;
using ChoboServer.Options;
using Microsoft.Extensions.Options;

namespace ChoboServer.BackgroundServices;

public sealed class SqliteQueryStatisticsBackgroundService(
    IServiceProvider services,
    IOptionsMonitor<ChoboSqliteOptions> options,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<SqliteQueryStatisticsBackgroundService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CurrentInterval(), stoppingToken);
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
                await DatabasePerformanceMaintenance.RefreshQueryStatisticsAsync(db, _logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "SQLite query statistics refresh cycle failed; continuing with existing statistics.");
            }
        }
    }

    private TimeSpan CurrentInterval()
    {
        var configured = options.CurrentValue.QueryStatisticsRefreshInterval;
        return configured <= TimeSpan.Zero ? TimeSpan.FromHours(1) : configured;
    }
}
