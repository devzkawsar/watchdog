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
                Id, Name, DisplayName, ExecutablePath, Arguments, WorkingDirectory,
                ApplicationType, HealthCheckUrl, HealthCheckInterval,
                DesiredInstances, MinInstances, MaxInstances,
                PortRequirements AS PortRequirementsJson, EnvironmentVariables AS EnvironmentVariablesJson, AutoStart,
                CreatedAt, UpdatedAt
            FROM Applications
            ORDER BY Name";
        
        var applications = await connection.QueryAsync<Application>(sql);
        
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
    
    public async Task<Application?> GetById(string id)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                Id, Name, DisplayName, ExecutablePath, Arguments, WorkingDirectory,
                ApplicationType, HealthCheckUrl, HealthCheckInterval,
                DesiredInstances, MinInstances, MaxInstances,
                PortRequirements AS PortRequirementsJson, EnvironmentVariables AS EnvironmentVariablesJson, AutoStart,
                CreatedAt, UpdatedAt
            FROM Applications
            WHERE Id = @Id";
        
        var app = await connection.QueryFirstOrDefaultAsync<Application>(sql, new { Id = id });
        
        if (app != null)
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
        
        return app;
    }
    
    public async Task<int> Create(Application application)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            INSERT INTO Applications 
                (Id, Name, DisplayName, ExecutablePath, Arguments, WorkingDirectory,
                 ApplicationType, HealthCheckUrl, HealthCheckInterval,
                 DesiredInstances, MinInstances, MaxInstances,
                 PortRequirements, EnvironmentVariables, AutoStart)
            VALUES 
                (@Id, @Name, @DisplayName, @ExecutablePath, @Arguments, @WorkingDirectory,
                 @ApplicationType, @HealthCheckUrl, @HealthCheckInterval,
                 @DesiredInstances, @MinInstances, @MaxInstances,
                 @PortRequirementsJson, @EnvironmentVariablesJson, @AutoStart)";
        
        // Serialize JSON fields
        application.PortRequirementsJson = JsonSerializer.Serialize(application.PortRequirements);
        application.EnvironmentVariablesJson = JsonSerializer.Serialize(application.EnvironmentVariables);
        
        return await connection.ExecuteAsync(sql, application);
    }
    
    public async Task<int> Update(Application application)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            UPDATE Applications 
            SET Name = @Name,
                DisplayName = @DisplayName,
                ExecutablePath = @ExecutablePath,
                Arguments = @Arguments,
                WorkingDirectory = @WorkingDirectory,
                ApplicationType = @ApplicationType,
                HealthCheckUrl = @HealthCheckUrl,
                HealthCheckInterval = @HealthCheckInterval,
                DesiredInstances = @DesiredInstances,
                MinInstances = @MinInstances,
                MaxInstances = @MaxInstances,
                PortRequirements = @PortRequirementsJson,
                EnvironmentVariables = @EnvironmentVariablesJson,
                AutoStart = @AutoStart,
                UpdatedAt = GETUTCDATE()
            WHERE Id = @Id";
        
        // Serialize JSON fields
        application.PortRequirementsJson = JsonSerializer.Serialize(application.PortRequirements);
        application.EnvironmentVariablesJson = JsonSerializer.Serialize(application.EnvironmentVariables);
        
        return await connection.ExecuteAsync(sql, application);
    }
    
    public async Task<int> Delete(string id)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = "DELETE FROM Applications WHERE Id = @Id";
        return await connection.ExecuteAsync(sql, new { Id = id });
    }
    
    public async Task<IEnumerable<ApplicationInstance>> GetApplicationInstances(string applicationId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                InstanceId, ApplicationId, AgentId, ProcessId,
                Status, CpuPercent, MemoryMB, AssignedPorts,
                LastHealthCheck, StartedAt, StoppedAt, CreatedAt
            FROM ApplicationInstances
            WHERE ApplicationId = @ApplicationId
            ORDER BY CreatedAt DESC";
        
        return await connection.QueryAsync<ApplicationInstance>(sql, new { ApplicationId = applicationId });
    }
    
    public async Task<int> UpdateInstanceStatus(string instanceId, string status, 
        double? cpuPercent = null, double? memoryMB = null)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            UPDATE ApplicationInstances 
            SET Status = @Status,
                CpuPercent = ISNULL(@CpuPercent, CpuPercent),
                MemoryMB = ISNULL(@MemoryMB, MemoryMB),
                LastHealthCheck = GETUTCDATE()
            WHERE InstanceId = @InstanceId";
        
        return await connection.ExecuteAsync(sql, new 
        { 
            InstanceId = instanceId,
            Status = status,
            CpuPercent = cpuPercent,
            MemoryMB = memoryMB
        });
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
    public string HealthCheckUrl { get; set; } = string.Empty;
    public int HealthCheckInterval { get; set; } = 30;
    public int DesiredInstances { get; set; } = 1;
    public int MinInstances { get; set; } = 1;
    public int MaxInstances { get; set; } = 5;
    public List<PortRequirement> PortRequirements { get; set; } = new();
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public bool AutoStart { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // JSON serialized fields for database storage
    public string PortRequirementsJson { get; set; } = string.Empty;
    public string EnvironmentVariablesJson { get; set; } = string.Empty;
}

public class ApplicationInstance
{
    public string InstanceId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public int? ProcessId { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Running, Stopped, Error
    public double? CpuPercent { get; set; }
    public double? MemoryMB { get; set; }
    public string AssignedPorts { get; set; } = string.Empty; // JSON
    public DateTime? LastHealthCheck { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PortRequirement
{
    public string Name { get; set; } = string.Empty;
    public int InternalPort { get; set; }
    public string Protocol { get; set; } = "TCP";
    public bool Required { get; set; } = true;
}