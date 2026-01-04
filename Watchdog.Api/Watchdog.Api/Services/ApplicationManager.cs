using System.ComponentModel.DataAnnotations;
using  Watchdog.Api.Data;

namespace Watchdog.Api.Services;

public interface IApplicationManager
{
    Task<IEnumerable<Application>> GetApplicationsAsync();
    Task<Application?> GetApplicationAsync(string id);
    Task<Application> CreateApplicationAsync(CreateApplicationRequest request);
    Task<bool> UpdateApplicationAsync(string id, UpdateApplicationRequest request);
    Task<bool> DeleteApplicationAsync(string id);
    Task<IEnumerable<ApplicationInstance>> GetApplicationInstancesAsync(string applicationId);
    Task<bool> StartApplicationAsync(string applicationId);
    Task<bool> StopApplicationAsync(string applicationId);
    Task<bool> RestartApplicationAsync(string applicationId);
}

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
    
    public async Task<IEnumerable<Application>> GetApplicationsAsync()
    {
        return await _applicationRepository.GetAllAsync();
    }
    
    public async Task<Application?> GetApplicationAsync(string id)
    {
        return await _applicationRepository.GetByIdAsync(id);
    }
    
    public async Task<Application> CreateApplicationAsync(CreateApplicationRequest request)
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
            HealthCheckUrl = request.HealthCheckUrl ?? string.Empty,
            HealthCheckInterval = request.HealthCheckInterval,
            DesiredInstances = request.DesiredInstances,
            MinInstances = request.MinInstances,
            MaxInstances = request.MaxInstances,
            PortRequirements = request.PortRequirements ?? new List<PortRequirement>(),
            EnvironmentVariables = request.EnvironmentVariables ?? new Dictionary<string, string>(),
            AutoStart = request.AutoStart
        };
        
        await _applicationRepository.CreateAsync(application);
        
        _logger.LogInformation("Created application {ApplicationId} ({Name})", 
            application.Id, application.Name);
        
        // Auto-start if configured
        if (application.AutoStart && application.DesiredInstances > 0)
        {
            _ = Task.Run(async () => await StartApplicationAsync(application.Id));
        }
        
        return application;
    }
    
    public async Task<bool> UpdateApplicationAsync(string id, UpdateApplicationRequest request)
    {
        var existing = await _applicationRepository.GetByIdAsync(id);
        if (existing == null)
            return false;
        
        existing.Name = request.Name;
        existing.DisplayName = request.DisplayName ?? existing.DisplayName;
        existing.ExecutablePath = request.ExecutablePath;
        existing.Arguments = request.Arguments ?? existing.Arguments;
        existing.WorkingDirectory = request.WorkingDirectory ?? existing.WorkingDirectory;
        existing.ApplicationType = request.ApplicationType;
        existing.HealthCheckUrl = request.HealthCheckUrl ?? existing.HealthCheckUrl;
        existing.HealthCheckInterval = request.HealthCheckInterval;
        existing.DesiredInstances = request.DesiredInstances;
        existing.MinInstances = request.MinInstances;
        existing.MaxInstances = request.MaxInstances;
        existing.PortRequirements = request.PortRequirements ?? existing.PortRequirements;
        existing.EnvironmentVariables = request.EnvironmentVariables ?? existing.EnvironmentVariables;
        existing.AutoStart = request.AutoStart;
        
        await _applicationRepository.UpdateAsync(existing);
        
        _logger.LogInformation("Updated application {ApplicationId}", id);
        
        // Trigger scaling if instance count changed
        if (existing.DesiredInstances != request.DesiredInstances)
        {
            await TriggerScalingAsync(existing);
        }
        
        return true;
    }
    
    public async Task<bool> DeleteApplicationAsync(string id)
    {
        // First stop all instances
        await StopApplicationAsync(id);
        
        // Then delete from database
        var result = await _applicationRepository.DeleteAsync(id);
        
        if (result > 0)
        {
            _logger.LogInformation("Deleted application {ApplicationId}", id);
            return true;
        }
        
        return false;
    }
    
    public async Task<IEnumerable<ApplicationInstance>> GetApplicationInstancesAsync(string applicationId)
    {
        return await _applicationRepository.GetApplicationInstancesAsync(applicationId);
    }
    
    public async Task<bool> StartApplicationAsync(string applicationId)
    {
        var application = await _applicationRepository.GetByIdAsync(applicationId);
        if (application == null)
            return false;
        
        _logger.LogInformation("Starting application {ApplicationId} with {DesiredInstances} instances",
            applicationId, application.DesiredInstances);
        
        // Distribute instances across available agents
        var agents = await _agentManager.GetOnlineAgentsAsync();
        if (!agents.Any())
        {
            _logger.LogError("No online agents available to start application {ApplicationId}", applicationId);
            return false;
        }
        
        var instancesPerAgent = CalculateInstanceDistribution(application.DesiredInstances, agents.Count());
        var instanceIndex = 0;
        
        foreach (var agent in agents)
        {
            var instances = instancesPerAgent[instanceIndex];
            for (int i = 0; i < instances; i++)
            {
                var instanceId = $"{applicationId}-{agent.Id}-{Guid.NewGuid():N}";
                
                // Queue spawn command
                await _commandService.QueueSpawnCommandAsync(
                    agent.Id,
                    application,
                    instanceId,
                    i + 1);
            }
            instanceIndex++;
        }
        
        return true;
    }
    
    public async Task<bool> StopApplicationAsync(string applicationId)
    {
        var instances = await _applicationRepository.GetApplicationInstancesAsync(applicationId);
        var runningInstances = instances.Where(i => i.Status == "Running");
        
        _logger.LogInformation("Stopping {Count} instances of application {ApplicationId}",
            runningInstances.Count(), applicationId);
        
        foreach (var instance in runningInstances)
        {
            // Queue kill command
            await _commandService.QueueKillCommandAsync(
                instance.AgentId,
                instance.ApplicationId,
                instance.InstanceId);
        }
        
        return true;
    }
    
    public async Task<bool> RestartApplicationAsync(string applicationId)
    {
        // Stop first
        await StopApplicationAsync(applicationId);
        
        // Wait a bit
        await Task.Delay(5000);
        
        // Start again
        return await StartApplicationAsync(applicationId);
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
    
    private async Task TriggerScalingAsync(Application application)
    {
        var currentInstances = await _applicationRepository.GetApplicationInstancesAsync(application.Id);
        var runningInstances = currentInstances.Count(i => i.Status == "Running");
        
        if (runningInstances < application.DesiredInstances)
        {
            // Need to scale up
            var needed = application.DesiredInstances - runningInstances;
            _logger.LogInformation(
                "Scaling up application {ApplicationId}: {Needed} more instances needed",
                application.Id, needed);
            
            await StartApplicationAsync(application.Id);
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
                .Where(i => i.Status == "Running")
                .OrderBy(i => i.StartedAt)
                .Take(excess);
            
            foreach (var instance in instancesToRemove)
            {
                await _commandService.QueueKillCommandAsync(
                    instance.AgentId,
                    instance.ApplicationId,
                    instance.InstanceId);
            }
        }
    }
}

