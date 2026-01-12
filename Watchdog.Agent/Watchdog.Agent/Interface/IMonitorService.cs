
using Watchdog.Agent.Models;
using Watchdog.Agent.Services;

namespace Watchdog.Agent.Interface;

public interface IMonitorService
{
    Task Start(CancellationToken cancellationToken);
    Task Stop();
    Task<List<ProcessMetrics>> CollectMetrics();
    Task<bool> CheckHealth(string instanceId);
    Task<List<HealthCheckResult>> CheckAllHealth();
    Task DetectFailures();
    Task<List<FailureDetection>> DetectAllFailures();
}
