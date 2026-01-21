namespace Watchdog.Agent.Models;

public class ProcessMetrics
{
    public double CpuPercent { get; set; }
    public double MemoryMB { get; set; }
    public int ThreadCount { get; set; }
    public long HandleCount { get; set; }
    public long IoReadBytes { get; set; }
    public long IoWriteBytes { get; set; }
    public DateTime CollectedAt { get; set; }
}