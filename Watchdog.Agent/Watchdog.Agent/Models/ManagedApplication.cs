using Watchdog.Agent.Protos;
using ApplicationStatus = Watchdog.Agent.Enums.ApplicationStatus;

namespace Watchdog.Agent.Models;

public class ManagedApplication
{
    public string InstanceId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public SpawnCommand? Command { get; set; }
    public bool IsWindowsService { get; set; }
    public string? WindowsServiceName { get; set; }
    public bool PersistWindowsService { get; set; }
    public ApplicationStatus Status { get; set; } = Enums.ApplicationStatus.Pending;
    public int? ProcessId { get; set; }
    public int? AssignedPort { get; set; }
    public ProcessMetrics? Metrics { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public bool StopReported { get; set; } = false;
    public DateTime? LastHealthCheck { get; set; }
    public int RestartCount { get; set; }
    public DateTime? LastRestartAttempt { get; set; }
    public string? LastError { get; set; }
    public DateTime? ReattachedAt { get; set; }
}
