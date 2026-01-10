using Watchdog.Agent.Models;
using Watchdog.Agent.Protos;

namespace Watchdog.Agent.Services;

public interface IProcessManager
{
    Task<ProcessSpawnResult> SpawnProcessAsync(SpawnCommand command, List<PortMapping> ports);
    Task<bool> KillProcessAsync(string instanceId, bool force = false, int timeoutSeconds = 30);
    Task<bool> RestartProcessAsync(string instanceId, int timeoutSeconds = 30);
    Task<ProcessInfo?> GetProcessInfoAsync(string instanceId);
    Task<List<ProcessInfo>> GetAllProcessesAsync();
    Task<bool> IsProcessRunningAsync(string instanceId);
    Task<ProcessMetrics?> GetProcessMetricsAsync(string instanceId);
}
