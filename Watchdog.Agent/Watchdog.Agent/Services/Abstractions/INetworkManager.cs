namespace Watchdog.Agent.Services;

public interface INetworkManager
{
    Task<int> AllocatePortAsync(int preferredPort = 0);
    Task<bool> ReleasePortAsync(int port);
    Task<bool> ReleasePortsAsync(List<int> ports);
    Task<List<int>> GetAvailablePortsAsync();
    Task<int> GetAvailablePortCountAsync();
    Task<bool> IsPortAvailableAsync(int port);
    Task<bool> IsPortInUseAsync(int port);
    Task<List<NetworkInterfaceInfo>> GetNetworkInterfacesAsync();
}