public class CreateApplicationRequest
{
    public string? Id { get; set; }
    
    [Required]
    public string Name { get; set; } = string.Empty;
    
    public string? DisplayName { get; set; }
    
    [Required]
    public string ExecutablePath { get; set; } = string.Empty;
    
    public string? Arguments { get; set; }
    
    public string? WorkingDirectory { get; set; }
    
    [Range(0, 2)]
    public int ApplicationType { get; set; } // 0=Console, 1=Service, 2=IIS
    
    public string? HealthCheckUrl { get; set; }
    
    [Range(5, 300)]
    public int HealthCheckInterval { get; set; } = 30;
    
    [Range(0, 100)]
    public int DesiredInstances { get; set; } = 1;
    
    [Range(0, 100)]
    public int MinInstances { get; set; } = 1;
    
    [Range(1, 100)]
    public int MaxInstances { get; set; } = 5;
    
    public List<PortRequirement>? PortRequirements { get; set; }
    
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    
    public bool AutoStart { get; set; } = true;
}

public class UpdateApplicationRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;
    
    public string? DisplayName { get; set; }
    
    [Required]
    public string ExecutablePath { get; set; } = string.Empty;
    
    public string? Arguments { get; set; }
    
    public string? WorkingDirectory { get; set; }
    
    [Range(0, 2)]
    public int ApplicationType { get; set; }
    
    public string? HealthCheckUrl { get; set; }
    
    [Range(5, 300)]
    public int HealthCheckInterval { get; set; }
    
    [Range(0, 100)]
    public int DesiredInstances { get; set; }
    
    [Range(0, 100)]
    public int MinInstances { get; set; }
    
    [Range(1, 100)]
    public int MaxInstances { get; set; }
    
    public List<PortRequirement>? PortRequirements { get; set; }
    
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    
    public bool AutoStart { get; set; }
}