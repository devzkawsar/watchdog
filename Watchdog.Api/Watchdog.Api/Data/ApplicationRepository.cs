using Dapper;
using System.Text.Json;
using Watchdog.Api.Interface;

namespace Watchdog.Api.Data;

public class ApplicationRepository : IApplicationRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    
    public ApplicationRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }
    
    public async Task<IEnumerable<Application>> GetAll()
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                id AS Id, 
                name AS Name, 
                display_name AS DisplayName, 
                executable_path AS ExecutablePath, 
                arguments AS Arguments, 
                working_directory AS WorkingDirectory,
                application_type AS ApplicationType, 
                health_check_interval AS HealthCheckInterval, 
                heartbeat_timeout AS HeartbeatTimeout,
                desired_instances AS DesiredInstances, 
                min_instances AS MinInstances, 
                max_instances AS MaxInstances,
                environment_variables AS EnvironmentVariablesJson, 
                auto_start AS AutoStart,
                auto_start AS AutoStart,
                created AS Created,
                updated AS Updated,
                created_by AS CreatedBy,
                updated_by AS UpdatedBy
            FROM application
            ORDER BY name";
        
        var applications = await connection.QueryAsync<Application>(sql);
        
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
    
    public async Task<Application?> GetById(string id)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                id AS Id, 
                name AS Name, 
                display_name AS DisplayName, 
                executable_path AS ExecutablePath, 
                arguments AS Arguments, 
                working_directory AS WorkingDirectory,
                application_type AS ApplicationType, 
                health_check_interval AS HealthCheckInterval, 
                heartbeat_timeout AS HeartbeatTimeout,
                desired_instances AS DesiredInstances, 
                min_instances AS MinInstances, 
                max_instances AS MaxInstances,
                environment_variables AS EnvironmentVariablesJson, 
                auto_start AS AutoStart,
                auto_start AS AutoStart,
                created AS Created,
                updated AS Updated,
                created_by AS CreatedBy,
                updated_by AS UpdatedBy
            FROM application
            WHERE id = @Id";
        
        var app = await connection.QueryFirstOrDefaultAsync<Application>(sql, new { Id = id });
        
        if (app != null)
        {
            if (!string.IsNullOrEmpty(app.EnvironmentVariablesJson))
            {
                app.EnvironmentVariables = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    app.EnvironmentVariablesJson) ?? new Dictionary<string, string>();
            }
        }
        
        return app;
    }
    
    public async Task<int> Create(Application application)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            INSERT INTO application 
                (id, name, display_name, executable_path, arguments, working_directory,
                 application_type,health_check_interval, heartbeat_timeout,
                 desired_instances, min_instances, max_instances,
                 environment_variables, auto_start, created, created_by)
            VALUES 
                (@Id, @Name, @DisplayName, @ExecutablePath, @Arguments, @WorkingDirectory,
                 @ApplicationType, @HealthCheckInterval, @HeartbeatTimeout,
                 @DesiredInstances, @MinInstances, @MaxInstances,
                 @EnvironmentVariablesJson, @AutoStart, GETUTCDATE(), NULL)";
        
        // Serialize JSON fields
        application.EnvironmentVariablesJson = JsonSerializer.Serialize(application.EnvironmentVariables);
        
        return await connection.ExecuteAsync(sql, application);
    }
    
    public async Task<int> Update(Application application)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            UPDATE application 
            SET name = @Name,
                display_name = @DisplayName,
                executable_path = @ExecutablePath,
                arguments = @Arguments,
                working_directory = @WorkingDirectory,
                application_type = @ApplicationType,
                health_check_interval = @HealthCheckInterval,
                heartbeat_timeout = @HeartbeatTimeout,
                desired_instances = @DesiredInstances,
                min_instances = @MinInstances,
                max_instances = @MaxInstances,
                updated_by = NULL
            WHERE id = @Id";
        
        // Serialize JSON fields
        application.EnvironmentVariablesJson = JsonSerializer.Serialize(application.EnvironmentVariables);
        
        return await connection.ExecuteAsync(sql, application);
    }
    
    public async Task<int> Delete(string id)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = "DELETE FROM application WHERE id = @Id";
        return await connection.ExecuteAsync(sql, new { Id = id });
    }
    
    public async Task<IEnumerable<ApplicationInstance>> GetApplicationInstances(string applicationId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                instance_id AS InstanceId, 
                application_id AS ApplicationId, 
                agent_id AS AgentId, 
                process_id AS ProcessId,
                status AS Status, 
                cpu_percent AS CpuPercent, 
                memory_mb AS MemoryMB, 
                memory_percent AS MemoryPercent, 
                thread_count AS ThreadCount, 
                handle_count AS HandleCount,
                assigned_port AS AssignedPort, 
                last_health_check AS LastHealthCheck, 
                last_heartbeat AS LastHeartbeat, 
                started_at AS StartedAt, 
                stopped_at AS StoppedAt, 
                created_at AS CreatedAt, 
                updated_at AS UpdatedAt
            FROM application_instance
            WHERE application_id = @ApplicationId
            ORDER BY created_at DESC";
        
        return await connection.QueryAsync<ApplicationInstance>(sql, new { ApplicationId = applicationId });
    }

    public async Task<IEnumerable<ApplicationInstance>> GetActiveInstancesForAgent(string agentId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                instance_id AS InstanceId, 
                application_id AS ApplicationId, 
                agent_id AS AgentId, 
                process_id AS ProcessId,
                status AS Status, 
                cpu_percent AS CpuPercent, 
                memory_mb AS MemoryMB, 
                memory_percent AS MemoryPercent, 
                thread_count AS ThreadCount, 
                handle_count AS HandleCount,
                assigned_port AS AssignedPort, 
                last_health_check AS LastHealthCheck, 
                last_heartbeat AS LastHeartbeat, 
                started_at AS StartedAt, 
                stopped_at AS StoppedAt, 
                created_at AS CreatedAt, 
                updated_at AS UpdatedAt
            FROM application_instance
            WHERE agent_id = @AgentId AND status = 'running'
            ORDER BY created_at DESC";
        
        return await connection.QueryAsync<ApplicationInstance>(sql, new { AgentId = agentId });
    }
    
    public async Task<IEnumerable<ApplicationInstance>> GetInstancesWithHeartbeatTimeout()
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                instance_id AS InstanceId, 
                application_id AS ApplicationId, 
                agent_id AS AgentId, 
                process_id AS ProcessId,
                status AS Status, 
                cpu_percent AS CpuPercent, 
                memory_mb AS MemoryMB, 
                memory_percent AS MemoryPercent, 
                thread_count AS ThreadCount, 
                handle_count AS HandleCount,
                assigned_port AS AssignedPort, 
                last_health_check AS LastHealthCheck, 
                last_heartbeat AS LastHeartbeat, 
                started_at AS StartedAt, 
                stopped_at AS StoppedAt, 
                created_at AS CreatedAt, 
                updated_at AS UpdatedAt
            FROM application_instance
            WHERE status IN ('running', 'starting')";
        
        return await connection.QueryAsync<ApplicationInstance>(sql);
    }

    public async Task<IEnumerable<ApplicationInstanceHeartbeatInfo>> GetStaleInstancesByHeartbeat()
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT
                ai.instance_id AS InstanceId,
                ai.application_id AS ApplicationId,
                ai.agent_id AS AgentId,
                ai.status AS Status,
                ai.last_heartbeat AS LastHeartbeat,
                a.health_check_interval AS HealthCheckInterval,
                a.heartbeat_timeout AS HeartbeatTimeout
            FROM application_instance ai
            INNER JOIN application a ON a.id = ai.application_id
            WHERE ai.status IN ('running', 'starting')
              AND (
                    ai.last_heartbeat IS NULL
                    OR ai.last_heartbeat < DATEADD(SECOND, -a.heartbeat_timeout, GETUTCDATE())
                  )";
        
        return await connection.QueryAsync<ApplicationInstanceHeartbeatInfo>(sql);
    }

    public async Task<int> UpdateInstanceStatusWithoutHeartbeat(string instanceId, string status)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            UPDATE application_instance
            SET status = @Status,
                updated_at = GETUTCDATE()
            WHERE instance_id = @InstanceId";
        
        return await connection.ExecuteAsync(sql, new
        {
            InstanceId = instanceId,
            Status = status
        });
    }
    
    public async Task<int> UpdateInstanceStatus(string instanceId, string status, 
        double? cpuPercent = null, double? memoryMB = null, int? processId = null, string? agentId = null)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            UPDATE application_instance 
            SET status = @Status,
                cpu_percent = COALESCE(@CpuPercent, cpu_percent),
                memory_mb = COALESCE(@MemoryMB, memory_mb),
                process_id = COALESCE(@ProcessId, process_id),
                agent_id = COALESCE(@AgentId, agent_id),
                last_heartbeat = GETUTCDATE(),
                updated_at = GETUTCDATE()
            WHERE instance_id = @InstanceId";
        
        return await connection.ExecuteAsync(sql, new 
        { 
            InstanceId = instanceId,
            Status = status,
            CpuPercent = cpuPercent,
            MemoryMB = memoryMB,
            ProcessId = processId,
            AgentId = agentId
        });
    }


    public async Task<IEnumerable<ApplicationInstance>> GetOrphanInstances(List<string> applicationIds)
    {
        if (applicationIds == null || !applicationIds.Any())
        {
            return Enumerable.Empty<ApplicationInstance>();
        }

        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                instance_id AS InstanceId, 
                application_id AS ApplicationId, 
                agent_id AS AgentId, 
                process_id AS ProcessId,
                status AS Status, 
                cpu_percent AS CpuPercent, 
                memory_mb AS MemoryMB, 
                memory_percent AS MemoryPercent, 
                thread_count AS ThreadCount, 
                handle_count AS HandleCount,
                assigned_port AS AssignedPort, 
                last_health_check AS LastHealthCheck, 
                last_heartbeat AS LastHeartbeat, 
                started_at AS StartedAt, 
                stopped_at AS StoppedAt, 
                created_at AS CreatedAt, 
                updated_at AS UpdatedAt
            FROM application_instance
            WHERE application_id IN @ApplicationIds 
              AND agent_id IS NULL"; // Only claim active orphans

        return await connection.QueryAsync<ApplicationInstance>(sql, new { ApplicationIds = applicationIds });
    }

    public async Task ClaimOrphanInstance(string oldInstanceId, string newInstanceId, string newAgentId)
    {
        using var connection = _connectionFactory.CreateConnection();

        
        using var transaction = connection.BeginTransaction();
        
        try
        {
            await connection.ExecuteAsync("DELETE FROM heartbeat WHERE instance_id = @OldId", new { OldId = oldInstanceId }, transaction);
            await connection.ExecuteAsync("DELETE FROM metrics_history WHERE instance_id = @OldId", new { OldId = oldInstanceId }, transaction);
            
            const string updateSql = @"
                UPDATE application_instance 
                SET instance_id = @NewId, 
                    agent_id = @AgentId, 
                    updated_at = GETUTCDATE() 
                WHERE instance_id = @OldId";

            await connection.ExecuteAsync(updateSql, new 
            { 
                OldId = oldInstanceId, 
                NewId = newInstanceId, 
                AgentId = newAgentId 
            }, transaction);

            transaction.Commit();
        }
        catch (Exception)
        {
            transaction.Rollback();
            throw;
        }
    }
}

