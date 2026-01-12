using Watchdog.Api.Data;
using Watchdog.Api.Services;

namespace Watchdog.Api.Interface;

public interface IApplicationManager
{
    Task<IEnumerable<Application>> GetApplications();
    Task<Application?> GetApplication(string id);
    Task<Application> CreateApplication(CreateApplicationRequest request);
    Task<bool> UpdateApplication(string id, UpdateApplicationRequest request);
    Task<bool> DeleteApplication(string id);
    Task<IEnumerable<ApplicationInstance>> GetApplicationInstances(string applicationId);
    Task<bool> UpdateInstanceStatus(string instanceId, string status, double? cpuPercent = null, double? memoryMB = null);
    Task<bool> StartApplication(string applicationId);
    Task<bool> StopApplication(string applicationId);
    Task<bool> RestartApplication(string applicationId);
}