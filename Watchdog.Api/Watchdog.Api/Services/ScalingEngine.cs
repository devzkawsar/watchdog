using Dapper;
using System.Data;
using  Watchdog.Api.Data;

namespace  Watchdog.Api.Services;

public interface IScalingEngine
{
    Task CheckAndScaleApplicationsAsync();
    Task ScaleApplicationAsync(string applicationId, int desiredInstances);
    Task ScaleDownApplicationAsync(string applicationId, int instancesToRemove);
    Task ScaleUpApplicationAsync(string applicationId, int instancesToAdd);
}

public class ScalingEngine : IScalingEngine
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IApplicationRepository _applicationRepository;
    private readonly IAgentManager _agentManager;
    private readonly ICommandService _commandService;
    private readonly ILogger<ScalingEngine> _logger;
    
    public ScalingEngine(
        IDbConnectionFactory connectionFactory,
        IApplicationRepository applicationRepository,
        IAgentManager agentManager,
        ICommandService commandService,
        ILogger<ScalingEngine> logger)
    {
        _connectionFactory = connectionFactory;
        _applicationRepository = applicationRepository;
        _agentManager = agentManager;
        _commandService = commandService;
        _logger = logger;
    }
    
    public async Task CheckAndScaleApplicationsAsync()
    {
        var applications = await _applicationRepository.GetAllAsync();
        
        foreach (var application in applications)
        {
            await CheckApplicationScalingAsync(application);
        }
    }
    
    public async Task ScaleApplicationAsync(string applicationId, int desiredInstances)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        // Update desired instances
        const string sql = @"
            UPDATE Applications 
            SET DesiredInstances = @DesiredInstances,
                UpdatedAt = GETUTCDATE()
            WHERE Id = @ApplicationId";
        
        await connection.ExecuteAsync(sql, new
        {
            ApplicationId = applicationId,
            DesiredInstances = desiredInstances
        });
        
        // Get current running instances
        var instances = await _applicationRepository.GetApplicationInstancesAsync(applicationId);
        var runningInstances = instances.Count(i => i.Status == "Running");
        
        if (runningInstances < desiredInstances)
        {
            // Need to scale up
            await ScaleUpApplicationAsync(applicationId, desiredInstances - runningInstances);
        }
        else if (runningInstances > desiredInstances)
        {
            // Need to scale down
            await ScaleDownApplicationAsync(applicationId, runningInstances - desiredInstances);
        }
    }
    
    public async Task ScaleUpApplicationAsync(string applicationId, int instancesToAdd)
    {
        var application = await _applicationRepository.GetByIdAsync(applicationId);
        if (application == null)
            return;
        
        _logger.LogInformation("Scaling up application {ApplicationId} by {InstancesToAdd} instances",
            applicationId, instancesToAdd);
        
        // Get online agents
        var agents = await _agentManager.GetOnlineAgentsAsync();
        if (!agents.Any())
        {
            _logger.LogWarning("No online agents available for scaling up application {ApplicationId}", 
                applicationId);
            return;
        }
        
        // Find agent with most available capacity
        var agent = agents.OrderByDescending(a => a.AvailableMemoryMB).First();
        
        for (int i = 0; i < instancesToAdd; i++)
        {
            var instanceId = $"{applicationId}-{agent.Id}-{Guid.NewGuid():N}";
            
            await _commandService.QueueSpawnCommandAsync(
                agent.Id,
                application,
                instanceId,
                i + 1);
        }
    }
    
    public async Task ScaleDownApplicationAsync(string applicationId, int instancesToRemove)
    {
        _logger.LogInformation("Scaling down application {ApplicationId} by {InstancesToRemove} instances",
            applicationId, instancesToRemove);
        
        // Get running instances
        var instances = await _applicationRepository.GetApplicationInstancesAsync(applicationId);
        var runningInstances = instances
            .Where(i => i.Status == "Running")
            .OrderBy(i => i.StartedAt) // Remove oldest first
            .Take(instancesToRemove);
        
        foreach (var instance in runningInstances)
        {
            await _commandService.QueueKillCommandAsync(
                instance.AgentId,
                instance.ApplicationId,
                instance.InstanceId);
        }
    }
    
    private async Task CheckApplicationScalingAsync(Application application)
    {
        // Get current instances
        var instances = await _applicationRepository.GetApplicationInstancesAsync(application.Id);
        var runningInstances = instances.Count(i => i.Status == "Running");
        
        // Check min instances
        if (runningInstances < application.MinInstances)
        {
            _logger.LogWarning(
                "Application {ApplicationId} has {Running}/{Min} instances. Scaling up...",
                application.Id, runningInstances, application.MinInstances);
            
            await ScaleUpApplicationAsync(application.Id, application.MinInstances - runningInstances);
        }
        
        // Check max instances
        else if (runningInstances > application.MaxInstances)
        {
            _logger.LogWarning(
                "Application {ApplicationId} has {Running}/{Max} instances. Scaling down...",
                application.Id, runningInstances, application.MaxInstances);
            
            await ScaleDownApplicationAsync(application.Id, runningInstances - application.MaxInstances);
        }
        
        // Check desired instances
        else if (runningInstances != application.DesiredInstances)
        {
            _logger.LogInformation(
                "Application {ApplicationId} has {Running}/{Desired} instances. Adjusting...",
                application.Id, runningInstances, application.DesiredInstances);
            
            await ScaleApplicationAsync(application.Id, application.DesiredInstances);
        }
    }
}