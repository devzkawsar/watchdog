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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Watchdog", "Applications");
    public string LogDirectory { get; set; } = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Watchdog", "Logs");
    public string DataDirectory { get; set; } = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Watchdog", "Data");
}
