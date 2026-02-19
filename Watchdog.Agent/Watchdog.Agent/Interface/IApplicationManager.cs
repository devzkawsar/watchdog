using Watchdog.Agent.Models;
using Watchdog.Api.Protos;
using ApplicationStatus = Watchdog.Agent.Enums.ApplicationStatus;

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
    Task UpdateInstanceStatus(string instanceId, ApplicationStatus status, int? processId = null, int? assignedPort = null, DateTime? reattachedAt = null);
    Task RemoveInstance(string instanceId);
    Task<List<Watchdog.Api.Protos.ApplicationStatus>> GetInstanceStatuses();
    Task UpdateApplicationsFromControlPlane(List<ApplicationAssignment> assignments);
    Task<bool> ShouldRestartInstance(ManagedApplication instance);
    Task IncrementRestartCount(string instanceId);
    Task NotifyInstanceStopped(string instanceId, int exitCode, string reason);
    Task SyncInstancesFromApi(List<Watchdog.Api.Protos.ApplicationStatus> activeInstances);
}
