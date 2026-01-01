using Watchdog.Api.Data.Models;

namespace Watchdog.Api.Data;

public interface IDbRepository
{
    // ========== APPLICATIONS ==========
    Task<Application?> GetApplicationAsync(string id);
    Task<IEnumerable<Application>> GetAllApplicationsAsync();
    Task<string> CreateApplicationAsync(Application app);
    Task<bool> UpdateApplicationAsync(Application app);
    Task<bool> DeleteApplicationAsync(string id);
    
    // ========== AGENTS ==========
    Task<Agent?> GetAgentAsync(string id);
    Task<IEnumerable<Agent>> GetAllAgentsAsync();
    Task<string> CreateAgentAsync(Agent agent);
    Task<bool> UpdateAgentAsync(Agent agent);
    Task<bool> DeleteAgentAsync(string id);
    Task UpdateAgentHeartbeatAsync(string agentId);
    
    // ========== APP INSTANCES ==========
    Task<IEnumerable<AppInstance>> GetAppInstancesAsync(string appId);
    Task<IEnumerable<AppInstance>> GetAgentInstancesAsync(string agentId);
    Task<IEnumerable<AppInstance>> GetAllInstancesAsync();
    Task<string> CreateAppInstanceAsync(AppInstance instance);
    Task<bool> UpdateAppInstanceAsync(AppInstance instance);
    Task<bool> DeleteAppInstanceAsync(string id);
    Task<bool> UpdateInstanceHeartbeatAsync(string instanceId);
    
    // ========== SCALING CONFIG ==========
    Task<ScalingConfig?> GetScalingConfigAsync(string appId);
    Task<bool> UpdateScalingConfigAsync(ScalingConfig config);
    
    // ========== ASSIGNMENT & LOAD ==========
    Task<string> AssignAppToAgentAsync(string agentId, string appId, int instanceCount = 1);
    Task<bool> RemoveAppFromAgentAsync(string agentId, string appId);
    Task<Dictionary<string, int>> GetAgentLoadAsync();
    
    // ========== HEALTH & STATISTICS ==========
    Task<int> GetRunningInstanceCountAsync(string appId);
    Task<IEnumerable<AppInstance>> GetUnhealthyInstancesAsync(int minutesThreshold = 5);
    Task<ApplicationStats> GetApplicationStatsAsync(string appId);
}