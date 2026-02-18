using Watchdog.Agent.Models;
using Watchdog.Agent.Protos;
using Watchdog.Agent.Services;

namespace Watchdog.Agent.Interface;

public interface IProcessManager
{
    Task<ProcessSpawnResult> SpawnProcess(SpawnCommand command, int assignedPort);
    Task<bool> KillProcess(string instanceId, bool force = false, int timeoutSeconds = 30);
    Task<bool> RestartProcess(string instanceId, int timeoutSeconds = 30);
    Task<ProcessInfo?> GetProcessInfo(string instanceId);
    Task<List<ProcessInfo>> GetAllProcesses();
    Task<bool> IsProcessRunning(string instanceId);
    Task<ProcessMetrics?> GetProcessMetrics(string instanceId);
    Task<List<ManagedProcess>> ReattachProcesses();
}
