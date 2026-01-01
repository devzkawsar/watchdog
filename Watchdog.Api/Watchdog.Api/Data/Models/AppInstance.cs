namespace Watchdog.Api.Data.Models;

public class AppInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string AppId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public int Status { get; set; } // 0=Unknown, 1=Running, 2=Stopped, 3=Error
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
    public int? ProcessId { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}