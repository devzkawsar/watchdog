using System.ComponentModel.DataAnnotations;
using  Watchdog.Api.Data;
using Watchdog.Api.Interface;
using Watchdog.Api.Request;

namespace Watchdog.Api.Services;



public class ApplicationManager : IApplicationManager
{
    private readonly IApplicationRepository _applicationRepository;
    private readonly IAgentManager _agentManager;
    private readonly ICommandService _commandService;
    private readonly ILogger<ApplicationManager> _logger;
    
    public ApplicationManager(
        IApplicationRepository applicationRepository,
        IAgentManager agentManager,
        ICommandService commandService,
        ILogger<ApplicationManager> logger)
    {
        _applicationRepository = applicationRepository;
        _agentManager = agentManager;
        _commandService = commandService;
        _logger = logger;
    }
    
    public async Task<IEnumerable<Application>> GetApplications()
    {
        return await _applicationRepository.GetAll();
    }
    
    public async Task<Application?> GetApplication(string id)
    {
        return await _applicationRepository.GetById(id);
    }
    
    public async Task<Application> CreateApplication(CreateApplicationRequest request)
    {
        var application = new Application
        {
            Id = request.Id ?? Guid.NewGuid().ToString(),
            Name = request.Name,
            DisplayName = request.DisplayName ?? request.Name,
            ExecutablePath = request.ExecutablePath,
            Arguments = request.Arguments ?? string.Empty,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            ApplicationType = request.ApplicationType,
            HeartbeatTimeout = request.HeartbeatTimeout,
            DesiredInstances = request.DesiredInstances,
            MinInstances = request.MinInstances,
            MaxInstances = request.MaxInstances,
            EnvironmentVariables = request.EnvironmentVariables ?? new Dictionary<string, string>(),
            AutoStart = request.AutoStart
        };
        
        ValidateApplication(application);
        
        await _applicationRepository.Create(application);
        
        _logger.LogInformation("Created application {ApplicationId} ({Name})", 
            application.Id, application.Name);
        
        // Auto-start if configured
        if (application.AutoStart && application.DesiredInstances > 0)
        {
            _ = Task.Run(async () => await StartApplication(application.Id));
        }
        
        return application;
    }
    
    public async Task<bool> UpdateApplication(string id, UpdateApplicationRequest request)
    {
        var existing = await _applicationRepository.GetById(id);
        if (existing == null)
            return false;
        
        existing.Name = request.Name;
        existing.DisplayName = request.DisplayName ?? existing.DisplayName;
        existing.ExecutablePath = request.ExecutablePath;
        existing.Arguments = request.Arguments ?? existing.Arguments;
        existing.WorkingDirectory = request.WorkingDirectory ?? existing.WorkingDirectory;
        existing.ApplicationType = request.ApplicationType;
        existing.HeartbeatTimeout = request.HeartbeatTimeout;
        existing.DesiredInstances = request.DesiredInstances;
        existing.MinInstances = request.MinInstances;
        existing.MaxInstances = request.MaxInstances;
        existing.EnvironmentVariables = request.EnvironmentVariables ?? existing.EnvironmentVariables;
        existing.AutoStart = request.AutoStart;
        
        ValidateApplication(existing);
        
        await _applicationRepository.Update(existing);
        
        _logger.LogInformation("Updated application {ApplicationId}", id);
        
        // Trigger scaling if instance count changed
        if (existing.DesiredInstances != request.DesiredInstances)
        {
            await TriggerScaling(existing);
        }
        
        return true;
    }
    
    public async Task<bool> DeleteApplication(string id)
    {
        // First stop all instances
        await StopApplication(id);
        
        // Then delete from database
        var result = await _applicationRepository.Delete(id);
        
        if (result > 0)
        {
            _logger.LogInformation("Deleted application {ApplicationId}", id);
            return true;
        }
        
        return false;
    }
    
    public async Task<IEnumerable<ApplicationInstance>> GetApplicationInstances(string applicationId)
    {
        return await _applicationRepository.GetApplicationInstances(applicationId);
    }
    
    public async Task<bool> UpdateInstanceStatus(string instanceId, string status, double? cpuPercent = null, double? memoryMB = null, int? processId = null)
    {
        var updated = await _applicationRepository.UpdateInstanceStatus(instanceId, status, cpuPercent, memoryMB, processId);
        return updated > 0;
    }
    
