namespace Watchdog.Agent.Interface;

public interface IHealthChecker
{
    Task<bool> PerformHttpHealthCheck(string url, int timeoutSeconds);
}