public class Application
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public int ApplicationType { get; set; } // 0=Console, 1=Service, 2=IIS
    public int DesiredInstances { get; set; } = 1;
    public int MinInstances { get; set; } = 1;
    public int MaxInstances { get; set; } = 5;
    public int HeartbeatTimeout { get; set; } = 120;
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public bool AutoStart { get; set; } = true;
    public DateTime Created { get; set; }
    public DateTime? Updated { get; set; }
    public long? CreatedBy { get; set; }
    public long? UpdatedBy { get; set; }
    
    // JSON serialized fields for database storage
    public string EnvironmentVariablesJson { get; set; } = string.Empty;
}

public class ApplicationInstance
{
    public string InstanceId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string? AgentId { get; set; }
    public int? ProcessId { get; set; }
    public string Status { get; set; } = "pending"; // pending, running, stopped, error
    public double? CpuPercent { get; set; }
    public double? MemoryMB { get; set; }
    public double? MemoryPercent { get; set; }
    public int? ThreadCount { get; set; }
    public int? HandleCount { get; set; }
    public int? AssignedPort { get; set; } // Changed from AssignedPorts string
    public DateTime? LastHealthCheck { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class ApplicationInstanceHeartbeatInfo
{
    public string InstanceId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? LastHeartbeat { get; set; }
    public int HealthCheckInterval { get; set; }
    public int HeartbeatTimeout { get; set; }
}