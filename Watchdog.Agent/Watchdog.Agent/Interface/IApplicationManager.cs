using Watchdog.Agent.Models;
using Watchdog.Agent.Protos;

namespace Watchdog.Agent.Interface;

public interface IApplicationManager
{
    Task Initialize();
    Task<List<ApplicationConfig>> GetAssignedApplications();
    Task<ApplicationConfig?> GetApplicationConfig(string applicationId);
    Task<bool> ValidateApplicationConfig(ApplicationConfig config);
    Task ReportToOrchestrator();
    Task<ManagedApplication> CreateApplicationInstance(SpawnCommand command);
    Task<ManagedApplication?> GetApplicationInstance(string instanceId);
    Task<List<ManagedApplication>> GetAllInstances();
    Task UpdateInstanceStatus(string instanceId, ApplicationStatus status, int? processId = null, List<PortMapping>? ports = null);
    Task RemoveInstance(string instanceId);
    Task<List<ApplicationInstanceStatus>> GetInstanceStatuses();
    Task UpdateApplicationsFromControlPlane(List<ApplicationAssignment> assignments);
    Task<bool> ShouldRestartInstance(ManagedApplication instance);
    Task IncrementRestartCount(string instanceId);
}
