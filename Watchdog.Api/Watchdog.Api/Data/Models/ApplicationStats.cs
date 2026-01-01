namespace Watchdog.Api.Data.Models;

public class ApplicationStats
{
    public string AppId { get; set; } = string.Empty;
    public int TotalInstances { get; set; }
    public int RunningInstances { get; set; }
    public double AvgCpuPercent { get; set; }
    public double AvgMemoryMb { get; set; }
    public DateTime? FirstInstanceCreated { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    
    public double HealthPercentage => 
        TotalInstances > 0 ? (RunningInstances * 100.0 / TotalInstances) : 0;
}