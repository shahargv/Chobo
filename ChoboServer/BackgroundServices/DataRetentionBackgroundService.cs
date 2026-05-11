using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.Extensions.Options;

namespace ChoboServer.BackgroundServices;

public sealed class DataRetentionBackgroundService(
    IServiceProvider services,
    IOptions<ChoboDataRetentionOptions> options,
    Serilog.ILogger logger) : BackgroundService
{
    private readonly Serilog.ILogger _logger = logger.ForContext<DataRetentionBackgroundService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Data retention purge failed.");
            }

            var interval = options.Value.Interval <= TimeSpan.Zero
                ? TimeSpan.FromHours(1)
                : options.Value.Interval;
            await Task.Delay(interval, stoppingToken);
        }
    }

    public async Task<(int LogsDeleted, int AuditsDeleted)> PurgeOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var logsDeleted = 0;
        var auditsDeleted = 0;
        var retention = options.Value;

        if (retention.LogsBefore is not null)
        {
            var logs = scope.ServiceProvider.GetRequiredService<ApplicationLogTimelineStore>();
            logsDeleted = await logs.DeleteBeforeAsync(retention.LogsBefore.Value, cancellationToken);
        }

        if (retention.AuditsBefore is not null)
        {
            var audits = scope.ServiceProvider.GetRequiredService<AuditTimelineStore>();
            auditsDeleted = await audits.DeleteBeforeAsync(retention.AuditsBefore.Value, cancellationToken);
        }

        if (retention.LogsBefore is not null || retention.AuditsBefore is not null)
        {
            var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
            await audit.RecordAsync("retention-purge", AuditEntityType.DataRetention, null, new
            {
                retention.LogsBefore,
                retention.AuditsBefore,
                logsDeleted,
                auditsDeleted
            });
        }

        return (logsDeleted, auditsDeleted);
    }
}
