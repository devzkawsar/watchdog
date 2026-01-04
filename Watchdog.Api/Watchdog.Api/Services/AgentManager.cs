using Dapper;
using System.Data;
using System.Text.Json;
using Watchdog.Api.Data;

namespace Watchdog.Api.Services;

public interface IAgentManager
{
    Task<IEnumerable<Agent>> GetAgentsAsync();
    Task<Agent?> GetAgentAsync(string id);
    Task<Agent> RegisterAgentAsync(AgentRegistration registration);
    Task<bool> UpdateAgentHeartbeatAsync(string agentId);
    Task<bool> AssignApplicationToAgentAsync(string agentId, string applicationId);
    Task<IEnumerable<Agent>> GetOnlineAgentsAsync();
    Task<IEnumerable<Application>> GetAgentApplicationsAsync(string agentId);
}

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
    
    public async Task<IEnumerable<Agent>> GetAgentsAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                Id, Name, IpAddress, Status,
                TotalMemoryMB, AvailableMemoryMB, CpuCores,
                AvailablePorts, LastHeartbeat, RegisteredAt
            FROM Agents
            ORDER BY Status DESC, Name";
        
        return await connection.QueryAsync<Agent>(sql);
    }
    
    public async Task<Agent?> GetAgentAsync(string id)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                Id, Name, IpAddress, Status,
                TotalMemoryMB, AvailableMemoryMB, CpuCores,
                AvailablePorts, LastHeartbeat, RegisteredAt
            FROM Agents
            WHERE Id = @Id";
        
        return await connection.QueryFirstOrDefaultAsync<Agent>(sql, new { Id = id });
    }
    
    public async Task<Agent> RegisterAgentAsync(AgentRegistration registration)
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
            AvailablePorts = "[]", // Empty JSON array
            LastHeartbeat = DateTime.UtcNow
        };
        
        const string sql = @"
            MERGE Agents AS target
            USING (SELECT @Id AS Id) AS source
            ON target.Id = source.Id
            WHEN MATCHED THEN
                UPDATE SET 
                    Name = @Name,
                    IpAddress = @IpAddress,
                    Status = 'Online',
                    TotalMemoryMB = @TotalMemoryMB,
                    AvailableMemoryMB = @AvailableMemoryMB,
                    CpuCores = @CpuCores,
                    AvailablePorts = @AvailablePorts,
                    LastHeartbeat = @LastHeartbeat
            WHEN NOT MATCHED THEN
                INSERT (Id, Name, IpAddress, Status, TotalMemoryMB, 
                        AvailableMemoryMB, CpuCores, AvailablePorts, LastHeartbeat, RegisteredAt)
                VALUES (@Id, @Name, @IpAddress, 'Online', @TotalMemoryMB, 
                        @AvailableMemoryMB, @CpuCores, @AvailablePorts, @LastHeartbeat, GETUTCDATE());";
        
        await connection.ExecuteAsync(sql, agent);
        
        _logger.LogInformation("Registered agent {AgentId} ({Name}) from {IpAddress}",
            agent.Id, agent.Name, agent.IpAddress);
        
        return agent;
    }
    
    public async Task<bool> UpdateAgentHeartbeatAsync(string agentId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            UPDATE Agents 
            SET LastHeartbeat = GETUTCDATE(),
                Status = 'Online'
            WHERE Id = @AgentId";
        
        var result = await connection.ExecuteAsync(sql, new { AgentId = agentId });
        
        if (result > 0)
        {
            // Mark offline agents
            await MarkOfflineAgentsAsync(connection);
            return true;
        }
        
        return false;
    }
    
    public async Task<bool> AssignApplicationToAgentAsync(string agentId, string applicationId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            MERGE AgentApplications AS target
            USING (SELECT @AgentId AS AgentId, @ApplicationId AS ApplicationId) AS source
            ON target.AgentId = source.AgentId AND target.ApplicationId = source.ApplicationId
            WHEN NOT MATCHED THEN
                INSERT (AgentId, ApplicationId, AssignedInstances)
                VALUES (@AgentId, @ApplicationId, 0);";
        
        var result = await connection.ExecuteAsync(sql, new
        {
            AgentId = agentId,
            ApplicationId = applicationId
        });
        
        if (result > 0)
        {
            _logger.LogInformation("Assigned application {ApplicationId} to agent {AgentId}",
                applicationId, agentId);
            return true;
        }
        
        return false;
    }
    
    public async Task<IEnumerable<Agent>> GetOnlineAgentsAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                Id, Name, IpAddress, Status,
                TotalMemoryMB, AvailableMemoryMB, CpuCores,
                AvailablePorts, LastHeartbeat, RegisteredAt
            FROM Agents
            WHERE Status = 'Online'
            AND LastHeartbeat > DATEADD(MINUTE, -5, GETUTCDATE())
            ORDER BY AvailableMemoryMB DESC";
        
        return await connection.QueryAsync<Agent>(sql);
    }
    
    public async Task<IEnumerable<Application>> GetAgentApplicationsAsync(string agentId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT a.*
            FROM Applications a
            INNER JOIN AgentApplications aa ON a.Id = aa.ApplicationId
            WHERE aa.AgentId = @AgentId
            ORDER BY a.Name";
        
        var applications = await connection.QueryAsync<Application>(sql, new { AgentId = agentId });
        
        // Deserialize JSON fields
        foreach (var app in applications)
        {
            if (!string.IsNullOrEmpty(app.PortRequirementsJson))
            {
                app.PortRequirements = JsonSerializer.Deserialize<List<PortRequirement>>(
                    app.PortRequirementsJson) ?? new List<PortRequirement>();
            }
            
            if (!string.IsNullOrEmpty(app.EnvironmentVariablesJson))
            {
                app.EnvironmentVariables = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    app.EnvironmentVariablesJson) ?? new Dictionary<string, string>();
            }
        }
        
        return applications;
    }
    
    private async Task MarkOfflineAgentsAsync(IDbConnection connection)
    {
        const string sql = @"
            UPDATE Agents 
            SET Status = 'Offline'
            WHERE LastHeartbeat < DATEADD(MINUTE, -5, GETUTCDATE())
            AND Status = 'Online'";
        
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
    public string Status { get; set; } = "Offline"; // Online, Offline, Draining
    public int? TotalMemoryMB { get; set; }
    public int? AvailableMemoryMB { get; set; }
    public int? CpuCores { get; set; }
    public string AvailablePorts { get; set; } = string.Empty; // JSON
    public DateTime? LastHeartbeat { get; set; }
    public DateTime RegisteredAt { get; set; }
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