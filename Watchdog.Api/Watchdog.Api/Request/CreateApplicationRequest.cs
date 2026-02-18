using System.ComponentModel.DataAnnotations;

namespace Watchdog.Api.Request;

public class CreateApplicationRequest
{
    public string? Id { get; set; }
    
    [Required]
    public string Name { get; set; } = string.Empty;
    
    public string? DisplayName { get; set; }
    
    [Required]
    public string ExecutablePath { get; set; } = string.Empty;
    
    public string? Arguments { get; set; }
    
    public string? WorkingDirectory { get; set; }
    
    [Range(0, 2)]
    public int ApplicationType { get; set; } // 0=Console, 1=Service, 2=IIS
    
    [Range(5, 3600)]
    public int HeartbeatTimeout { get; set; } = 120;
    
    [Range(0, 100)]
    public int DesiredInstances { get; set; } = 1;
    
    [Range(0, 100)]
    public int MinInstances { get; set; } = 1;
    
    [Range(1, 100)]
    public int MaxInstances { get; set; } = 5;
    
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    
    public bool AutoStart { get; set; } = true;
}