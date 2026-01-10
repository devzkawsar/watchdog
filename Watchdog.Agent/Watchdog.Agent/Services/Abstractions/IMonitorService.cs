using Watchdog.Agent.Models;

namespace Watchdog.Agent.Services;

public interface IMonitorService
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
    Task<List<ProcessMetrics>> CollectMetricsAsync();
    Task<bool> CheckHealthAsync(string instanceId);
    Task<List<HealthCheckResult>> CheckAllHealthAsync();
    Task DetectFailuresAsync();
    Task<List<FailureDetection>> DetectAllFailuresAsync();
}
