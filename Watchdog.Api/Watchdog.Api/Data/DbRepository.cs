using System.Data;
using System.Data.SqlClient;
using Dapper;
using Watchdog.Api.Data.Models;

namespace Watchdog.Api.Data;

public class DbRepository : IDbRepository
{
    private readonly string _connectionString;
    
    public DbRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("WatchdogDB") 
            ?? throw new Exception("Database connection string not found");
    }
    
    private IDbConnection CreateConnection() => new SqlConnection(_connectionString);
    
    // Applications
    public async Task<Application?> GetApplicationAsync(string id)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Application>(
            "SELECT * FROM Applications WHERE Id = @Id",
            new { Id = id });
    }
    
    public async Task<IEnumerable<Application>> GetAllApplicationsAsync()
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<Application>("SELECT * FROM Applications ORDER BY Name");
    }
    
    public async Task<string> CreateApplicationAsync(Application app)
    {
        using var conn = CreateConnection();
        app.Id = Guid.NewGuid().ToString();
        app.CreatedAt = DateTime.UtcNow;
        app.UpdatedAt = DateTime.UtcNow;
        
        await conn.ExecuteAsync(@"
            INSERT INTO Applications 
            (Id, Name, DisplayName, ExecutablePath, WorkingDirectory, Arguments, 
             AppType, DesiredInstances, MaxRestartAttempts, AutoStart, Status, CreatedAt, UpdatedAt)
            VALUES 
            (@Id, @Name, @DisplayName, @ExecutablePath, @WorkingDirectory, @Arguments,
             @AppType, @DesiredInstances, @MaxRestartAttempts, @AutoStart, @Status, @CreatedAt, @UpdatedAt)",
            app);
        
        // Create default scaling config
        await conn.ExecuteAsync(@"
            INSERT INTO ScalingConfig 
            (AppId, DesiredReplica, MinReplica, MaxReplica, UpdatedAt)
            VALUES 
            (@AppId, @DesiredInstances, 1, 5, @UpdatedAt)",
            new { AppId = app.Id, app.DesiredInstances, app.UpdatedAt });
        
        return app.Id;
    }
    
    public async Task<bool> UpdateApplicationAsync(Application app)
    {
        using var conn = CreateConnection();
        app.UpdatedAt = DateTime.UtcNow;
        
        var affected = await conn.ExecuteAsync(@"
            UPDATE Applications SET
                Name = @Name,
                DisplayName = @DisplayName,
                ExecutablePath = @ExecutablePath,
                WorkingDirectory = @WorkingDirectory,
                Arguments = @Arguments,
                AppType = @AppType,
                DesiredInstances = @DesiredInstances,
                MaxRestartAttempts = @MaxRestartAttempts,
                AutoStart = @AutoStart,
                Status = @Status,
                UpdatedAt = @UpdatedAt
            WHERE Id = @Id", app);
        
        return affected > 0;
    }
    
    public async Task<bool> DeleteApplicationAsync(string id)
    {
        using var conn = CreateConnection();
        
        // Delete related records
        await conn.ExecuteAsync("DELETE FROM AppInstances WHERE AppId = @Id", new { Id = id });
        await conn.ExecuteAsync("DELETE FROM ScalingConfig WHERE AppId = @Id", new { Id = id });
        
        var affected = await conn.ExecuteAsync("DELETE FROM Applications WHERE Id = @Id", new { Id = id });
        return affected > 0;
    }
    
    // Agents
    public async Task<Agent?> GetAgentAsync(string id)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Agent>(
            "SELECT * FROM Agents WHERE Id = @Id",
            new { Id = id });
    }
    
    public async Task<IEnumerable<Agent>> GetAllAgentsAsync()
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<Agent>("SELECT * FROM Agents ORDER BY Name");
    }
    
    public async Task<string> CreateAgentAsync(Agent agent)
    {
        using var conn = CreateConnection();
        agent.Id = Guid.NewGuid().ToString();
        agent.CreatedAt = DateTime.UtcNow;
        
        await conn.ExecuteAsync(@"
            INSERT INTO Agents 
            (Id, Name, Hostname, IpAddress, Status, CpuCores, TotalMemoryMb, LastHeartbeat, CreatedAt)
            VALUES 
            (@Id, @Name, @Hostname, @IpAddress, @Status, @CpuCores, @TotalMemoryMb, @LastHeartbeat, @CreatedAt)",
            agent);
        
        return agent.Id;
    }
    
    public async Task<bool> UpdateAgentAsync(Agent agent)
    {
        using var conn = CreateConnection();
        
        var affected = await conn.ExecuteAsync(@"
            UPDATE Agents SET
                Name = @Name,
                Hostname = @Hostname,
                IpAddress = @IpAddress,
                Status = @Status,
                CpuCores = @CpuCores,
                TotalMemoryMb = @TotalMemoryMb,
                LastHeartbeat = @LastHeartbeat
            WHERE Id = @Id", agent);
        
        return affected > 0;
    }
    
    public async Task<bool> DeleteAgentAsync(string id)
    {
        using var conn = CreateConnection();
        
        // Mark agent instances as stopped
        await conn.ExecuteAsync(@"
            UPDATE AppInstances 
            SET Status = 2, LastHeartbeat = GETUTCDATE() 
            WHERE AgentId = @Id AND Status = 1",
            new { Id = id });
        
        var affected = await conn.ExecuteAsync("DELETE FROM Agents WHERE Id = @Id", new { Id = id });
        return affected > 0;
    }
    
    public async Task UpdateAgentHeartbeatAsync(string agentId)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE Agents 
            SET LastHeartbeat = GETUTCDATE(), Status = 1 
            WHERE Id = @AgentId",
            new { AgentId = agentId });
    }
    
    // App Instances
    public async Task<IEnumerable<AppInstance>> GetAppInstancesAsync(string appId)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<AppInstance>(
            "SELECT * FROM AppInstances WHERE AppId = @AppId ORDER BY CreatedAt DESC",
            new { AppId = appId });
    }
    
    public async Task<string> CreateAppInstanceAsync(AppInstance instance)
    {
        using var conn = CreateConnection();
        instance.Id = Guid.NewGuid().ToString();
        instance.CreatedAt = DateTime.UtcNow;
        
        await conn.ExecuteAsync(@"
            INSERT INTO AppInstances 
            (Id, AppId, AgentId, Status, CpuPercent, MemoryMb, ProcessId, StartTime, LastHeartbeat, CreatedAt)
            VALUES 
            (@Id, @AppId, @AgentId, @Status, @CpuPercent, @MemoryMb, @ProcessId, @StartTime, @LastHeartbeat, @CreatedAt)",
            instance);
        
        return instance.Id;
    }
    
    public async Task<bool> UpdateAppInstanceAsync(AppInstance instance)
    {
        using var conn = CreateConnection();
        
        var affected = await conn.ExecuteAsync(@"
            UPDATE AppInstances SET
                Status = @Status,
                CpuPercent = @CpuPercent,
                MemoryMb = @MemoryMb,
                ProcessId = @ProcessId,
                LastHeartbeat = @LastHeartbeat,
                StartTime = CASE WHEN @Status = 1 AND StartTime IS NULL THEN GETUTCDATE() ELSE StartTime END
            WHERE Id = @Id",
            new { 
                instance.Id, 
                instance.Status, 
                instance.CpuPercent, 
                instance.MemoryMb,
                instance.ProcessId,
                instance.LastHeartbeat
            });
        
        return affected > 0;
    }
    // Scaling Config
    public async Task<ScalingConfig?> GetScalingConfigAsync(string appId)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<ScalingConfig>(
            "SELECT * FROM ScalingConfig WHERE AppId = @AppId",
            new { AppId = appId });
    }
    
    public async Task<bool> UpdateScalingConfigAsync(ScalingConfig config)
    {
        using var conn = CreateConnection();
        config.UpdatedAt = DateTime.UtcNow;
        
        var affected = await conn.ExecuteAsync(@"
            UPDATE ScalingConfig SET
                DesiredReplica = @DesiredReplica,
                MinReplica = @MinReplica,
                MaxReplica = @MaxReplica,
                ScaleUpThresholdCpu = @ScaleUpThresholdCpu,
                ScaleDownThresholdCpu = @ScaleDownThresholdCpu,
                ScaleUpThresholdMemory = @ScaleUpThresholdMemory,
                ScaleDownThresholdMemory = @ScaleDownThresholdMemory,
                UpdatedAt = @UpdatedAt
            WHERE AppId = @AppId", config);
        
        return affected > 0;
    }
    
    // Agent Instances
    public async Task<IEnumerable<AppInstance>> GetAgentInstancesAsync(string agentId)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<AppInstance>(
            "SELECT * FROM AppInstances WHERE AgentId = @AgentId ORDER BY CreatedAt DESC",
            new { AgentId = agentId });
    }
    
    // Delete App Instance
    public async Task<bool> DeleteAppInstanceAsync(string id)
    {
        using var conn = CreateConnection();
        
        var affected = await conn.ExecuteAsync(
            "DELETE FROM AppInstances WHERE Id = @Id",
            new { Id = id });
        
        return affected > 0;
    }
    
    // ========== ADDITIONAL HELPER METHODS ==========
    
    // Get running instances for an app
    public async Task<int> GetRunningInstanceCountAsync(string appId)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM AppInstances WHERE AppId = @AppId AND Status = 1",
            new { AppId = appId });
    }
    
    // Get all instances across all agents
    public async Task<IEnumerable<AppInstance>> GetAllInstancesAsync()
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<AppInstance>(
            "SELECT * FROM AppInstances ORDER BY CreatedAt DESC");
    }
    
    // Update instance heartbeat
    public async Task<bool> UpdateInstanceHeartbeatAsync(string instanceId)
    {
        using var conn = CreateConnection();
        
        var affected = await conn.ExecuteAsync(@"
            UPDATE AppInstances 
            SET LastHeartbeat = GETUTCDATE() 
            WHERE Id = @InstanceId",
            new { InstanceId = instanceId });
        
        return affected > 0;
    }
    
    // Get unhealthy instances (no heartbeat for X minutes)
    public async Task<IEnumerable<AppInstance>> GetUnhealthyInstancesAsync(int minutesThreshold = 5)
    {
        using var conn = CreateConnection();
        var cutoffTime = DateTime.UtcNow.AddMinutes(-minutesThreshold);
        
        return await conn.QueryAsync<AppInstance>(@"
            SELECT * FROM AppInstances 
            WHERE (LastHeartbeat IS NULL OR LastHeartbeat < @CutoffTime)
            AND Status = 1", // Only running instances
            new { CutoffTime = cutoffTime });
    }
    
    // Assign app to agent with instance tracking
    public async Task<string> AssignAppToAgentAsync(string agentId, string appId, int instanceCount = 1)
    {
        using var conn = CreateConnection();
        
        var instanceIds = new List<string>();
        for (int i = 0; i < instanceCount; i++)
        {
            var instanceId = Guid.NewGuid().ToString();
            instanceIds.Add(instanceId);
            
            await conn.ExecuteAsync(@"
                INSERT INTO AppInstances 
                (Id, AppId, AgentId, Status, CreatedAt)
                VALUES 
                (@Id, @AppId, @AgentId, 0, @CreatedAt)",
                new 
                { 
                    Id = instanceId, 
                    AppId = appId, 
                    AgentId = agentId,
                    CreatedAt = DateTime.UtcNow 
                });
        }
        
        return string.Join(",", instanceIds);
    }
    
    // Remove all instances of an app from an agent
    public async Task<bool> RemoveAppFromAgentAsync(string agentId, string appId)
    {
        using var conn = CreateConnection();
        
        var affected = await conn.ExecuteAsync(
            "DELETE FROM AppInstances WHERE AgentId = @AgentId AND AppId = @AppId",
            new { AgentId = agentId, AppId = appId });
        
        return affected > 0;
    }
    
    // Get agent load (number of instances per agent)
    public async Task<Dictionary<string, int>> GetAgentLoadAsync()
    {
        using var conn = CreateConnection();
        
        var results = await conn.QueryAsync(@"
            SELECT AgentId, COUNT(*) as InstanceCount 
            FROM AppInstances 
            WHERE Status = 1 
            GROUP BY AgentId");
        
        return results.ToDictionary(
            row => (string)row.AgentId, 
            row => (int)row.InstanceCount);
    }
    
    // Get application statistics
    public async Task<ApplicationStats> GetApplicationStatsAsync(string appId)
    {
        using var conn = CreateConnection();
        
        var stats = await conn.QuerySingleOrDefaultAsync(@"
            SELECT 
                COUNT(*) as TotalInstances,
                SUM(CASE WHEN Status = 1 THEN 1 ELSE 0 END) as RunningInstances,
                AVG(CASE WHEN Status = 1 THEN CpuPercent ELSE NULL END) as AvgCpuPercent,
                AVG(CASE WHEN Status = 1 THEN MemoryMb ELSE NULL END) as AvgMemoryMb,
                MIN(CreatedAt) as FirstInstanceCreated,
                MAX(LastHeartbeat) as LastHeartbeat
            FROM AppInstances 
            WHERE AppId = @AppId",
            new { AppId = appId });
        
        if (stats == null)
            return new ApplicationStats { AppId = appId };
        
        return new ApplicationStats
        {
            AppId = appId,
            TotalInstances = (int)stats.TotalInstances,
            RunningInstances = (int)stats.RunningInstances,
            AvgCpuPercent = stats.AvgCpuPercent != null ? (double)stats.AvgCpuPercent : 0,
            AvgMemoryMb = stats.AvgMemoryMb != null ? (double)stats.AvgMemoryMb : 0,
            FirstInstanceCreated = stats.FirstInstanceCreated as DateTime?,
            LastHeartbeat = stats.LastHeartbeat as DateTime?
        };
    }
}