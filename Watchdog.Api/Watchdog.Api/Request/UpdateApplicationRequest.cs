using System.ComponentModel.DataAnnotations;

namespace Watchdog.Api.Request;

public class UpdateApplicationRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;
    
    public string? DisplayName { get; set; }
    
    [Required]
    public string ExecutablePath { get; set; } = string.Empty;
    
    public string? Arguments { get; set; }
    
    public string? WorkingDirectory { get; set; }
    
    [Range(0, 2)]
    public int ApplicationType { get; set; }
    
    [Range(5, 300)]
    public int HealthCheckInterval { get; set; }
    
    [Range(5, 3600)]
    public int HeartbeatTimeout { get; set; }
    
    [Range(0, 100)]
    public int DesiredInstances { get; set; }
    
    [Range(0, 100)]
    public int MinInstances { get; set; }
    
    [Range(1, 100)]
    public int MaxInstances { get; set; }
    
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    
    public bool AutoStart { get; set; }
}