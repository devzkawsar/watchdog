namespace Watchdog.Agent.Configuration;

public class AgentSettings
{
    public string AgentId { get; set; } = Environment.MachineName;
    public string AgentName { get; set; } = Environment.MachineName;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public bool AutoRegister { get; set; } = true;
    public int RegistrationRetryInterval { get; set; } = 30;
    public int MaxRegistrationAttempts { get; set; } = 10;
    public string WorkingDirectory { get; set; } = 
        OperatingSystem.IsWindows() 
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Watchdog", "Applications")
            : Path.Combine(AppContext.BaseDirectory, "data", "applications");
            
    public string LogDirectory { get; set; } = 
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Watchdog", "Logs")
            : Path.Combine(AppContext.BaseDirectory, "data", "logs");
            
    public string DataDirectory { get; set; } = 
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Watchdog", "Data")
            : Path.Combine(AppContext.BaseDirectory, "data", "storage");
}
