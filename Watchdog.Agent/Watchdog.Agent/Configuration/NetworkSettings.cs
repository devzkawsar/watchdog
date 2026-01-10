namespace Watchdog.Agent.Configuration;

public class NetworkSettings
{
    public int PortRangeStart { get; set; } = 30000;
    public int PortRangeEnd { get; set; } = 40000;
    public int[] ReservedPorts { get; set; } = { 80, 443, 8080, 8443 };
    public bool CheckPortAvailability { get; set; } = true;
    public int PortCheckTimeoutMs { get; set; } = 100;
}