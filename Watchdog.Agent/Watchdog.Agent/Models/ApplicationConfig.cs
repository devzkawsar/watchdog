using Watchdog.Api.Protos;

namespace Watchdog.Agent.Models;

public class ApplicationConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public int DesiredInstances { get; set; } = 1;
    public int? BuiltInPort { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public string HealthCheckUrl { get; set; } = string.Empty;
    public int HealthCheckInterval { get; set; } = 30;
    public int MaxRestartAttempts { get; set; } = 3;
    public int RestartDelaySeconds { get; set; } = 10;
}