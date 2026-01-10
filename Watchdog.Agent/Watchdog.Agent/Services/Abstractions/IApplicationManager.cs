using Watchdog.Agent.Models;
using Watchdog.Agent.Protos;

namespace Watchdog.Agent.Services;

public interface IApplicationManager
{
    Task InitializeAsync();
    Task<List<ApplicationConfig>> GetAssignedApplicationsAsync();
    Task<ApplicationConfig?> GetApplicationConfigAsync(string applicationId);
    Task<bool> ValidateApplicationConfigAsync(ApplicationConfig config);
    Task ReportToOrchestratorAsync();
    Task<ManagedApplication> CreateApplicationInstanceAsync(SpawnCommand command);
    Task<ManagedApplication?> GetApplicationInstanceAsync(string instanceId);
    Task<List<ManagedApplication>> GetAllInstancesAsync();
    Task UpdateInstanceStatusAsync(string instanceId, ApplicationStatus status, int? processId = null, List<PortMapping>? ports = null);
    Task RemoveInstanceAsync(string instanceId);
    Task<List<ApplicationInstanceStatus>> GetInstanceStatusesAsync();
    Task UpdateApplicationsFromControlPlaneAsync(List<ApplicationAssignment> assignments);
    Task<bool> ShouldRestartInstanceAsync(ManagedApplication instance);
    Task IncrementRestartCountAsync(string instanceId);
}
