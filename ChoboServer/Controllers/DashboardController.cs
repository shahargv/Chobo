using Chobo.Contracts;
using ChoboServer.Application;
using Microsoft.AspNetCore.Mvc;

namespace ChoboServer.Controllers;

[ApiController]
[Route(ChoboApi.ApiPrefix)]
public sealed class DashboardController(DashboardApplicationService dashboard) : ControllerBase
{
    [HttpGet("dashboard")]
    public Task<DashboardDto> GetDashboard([FromQuery] int nextHours = 6, CancellationToken cancellationToken = default) =>
        dashboard.GetDashboardAsync(nextHours, cancellationToken);

    [HttpGet("metrics")]
    public Task<IReadOnlyDictionary<string, double?>> GetMetrics(CancellationToken cancellationToken) =>
        dashboard.GetMetricsAsync(cancellationToken);
}

