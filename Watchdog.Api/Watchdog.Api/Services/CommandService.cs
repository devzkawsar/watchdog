using Dapper;
using System.Data;
using System.Text.Json;
using Watchdog.Api.Data;
using Watchdog.Api.gRPC;
using Watchdog.Api.Interface;
using Watchdog.Api.Protos;

namespace Watchdog.Api.Services;

public class CommandService : ICommandService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IAgentGrpcService _agentGrpcService;
    private readonly ILogger<CommandService> _logger;
    
    public CommandService(
        IDbConnectionFactory connectionFactory,
        IAgentGrpcService agentGrpcService,
        ILogger<CommandService> logger)
    {
        _connectionFactory = connectionFactory;
        _agentGrpcService = agentGrpcService;
        _logger = logger;
    }
    
    public async Task<CommandQueueItem> QueueSpawnCommand(
        string agentId, 
        Application application, 
        string instanceId, 
        int instanceIndex)
    {
        using var connection = _connectionFactory.CreateConnection();
        var commandId = Guid.NewGuid().ToString();
        var parameters = new SpawnCommandParams
        {
            ExecutablePath = application.ExecutablePath,
            Arguments = application.Arguments,
            WorkingDirectory = application.WorkingDirectory,
            EnvironmentVariables = application.EnvironmentVariables,
            Port = application.BuiltInPort ?? 0,
            InstanceIndex = instanceIndex
        };
        
        var command = new CommandQueueItem
        {
            CommandId = commandId,
            CommandType = "spawn",
            AgentId = agentId,
            ApplicationId = application.Id,
            InstanceId = instanceId,
            Parameters = JsonSerializer.Serialize(parameters),
            Status = "pending",
            CreatedAt = DateTime.UtcNow
        };
        
        await QueueCommand(command);
        
        _logger.LogInformation(
            "Queued SPAWN command {CommandId} for application {AppId} instance {InstanceId} on agent {AgentId}",
            commandId, application.Id, instanceId, agentId);
        
        // Try to send immediately if agent is connected
        await TrySendCommandImmediately(command);
        
        return command;
    }
    
    public async Task<CommandQueueItem> QueueKillCommand(
        string agentId, 
        string applicationId, 
        string instanceId)
    {
        var commandId = Guid.NewGuid().ToString();
        var parameters = new KillCommandParams
        {
            Force = true,
            TimeoutSeconds = 30
        };
        
        var command = new CommandQueueItem
        {
            CommandId = commandId,
            CommandType = "kill",
            AgentId = agentId,
            ApplicationId = applicationId,
            InstanceId = instanceId,
            Parameters = JsonSerializer.Serialize(parameters),
            Status = "pending",
            CreatedAt = DateTime.UtcNow
        };
        
        await QueueCommand(command);
        
        _logger.LogInformation(
            "Queued KILL command {CommandId} for instance {InstanceId} on agent {AgentId}",
            commandId, instanceId, agentId);
        
        // Try to send immediately if agent is connected
        await TrySendCommandImmediately(command);
        
        return command;
    }
    
    public async Task<CommandQueueItem> QueueRestartCommand(
        string agentId, 
        string applicationId, 
        string instanceId)
    {
        var commandId = Guid.NewGuid().ToString();
        var parameters = new RestartCommandParams
        {
            TimeoutSeconds = 30
        };
        
        var command = new CommandQueueItem
        {
            CommandId = commandId,
            CommandType = "restart",
            AgentId = agentId,
            ApplicationId = applicationId,
            InstanceId = instanceId,
            Parameters = JsonSerializer.Serialize(parameters),
            Status = "pending",
            CreatedAt = DateTime.UtcNow
        };
        
        await QueueCommand(command);
        
        _logger.LogInformation(
            "Queued RESTART command {CommandId} for instance {InstanceId} on agent {AgentId}",
            commandId, instanceId, agentId);
        
        // Try to send immediately if agent is connected
        await TrySendCommandImmediately(command);
        
        return command;
    }
    
    public async Task<List<CommandQueueItem>> GetPendingCommands(string agentId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            SELECT 
                command_id AS CommandId, 
                command_type AS CommandType, 
                agent_id AS AgentId, 
                application_id AS ApplicationId, 
                instance_id AS InstanceId, 
                parameters AS Parameters, 
                status AS Status, 
                created_at AS CreatedAt, 
                sent_at AS SentAt, 
                completed_at AS CompletedAt, 
                error_message AS ErrorMessage
            FROM command_queue
            WHERE agent_id = @AgentId 
            AND status = 'pending'
            ORDER BY created_at ASC";
        
        return (await connection.QueryAsync<CommandQueueItem>(sql, new { AgentId = agentId })).ToList();
    }
    
    public async Task<bool> MarkCommandAsSent(string commandId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            UPDATE command_queue 
            SET status = 'sent',
                sent_at = GETUTCDATE()
            WHERE command_id = @CommandId";
        
        var result = await connection.ExecuteAsync(sql, new { CommandId = commandId });
        return result > 0;
    }
    
    public async Task<bool> MarkCommandAsExecuted(
        string commandId, 
        bool success, 
        string? result = null, 
        string? error = null)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            UPDATE command_queue 
            SET status = @Status,
                completed_at = GETUTCDATE(),
                error_message = @ErrorMessage
            WHERE command_id = @CommandId";
        
        var parameters = new
        {
            CommandId = commandId,
            Status = success ? "executed" : "failed",
            ErrorMessage = error
        };
        
        var rowsAffected = await connection.ExecuteAsync(sql, parameters);
        
        if (rowsAffected > 0 && success && !string.IsNullOrEmpty(result))
        {
            // If spawn was successful, we might need to update the instance record
            await UpdateInstanceAfterSuccessfulSpawn(commandId, result);
        }
        
        return rowsAffected > 0;
    }
    
    public async Task<bool> QueueCommand(CommandQueueItem command)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            INSERT INTO command_queue 
                (command_id, command_type, agent_id, application_id, 
                 instance_id, parameters, status, created_at)
            VALUES 
                (@CommandId, @CommandType, @AgentId, @ApplicationId, 
                 @InstanceId, @Parameters, @Status, @CreatedAt)";
        
        try
        {
            var result = await connection.ExecuteAsync(sql, command);
            return result > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue command {CommandId}", command.CommandId);
            return false;
        }
    }
    
    public async Task CleanupOldCommands()
    {
        using var connection = _connectionFactory.CreateConnection();
        
        // Delete commands older than 7 days
        const string sql = @"
            DELETE FROM command_queue 
            WHERE created_at < DATEADD(DAY, -7, GETUTCDATE())
            AND status IN ('executed', 'failed')";
        
        var deletedCount = await connection.ExecuteAsync(sql);
        
        if (deletedCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} old commands", deletedCount);
        }
    }
    
    private async Task TrySendCommandImmediately(CommandQueueItem command)
    {
        try
        {
            // Convert to gRPC command
            var grpcCommand = new CommandRequest
            {
                CommandId = command.CommandId,
                CommandType = command.CommandType,
                ApplicationId = command.ApplicationId,
                InstanceId = command.InstanceId,
                Parameters = command.Parameters,
                Timestamp = command.CreatedAt.Ticks
            };
            
            // Try to send via gRPC
            var sent = await _agentGrpcService.SendCommandToAgentAsync(command.AgentId, grpcCommand);
            
            if (sent)
            {
                await MarkCommandAsSent(command.CommandId);
                _logger.LogDebug(
                    "Successfully sent command {CommandId} to agent {AgentId} immediately",
                    command.CommandId, command.AgentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, 
                "Failed to send command {CommandId} immediately to agent {AgentId}. It will be sent on next connection.",
                command.CommandId, command.AgentId);
        }
    }
    
    private async Task UpdateInstanceAfterSuccessfulSpawn(string commandId, string result)
    {
        try
        {
            // Get the command to find instance details
            using var connection = _connectionFactory.CreateConnection();
            
            const string getCommandSql = @"
                SELECT application_id AS ApplicationId, instance_id AS InstanceId, agent_id AS AgentId 
                FROM command_queue 
                WHERE command_id = @CommandId";
            
            var command = await connection.QueryFirstOrDefaultAsync<CommandQueueItem>(getCommandSql, 
                new { CommandId = commandId });
            
            if (command != null && command.CommandType == "spawn")
            {
                // Parse result to get process ID and ports
                var spawnResult = JsonSerializer.Deserialize<SpawnResult>(result);
                if (spawnResult != null && spawnResult.ProcessId > 0)
                {
                    // Update ApplicationInstances table
                    const string updateInstanceSql = @"
                        IF EXISTS (SELECT 1 FROM application_instance WHERE instance_id = @InstanceId)
                        BEGIN
                            UPDATE application_instance 
                            SET agent_id = @AgentId,
                                process_id = @ProcessId,
                                status = 'running',
                                assigned_port = @AssignedPort,
                                started_at = @StartedAt,
                                updated_at = GETUTCDATE(),
                                last_heartbeat = GETUTCDATE()
                            WHERE instance_id = @InstanceId
                        END
                        ELSE
                        BEGIN
                            INSERT INTO application_instance 
                                (instance_id, application_id, agent_id, process_id, status, 
                                 assigned_port, started_at, created_at, updated_at, last_heartbeat)
                            VALUES 
                                (@InstanceId, @ApplicationId, @AgentId, @ProcessId, 'running',
                                 @AssignedPort, @StartedAt, GETUTCDATE(), GETUTCDATE(), GETUTCDATE())
                        END";
                    
                    await connection.ExecuteAsync(updateInstanceSql, new
                    {
                        InstanceId = command.InstanceId,
                        ApplicationId = command.ApplicationId,
                        AgentId = command.AgentId,
                        ProcessId = spawnResult.ProcessId,
                        AssignedPort = spawnResult.AssignedPort,
                        StartedAt = spawnResult.StartTime
                    });
                    
                    _logger.LogInformation(
                        "Updated instance {InstanceId} after successful spawn (PID: {ProcessId})",
                        command.InstanceId, spawnResult.ProcessId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update instance after successful spawn for command {CommandId}", 
                commandId);
        }
    }
}

// DTOs used by CommandService
public class SpawnResult
{
    public int ProcessId { get; set; }
    public string InstanceId { get; set; } = string.Empty;
    public int AssignedPort { get; set; }
    public DateTime StartTime { get; set; }
}

public class SpawnCommandParams
{
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public int Port { get; set; }
    public int HealthCheckInterval { get; set; } = 30;
    public int InstanceIndex { get; set; }
}

public class PortAssignment
{
    public int InternalPort { get; set; }
    public int ExternalPort { get; set; }
    public string Protocol { get; set; } = "TCP";
    public string Name { get; set; } = string.Empty;
}