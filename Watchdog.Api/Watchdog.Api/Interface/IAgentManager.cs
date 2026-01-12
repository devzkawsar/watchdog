using Watchdog.Api.Data;
using Watchdog.Api.Services;

namespace Watchdog.Api.Interface;

public interface IAgentManager
{
    Task<IEnumerable<Agent>> GetAgents();
    Task<Agent?> GetAgent(string id);
    Task<Agent> RegisterAgent(AgentRegistration registration);
    Task<bool> UpdateAgentHeartbeat(string agentId);
    Task<bool> AssignApplicationToAgent(string agentId, string applicationId);
    Task<IEnumerable<Agent>> GetOnlineAgents();
    Task<IEnumerable<Application>> GetAgentApplications(string agentId);
}
