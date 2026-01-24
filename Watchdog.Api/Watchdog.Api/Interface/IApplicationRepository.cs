using Watchdog.Api.Data;

namespace Watchdog.Api.Interface;

public interface IApplicationRepository
{
    Task<IEnumerable<Application>> GetAll();
    Task<Application?> GetById(string id);
    Task<int> Create(Application application);
    Task<int> Update(Application application);
    Task<int> Delete(string id);
    Task<IEnumerable<ApplicationInstance>> GetApplicationInstances(string applicationId);
    Task<IEnumerable<ApplicationInstance>> GetActiveInstancesForAgent(string agentId);
    Task<int> UpdateInstanceStatus(string instanceId, string status, 
        double? cpuPercent = null, double? memoryMB = null, int? processId = null);
}