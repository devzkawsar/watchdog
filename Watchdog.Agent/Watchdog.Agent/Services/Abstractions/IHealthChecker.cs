namespace Watchdog.Agent.Services;

public interface IHealthChecker
{
    Task<bool> PerformHttpHealthCheckAsync(string url, int timeoutSeconds);
}
