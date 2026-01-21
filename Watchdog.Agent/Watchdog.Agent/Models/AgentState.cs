namespace Watchdog.Agent.Models;

public class AgentState
{
    public List<ApplicationConfig> Applications { get; set; } = new();
    public List<ManagedApplication> Instances { get; set; } = new();
    public DateTime SavedAt { get; set; }
}