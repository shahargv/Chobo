using ChoboServer.Data;
using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.BackgroundServices;

public sealed class ClickHouseClusterMetadataRefreshBackgroundService(
    IServiceProvider services,
    IOptionsMonitor<ChoboClusterMetadataOptions> options,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<ClickHouseClusterMetadataRefreshBackgroundService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "ClickHouse cluster metadata background refresh failed.");
            }

            var interval = options.CurrentValue.RefreshInterval <= TimeSpan.Zero
                ? TimeSpan.FromMinutes(30)
                : options.CurrentValue.RefreshInterval;
            await Task.Delay(interval, stoppingToken);
        }
    }

    public async Task RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChoboDbContext>();
        var metadata = scope.ServiceProvider.GetRequiredService<IClickHouseClusterMetadataService>();
        var clusters = await db.ClickHouseClusters
            .Include(x => x.AccessNodes)
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        foreach (var cluster in clusters)
        {
            try
            {
                await metadata.GetAsync(cluster, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "ClickHouse metadata refresh failed for cluster {ClusterId} ({ClusterName}): {Message}", cluster.Id, cluster.Name, ex.Message);
            }
        }
    }
}
