using Watchdog.Api.Interface;

namespace Watchdog.Api.BackgroundServices;

public class HeartbeatMonitorBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HeartbeatMonitorBackgroundService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(10);

    public HeartbeatMonitorBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<HeartbeatMonitorBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Heartbeat monitor background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IApplicationRepository>();

                var staleInstances = (await repo.GetStaleInstancesByHeartbeat()).ToList();
                if (staleInstances.Count > 0)
                {
                    foreach (var stale in staleInstances)
                    {
                        var now = DateTime.UtcNow;
                        var timeoutSeconds = stale.HeartbeatTimeout;

                        if (timeoutSeconds <= 0)
                        {
                            continue;
                        }

                        var overdueSeconds = stale.LastHeartbeat.HasValue
                            ? (now - stale.LastHeartbeat.Value).TotalSeconds
                            : double.MaxValue;

                        var targetStatus = overdueSeconds >= (timeoutSeconds * 3)
                            ? "error"
                            : "warning";

                        if (string.Equals(stale.Status, targetStatus, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var updated = await repo.UpdateInstanceStatusWithoutHeartbeat(stale.InstanceId, targetStatus);
                        if (updated > 0)
                        {
                            _logger.LogWarning(
                                "Marked instance {InstanceId} (App: {ApplicationId}, Agent: {AgentId}) as {Status} due to stale heartbeat (LastHeartbeat: {LastHeartbeat}, TimeoutSeconds: {HeartbeatTimeout})",
                                stale.InstanceId,
                                stale.ApplicationId,
                                stale.AgentId,
                                targetStatus,
                                stale.LastHeartbeat,
                                stale.HeartbeatTimeout);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in heartbeat monitor background service");
            }

            await Task.Delay(_interval, stoppingToken);
        }

        _logger.LogInformation("Heartbeat monitor background service stopped");
    }
}
