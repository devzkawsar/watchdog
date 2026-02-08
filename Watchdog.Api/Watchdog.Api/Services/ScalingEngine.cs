using Dapper;
using System.Data;
using  Watchdog.Api.Data;
using Watchdog.Api.Interface;

namespace  Watchdog.Api.Services;

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
    
    public async Task CheckAndScaleApplications()
    {
        var applications = await _applicationRepository.GetAll();
        
        foreach (var application in applications)
        {
            if (IsValidForScaling(application))
            {
                await CheckApplicationScaling(application);
            }
        }
    }
    
    public async Task ScaleApplication(string applicationId, int desiredInstances)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        // Update desired instances
        const string sql = @"
            UPDATE application 
            SET desired_instances = @DesiredInstances,
                updated = GETUTCDATE()
            WHERE id = @ApplicationId";
        
        await connection.ExecuteAsync(sql, new
        {
            ApplicationId = applicationId,
            DesiredInstances = desiredInstances
        });
        
        // Get current running instances
        var instances = await _applicationRepository.GetApplicationInstances(applicationId);
        var runningInstances = instances.Count(i => i.Status == "running");
        
        if (runningInstances < desiredInstances)
        {
            // Need to scale up
            await ScaleUpApplication(applicationId, desiredInstances - runningInstances);
        }
        else if (runningInstances > desiredInstances)
        {
            // Need to scale down
            await ScaleDownApplication(applicationId, runningInstances - desiredInstances);
        }
    }
    
    public async Task ScaleUpApplication(string applicationId, int instancesToAdd)
    {
        var application = await _applicationRepository.GetById(applicationId);
        if (application == null)
            return;
        
        _logger.LogInformation("Scaling up application {ApplicationId} by {InstancesToAdd} instances",
            applicationId, instancesToAdd);
        
        var agent = await GetAgentForScaling(applicationId);
        if (agent == null)
        {
            _logger.LogWarning("No online agents available for scaling up application {ApplicationId}", 
                applicationId);
            return;
        }
        
        for (int i = 0; i < instancesToAdd; i++)
        {
            var instanceId = $"{applicationId}-{agent.Id}-{Guid.NewGuid():N}";
            
            await _commandService.QueueSpawnCommand(
                agent.Id,
                application,
                instanceId,
                i + 1);
        }
    }
    
    public async Task ScaleDownApplication(string applicationId, int instancesToRemove)
    {
        _logger.LogInformation("Scaling down application {ApplicationId} by {InstancesToRemove} instances",
            applicationId, instancesToRemove);
        
        // Get running instances
        var instances = await _applicationRepository.GetApplicationInstances(applicationId);
        var runningInstances = instances
            .Where(i => i.Status == "running")
            .OrderBy(i => i.StartedAt) // Remove oldest first
            .Take(instancesToRemove);
        
        foreach (var instance in runningInstances)
        {
            if (string.IsNullOrEmpty(instance.AgentId))
            {
                _logger.LogWarning("Cannot scale down instance {InstanceId} as it has no assigned agent", instance.InstanceId);
                continue;
            }

            await _commandService.QueueKillCommand(
                instance.AgentId,
                instance.ApplicationId,
                instance.InstanceId);
        }
    }
    
    private async Task CheckApplicationScaling(Application application)
    {
        // Get current instances
        var instances = await _applicationRepository.GetApplicationInstances(application.Id);
        var runningInstances = instances.Count(i => i.Status == "running");
        
        // Check min instances
        if (runningInstances < application.MinInstances)
        {
            _logger.LogWarning(
                "Application {ApplicationId} has {Running}/{Min} instances. Scaling up...",
                application.Id, runningInstances, application.MinInstances);
            
            await ScaleUpApplication(application.Id, application.MinInstances - runningInstances);
        }
        
        // Check max instances
        else if (runningInstances > application.MaxInstances)
        {
            _logger.LogWarning(
                "Application {ApplicationId} has {Running}/{Max} instances. Scaling down...",
                application.Id, runningInstances, application.MaxInstances);
            
            await ScaleDownApplication(application.Id, runningInstances - application.MaxInstances);
        }
        
        // Check desired instances
        else if (runningInstances != application.DesiredInstances)
        {
            _logger.LogInformation(
                "Application {ApplicationId} has {Running}/{Desired} instances. Adjusting...",
                application.Id, runningInstances, application.DesiredInstances);
            
            await ScaleApplication(application.Id, application.DesiredInstances);
        }
    }

    private async Task<Agent?> GetAgentForScaling(string applicationId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string assignedSql = @"
            SELECT 
                a.id AS Id, 
                a.name AS Name, 
                a.ip_address AS IpAddress, 
                a.status AS Status,
                a.total_memory_mb AS TotalMemoryMB, 
                a.available_memory_mb AS AvailableMemoryMB, 
                a.cpu_cores AS CpuCores,
                a.tags AS Tags, 
                a.last_heartbeat AS LastHeartbeat, 
                a.created AS RegisteredAt
            FROM agent_application aa
            INNER JOIN agent a ON aa.agent_id = a.id
            WHERE aa.application_id = @ApplicationId
            AND a.status = 'online'
            AND a.last_heartbeat > DATEADD(MINUTE, -5, GETUTCDATE())
            ORDER BY a.available_memory_mb DESC";
        
        var assignedAgents = await connection.QueryAsync<Agent>(assignedSql, new { ApplicationId = applicationId });
        var assignedList = assignedAgents.ToList();
        // if (assignedList.Any())
        // {
        //     return assignedList.First();
        // }
        //
        // var agents = await _agentManager.GetOnlineAgents();
        // var agentsList = agents.ToList();
        if (!assignedList.Any())
        {
            return null;
        }
        
        var agent = assignedList.OrderByDescending(a => a.AvailableMemoryMB).First();
        
        await _agentManager.AssignApplicationToAgent(agent.Id, applicationId);
        
        return agent;
    }

    private bool IsValidForScaling(Application application)
    {
        if (string.IsNullOrWhiteSpace(application.ExecutablePath) || 
            application.ExecutablePath.Trim().Equals("string", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Skipping scaling for application {ApplicationId} because it has an invalid ExecutablePath: '{Path}'", 
                application.Id, application.ExecutablePath);
            return false;
        }

        if (string.IsNullOrWhiteSpace(application.Name) || 
            application.Name.Trim().Equals("string", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Skipping scaling for application {ApplicationId} because it has an invalid Name: '{Name}'", 
                application.Id, application.Name);
            return false;
        }

        return true;
    }
}