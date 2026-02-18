using Dapper;
using System.Data;
using System.Text.Json;
using Watchdog.Api.Data;
using Watchdog.Api.Interface;

namespace Watchdog.Api.Services;


public class AgentManager : IAgentManager
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<AgentManager> _logger;
    
    public AgentManager(
        IDbConnectionFactory connectionFactory,
        ILogger<AgentManager> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }
    
    /// <summary>
    /// Retrieves a list of agents.
    /// </summary>
    /// <returns>A list of agents.</returns>
    public async Task<IEnumerable<Agent>> GetAgents()
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                id AS Id, 
                name AS Name, 
                ip_address AS IpAddress, 
                status AS Status,
                total_memory_mb AS TotalMemoryMB, 
                available_memory_mb AS AvailableMemoryMB, 
                cpu_cores AS CpuCores,
                tags AS Tags, 
                last_heartbeat AS LastHeartbeat, 
                created AS Created,
                updated AS Updated,
                created_by AS CreatedBy,
                updated_by AS UpdatedBy
            FROM agent
            ORDER BY status DESC, name";
        
        return await connection.QueryAsync<Agent>(sql);
    }
    
    public async Task<Agent?> GetAgent(string id)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                id AS Id, 
                name AS Name, 
                ip_address AS IpAddress, 
                status AS Status,
                total_memory_mb AS TotalMemoryMB, 
                available_memory_mb AS AvailableMemoryMB, 
                cpu_cores AS CpuCores,
                tags AS Tags, 
                last_heartbeat AS LastHeartbeat, 
                created AS Created,
                updated AS Updated,
                created_by AS CreatedBy,
                updated_by AS UpdatedBy
            FROM agent
            WHERE id = @Id";
        
        return await connection.QueryFirstOrDefaultAsync<Agent>(sql, new { Id = id });
    }
    
    public async Task<Agent> RegisterAgent(AgentRegistration registration)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        var agent = new Agent
        {
            Id = registration.AgentId,
            Name = registration.AgentName,
            IpAddress = registration.IpAddress,
            Status = "Online",
            TotalMemoryMB = registration.TotalMemoryMB,
            AvailableMemoryMB = registration.TotalMemoryMB,
            CpuCores = registration.CpuCores,
            OsVersion = registration.OsVersion,
            Tags = "[]", // Empty JSON array
            LastHeartbeat = DateTime.UtcNow
        };
        
        const string sql = @"
            MERGE agent AS target
            USING (SELECT @Id AS Id) AS source
            ON target.id = source.Id
            WHEN MATCHED THEN
                UPDATE SET 
                    name = @Name,
                    ip_address = @IpAddress,
                    status = 'online',
                    total_memory_mb = @TotalMemoryMB,
                    available_memory_mb = @AvailableMemoryMB,
                    cpu_cores = @CpuCores,
                    os_version = @OsVersion,
                    tags = @Tags,
                    last_heartbeat = @LastHeartbeat,
                    updated = GETUTCDATE(),
                    updated_by = NULL
            WHEN NOT MATCHED THEN
                INSERT (id, name, ip_address, status, total_memory_mb, 
                        available_memory_mb, cpu_cores, os_version, tags, last_heartbeat, created, created_by)
                VALUES (@Id, @Name, @IpAddress, 'online', @TotalMemoryMB, 
                        @AvailableMemoryMB, @CpuCores, @OsVersion, @Tags, @LastHeartbeat, GETUTCDATE(), NULL);";
        
        await connection.ExecuteAsync(sql, agent);
        
        _logger.LogInformation("Registered agent {AgentId} ({Name}) from {IpAddress}",
            agent.Id, agent.Name, agent.IpAddress);
        
        return agent;
    }
    
    public async Task<bool> UpdateAgentHeartbeat(string agentId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            UPDATE agent 
            SET last_heartbeat = GETUTCDATE(),
                status = 'online',
                updated = GETUTCDATE()
            WHERE id = @AgentId";
        
        var result = await connection.ExecuteAsync(sql, new { AgentId = agentId });
        
        if (result > 0)
        {
            // Mark offline agents
            await MarkOfflineAgents(connection);
            return true;
        }
        
        return false;
    }
    
    public async Task<bool> AssignApplicationToAgent(string agentId, string applicationId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        // Check if agent exists
        var agentExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM agent WHERE id = @Id", new { Id = agentId }) > 0;
        
        if (!agentExists)
        {
            _logger.LogWarning("Failed to assign application: Agent {AgentId} not found", agentId);
            return false;
        }

        // Check if application exists
        var applicationExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM application WHERE id = @Id", new { Id = applicationId }) > 0;
        
        if (!applicationExists)
        {
            _logger.LogWarning("Failed to assign application: Application {ApplicationId} not found", applicationId);
            return false;
        }

        const string sql = @"
            IF NOT EXISTS (SELECT 1 FROM agent_application WHERE agent_id = @AgentId AND application_id = @ApplicationId)
            BEGIN
                INSERT INTO agent_application (agent_id, application_id, assigned_at)
                VALUES (@AgentId, @ApplicationId, GETUTCDATE());
            END";
        
        await connection.ExecuteAsync(sql, new
        {
            AgentId = agentId,
            ApplicationId = applicationId
        });
        
        _logger.LogInformation("Assigned application {ApplicationId} to agent {AgentId}",
            applicationId, agentId);
        
        return true;
    }
    
    public async Task<IEnumerable<Agent>> GetOnlineAgents()
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                id AS Id, 
                name AS Name, 
                ip_address AS IpAddress, 
                status AS Status,
                total_memory_mb AS TotalMemoryMB, 
                available_memory_mb AS AvailableMemoryMB, 
                cpu_cores AS CpuCores,
                tags AS Tags, 
                last_heartbeat AS LastHeartbeat, 
                created AS Created,
                updated AS Updated,
                created_by AS CreatedBy,
                updated_by AS UpdatedBy
            FROM agent
            WHERE status = 'online'
            AND last_heartbeat > DATEADD(MINUTE, -5, GETUTCDATE())
            ORDER BY available_memory_mb DESC";
        
        return await connection.QueryAsync<Agent>(sql);
    }
    
    public async Task<IEnumerable<Application>> GetAgentApplications(string agentId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                a.id AS Id, 
                a.name AS Name, 
                a.display_name AS DisplayName, 
                a.executable_path AS ExecutablePath, 
                a.arguments AS Arguments, 
                a.working_directory AS WorkingDirectory,
                a.application_type AS ApplicationType, 
                a.health_check_interval AS HealthCheckInterval,
                a.desired_instances AS DesiredInstances, 
                a.min_instances AS MinInstances, 
                a.max_instances AS MaxInstances,
                a.environment_variables AS EnvironmentVariablesJson, 
                a.auto_start AS AutoStart,
                a.created AS CreatedAt, 
                a.updated AS UpdatedAt
            FROM application a
            INNER JOIN agent_application aa ON a.id = aa.application_id
            WHERE aa.agent_id = @AgentId
            ORDER BY a.name";
        
        var applications = await connection.QueryAsync<Application>(sql, new { AgentId = agentId });
        
        // Deserialize JSON fields
        foreach (var app in applications)
        {
            if (!string.IsNullOrEmpty(app.EnvironmentVariablesJson))
            {
                app.EnvironmentVariables = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    app.EnvironmentVariablesJson) ?? new Dictionary<string, string>();
            }
        }
        
        return applications;
    }

    private async Task MarkOfflineAgents(IDbConnection connection)
    {
        const string sql = @"
            UPDATE agent 
            SET status = 'offline',
                updated = GETUTCDATE()
            WHERE last_heartbeat < DATEADD(MINUTE, -5, GETUTCDATE())
            AND status = 'online'";
        
        var offlineCount = await connection.ExecuteAsync(sql);
        
        if (offlineCount > 0)
        {
            _logger.LogWarning("Marked {Count} agents as offline", offlineCount);
        }
    }
}

public class Agent
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Status { get; set; } = "offline"; // Online, Offline, Draining
    public int? TotalMemoryMB { get; set; }
    public int? AvailableMemoryMB { get; set; }
    public int? CpuCores { get; set; }
    public string OsVersion { get; set; } = string.Empty;
    public string Tags { get; set; } = "[]"; // JSON, Renamed from AvailablePorts
    public DateTime? LastHeartbeat { get; set; }
    public DateTime Created { get; set; }
    public DateTime? Updated { get; set; }
    public long? CreatedBy { get; set; }
    public long? UpdatedBy { get; set; }
}

public class AgentRegistration
{
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public int TotalMemoryMB { get; set; }
    public int CpuCores { get; set; }
    public string OsVersion { get; set; } = string.Empty;
}