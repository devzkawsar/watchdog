using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Watchdog.Agent.Configuration;
using Watchdog.Agent.Interface;

namespace Watchdog.Agent.Services;

internal interface INetworkManagerInternal : INetworkManager
{
}

public class NetworkManager : INetworkManagerInternal
{
    private readonly ILogger<NetworkManager> _logger;
    private readonly IOptions<NetworkSettings> _networkSettings;
    
    private readonly HashSet<int> _allocatedPorts = new();
    private readonly HashSet<int> _reservedPorts;
    private readonly object _lock = new();
    
    public NetworkManager(
        ILogger<NetworkManager> logger,
        IOptions<NetworkSettings> networkSettings)
    {
        _logger = logger;
        _networkSettings = networkSettings;
        
        // Initialize reserved ports
        _reservedPorts = new HashSet<int>(_networkSettings.Value.ReservedPorts);
        
        _logger.LogInformation(
            "NetworkManager initialized with port range {Start}-{End}", 
            _networkSettings.Value.PortRangeStart, 
            _networkSettings.Value.PortRangeEnd);
    }
    
    public Task<int> AllocatePort(int preferredPort = 0)
    {
        lock (_lock)
        {
            // Check preferred port first
            if (preferredPort > 0)
            {
                if (IsPortAvailableInternal(preferredPort))
                {
                    _allocatedPorts.Add(preferredPort);
                    _logger.LogDebug("Allocated preferred port {Port}", preferredPort);
                    return Task.FromResult(preferredPort);
                }
            }
            
            // Find next available port
            for (int port = _networkSettings.Value.PortRangeStart; 
                 port <= _networkSettings.Value.PortRangeEnd; 
                 port++)
            {
                if (IsPortAvailableInternal(port))
                {
                    _allocatedPorts.Add(port);
                    _logger.LogDebug("Allocated port {Port}", port);
                    return Task.FromResult(port);
                }
            }
            
            _logger.LogError("No available ports in range {Start}-{End}", 
                _networkSettings.Value.PortRangeStart, 
                _networkSettings.Value.PortRangeEnd);
            
            return Task.FromResult(0);
        }
    }
    
    public Task<bool> ReleasePort(int port)
    {
        lock (_lock)
        {
            var removed = _allocatedPorts.Remove(port);
            if (removed)
            {
                _logger.LogDebug("Released port {Port}", port);
            }
            else
            {
                _logger.LogDebug("Port {Port} was not allocated", port);
            }
            return Task.FromResult(removed);
        }
    }
    
    
    public Task<List<int>> GetAvailablePorts()
    {
        lock (_lock)
        {
            var availablePorts = new List<int>();
            
            for (int port = _networkSettings.Value.PortRangeStart; 
                 port <= _networkSettings.Value.PortRangeEnd; 
                 port++)
            {
                if (IsPortAvailableInternal(port))
                {
                    availablePorts.Add(port);
                }
            }
            
            return Task.FromResult(availablePorts);
        }
    }
    
    public async Task<int> GetAvailablePortCount()
    {
        var availablePorts = await GetAvailablePorts();
        return availablePorts.Count;
    }
    
    public Task<bool> IsPortAvailable(int port)
    {
        lock (_lock)
        {
            return Task.FromResult(IsPortAvailableInternal(port));
        }
    }
    
    public async Task<bool> IsPortInUse(int port)
    {
        if (_networkSettings.Value.CheckPortAvailability)
        {
            return await CheckPortInUse(port);
        }
        
        return false;
    }
    
    public Task<List<NetworkInterfaceInfo>> GetNetworkInterfaces()
    {
        var interfaces = new List<NetworkInterfaceInfo>();
        
        try
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            
            foreach (var ni in networkInterfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                
                var info = new NetworkInterfaceInfo
                {
                    Name = ni.Name,
                    Description = ni.Description,
                    Type = ni.NetworkInterfaceType.ToString(),
                    Status = ni.OperationalStatus.ToString(),
                    Speed = ni.Speed,
                    SupportsIPv4 = ni.Supports(NetworkInterfaceComponent.IPv4),
                    SupportsIPv6 = ni.Supports(NetworkInterfaceComponent.IPv6)
                };
                
                // Get IP addresses
                var ipProperties = ni.GetIPProperties();
                foreach (var addr in ipProperties.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        info.IPv4Addresses.Add(addr.Address.ToString());
                    }
                    else if (addr.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        info.IPv6Addresses.Add(addr.Address.ToString());
                    }
                }
                
                interfaces.Add(info);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting network interfaces");
        }
        
        return Task.FromResult(interfaces);
    }
    
    private bool IsPortAvailableInternal(int port)
    {
        // Check if port is in range
        if (port < _networkSettings.Value.PortRangeStart || 
            port > _networkSettings.Value.PortRangeEnd)
        {
            return false;
        }
        
        // Check if port is reserved
        if (_reservedPorts.Contains(port))
        {
            return false;
        }
        
        // Check if port is already allocated
        if (_allocatedPorts.Contains(port))
        {
            return false;
        }
        
        // Check if port is in use on the system
        if (_networkSettings.Value.CheckPortAvailability)
        {
            return !CheckPortInUseSync(port);
        }
        
        return true;
    }
    
    private bool CheckPortInUseSync(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);
            socket.Bind(endpoint);
            socket.Close();
            
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking port {Port} availability", port);
            return true;
        }
    }
    
    private async Task<bool> CheckPortInUse(int port)
    {
        return await Task.Run(() => CheckPortInUseSync(port));
    }
}

public class NetworkInterfaceInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long Speed { get; set; }
    public bool SupportsIPv4 { get; set; }
    public bool SupportsIPv6 { get; set; }
    public List<string> IPv4Addresses { get; set; } = new();
    public List<string> IPv6Addresses { get; set; } = new();
}