namespace Watchdog.Agent.Configuration;

public class MonitoringSettings
{
    public int StatusReportIntervalMs { get; set; } = 5000;
    public int HealthCheckIntervalMs { get; set; } = 30000;
    public int MetricsCollectionIntervalMs { get; set; } = 10000;
    public int FailureDetectionIntervalMs { get; set; } = 60000;
    public int MaxCpuThreshold { get; set; } = 90;
    public int MaxMemoryThreshold { get; set; } = 90;
    public int MaxRestartAttempts { get; set; } = 3;
    public int RestartDelaySeconds { get; set; } = 10;
}