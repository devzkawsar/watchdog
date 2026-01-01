namespace Watchdog.Api.Data.Models;

public class Application
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public int AppType { get; set; } // 0=Console, 1=Service, 2=IIS
    public int DesiredInstances { get; set; } = 1;
    public int MaxRestartAttempts { get; set; } = 3;
    public bool AutoStart { get; set; } = true;
    public int Status { get; set; } // 0=Unknown, 1=Healthy, 2=Warning, 3=Error
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
