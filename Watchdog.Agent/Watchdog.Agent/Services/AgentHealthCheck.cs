using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Watchdog.Agent.Services;

public class AgentHealthCheck : IHealthCheck
{
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Agent is running"));
    }
}
