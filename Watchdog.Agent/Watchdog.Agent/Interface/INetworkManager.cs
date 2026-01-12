using Watchdog.Agent.Services;

namespace Watchdog.Agent.Interface;

public interface INetworkManager
{
    Task<int> AllocatePort(int preferredPort = 0);
    Task<bool> ReleasePort(int port);
    Task<bool> ReleasePorts(List<int> ports);
    Task<List<int>> GetAvailablePorts();
    Task<int> GetAvailablePortCount();
    Task<bool> IsPortAvailable(int port);
    Task<bool> IsPortInUse(int port);
    Task<List<NetworkInterfaceInfo>> GetNetworkInterfaces();
}
