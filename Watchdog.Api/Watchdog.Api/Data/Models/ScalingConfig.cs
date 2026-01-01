namespace Watchdog.Api.Data.Models;

public class ScalingConfig
{
    public string AppId { get; set; } = string.Empty;
    public int DesiredReplica { get; set; } = 1;
    public int MinReplica { get; set; } = 1;
    public int MaxReplica { get; set; } = 5;
    public double ScaleUpThresholdCpu { get; set; } = 80.0;
    public double ScaleDownThresholdCpu { get; set; } = 20.0;
    public double ScaleUpThresholdMemory { get; set; } = 85.0;
    public double ScaleDownThresholdMemory { get; set; } = 30.0;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}