    public async Task<bool> StartApplication(string applicationId)
    {
        var application = await _applicationRepository.GetById(applicationId);
        if (application == null)
            return false;
        
        _logger.LogInformation("Starting application {ApplicationId} with {DesiredInstances} instances",
            applicationId, application.DesiredInstances);
        
        // Distribute instances across available agents
        // var agents = await _agentManager.GetOnlineAgents();
        // if (!agents.Any())
        // {
        //     _logger.LogError("No online agents available to start application {ApplicationId}", applicationId);
        //     return false;
        // }
        //
        // var instancesPerAgent = CalculateInstanceDistribution(application.DesiredInstances, agents.Count());
        // var instanceIndex = 0;
        //
        // foreach (var agent in agents)
        // {
        //     var instances = instancesPerAgent[instanceIndex];
        //     for (int i = 0; i < instances; i++)
        //     {
        //         var instanceId = $"{applicationId}-{agent.Id}-{Guid.NewGuid():N}";
        //         
        //         // Queue spawn command
        //         await _commandService.QueueSpawnCommand(
        //             agent.Id,
        //             application,
        //             instanceId,
        //             i + 1);
        //     }
        //     instanceIndex++;
        // }
        
        return true;
    }
    
    public async Task<bool> StopApplication(string applicationId)
    {
        var instances = await _applicationRepository.GetApplicationInstances(applicationId);
        var runningInstances = instances.Where(i => i.Status == "running");
        
        _logger.LogInformation("Stopping {Count} instances of application {ApplicationId}",
            runningInstances.Count(), applicationId);
        
        foreach (var instance in runningInstances)
        {
            if (string.IsNullOrEmpty(instance.AgentId))
            {
                _logger.LogWarning("Cannot stop instance {InstanceId} as it has no assigned agent. Marking as stopped.", instance.InstanceId);
                await _applicationRepository.UpdateInstanceStatus(instance.InstanceId, "stopped");
                continue;
            }

            // Queue kill command
            await _commandService.QueueKillCommand(
                instance.AgentId,
                instance.ApplicationId,
                instance.InstanceId);
        }
        
        return true;
    }
    
    public async Task<bool> RestartApplication(string applicationId)
    {
        // Stop first
        await StopApplication(applicationId);
        
        // Wait a bit
        await Task.Delay(5000);
        
        // Start again
        return await StartApplication(applicationId);
    }
    
    private Dictionary<int, int> CalculateInstanceDistribution(int totalInstances, int agentCount)
    {
        var distribution = new Dictionary<int, int>();
        int baseCount = totalInstances / agentCount;
        int remainder = totalInstances % agentCount;
        
        for (int i = 0; i < agentCount; i++)
        {
            distribution[i] = baseCount + (i < remainder ? 1 : 0);
        }
        
        return distribution;
    }
    
    private async Task TriggerScaling(Application application)
    {
        var currentInstances = await _applicationRepository.GetApplicationInstances(application.Id);
        var runningInstances = currentInstances.Count(i => i.Status == "running");
        
        if (runningInstances < application.DesiredInstances)
        {
            // Need to scale up
            var needed = application.DesiredInstances - runningInstances;
            _logger.LogInformation(
                "Scaling up application {ApplicationId}: {Needed} more instances needed",
                application.Id, needed);
            
            await StartApplication(application.Id);
        }
        else if (runningInstances > application.DesiredInstances)
        {
            // Need to scale down
            var excess = runningInstances - application.DesiredInstances;
            _logger.LogInformation(
                "Scaling down application {ApplicationId}: {Excess} instances to remove",
                application.Id, excess);
            
            // Remove oldest instances
            var instancesToRemove = currentInstances
                .Where(i => i.Status == "running")
                .OrderBy(i => i.StartedAt)
                .Take(excess);
            
            foreach (var instance in instancesToRemove)
            {
                if (string.IsNullOrEmpty(instance.AgentId))
                {
                    _logger.LogWarning("Cannot scale down instance {InstanceId} as it has no assigned agent. Marking as stopped.", instance.InstanceId);
                    await _applicationRepository.UpdateInstanceStatus(instance.InstanceId, "stopped");
                    continue;
                }

                await _commandService.QueueKillCommand(
                    instance.AgentId,
                    instance.ApplicationId,
                    instance.InstanceId);
            }
        }
    }

    private void ValidateApplication(Application application)
    {
        if (string.IsNullOrWhiteSpace(application.ExecutablePath) || 
            application.ExecutablePath.Trim().Equals("string", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("ExecutablePath cannot be empty or a placeholder value like 'string'.");
        }

        if (string.IsNullOrWhiteSpace(application.Name) || 
            application.Name.Trim().Equals("string", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("Application Name cannot be empty or a placeholder value like 'string'.");
        }
    }
}



