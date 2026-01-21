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
    private readonly INetworkManager _networkManager;
    private readonly ILogger<CommandService> _logger;
    
    public CommandService(
        IDbConnectionFactory connectionFactory,
        IAgentGrpcService agentGrpcService,
        INetworkManager networkManager,
        ILogger<CommandService> logger)
    {
        _connectionFactory = connectionFactory;
        _agentGrpcService = agentGrpcService;
        _networkManager = networkManager;
        _logger = logger;
    }
    
    public async Task<CommandQueueItem> QueueSpawnCommand(
        string agentId, 
        Application application, 
        string instanceId, 
        int instanceIndex)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        // Allocate ports if needed
        List<PortAssignment> ports = new();
        if (application.PortRequirements != null && application.PortRequirements.Any())
        {
            ports = await _networkManager.AllocatePorts(agentId, application.PortRequirements.Count);
        }
        
        var commandId = Guid.NewGuid().ToString();
        var parameters = new SpawnCommandParams
        {
            ExecutablePath = application.ExecutablePath,
            Arguments = application.Arguments,
            WorkingDirectory = application.WorkingDirectory,
            EnvironmentVariables = application.EnvironmentVariables,
            Ports = ports.Select(p => new PortMapping
            {
                Name = p.Name,
                InternalPort = p.InternalPort,
                ExternalPort = p.ExternalPort,
                Protocol = p.Protocol
            }).ToList(),
            HealthCheckUrl = application.HealthCheckUrl,
            InstanceIndex = instanceIndex
        };
        
        var command = new CommandQueueItem
        {
            CommandId = commandId,
            CommandType = "SPAWN",
            AgentId = agentId,
            ApplicationId = application.Id,
            InstanceId = instanceId,
            Parameters = JsonSerializer.Serialize(parameters),
            Status = "Pending",
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
            CommandType = "KILL",
            AgentId = agentId,
            ApplicationId = applicationId,
            InstanceId = instanceId,
            Parameters = JsonSerializer.Serialize(parameters),
            Status = "Pending",
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
            CommandType = "RESTART",
            AgentId = agentId,
            ApplicationId = applicationId,
            InstanceId = instanceId,
            Parameters = JsonSerializer.Serialize(parameters),
            Status = "Pending",
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
                CommandId, CommandType, AgentId, ApplicationId, 
                InstanceId, Parameters, Status, CreatedAt, 
                SentAt, CompletedAt, ErrorMessage
            FROM CommandQueue
            WHERE AgentId = @AgentId 
            AND Status = 'Pending'
            ORDER BY CreatedAt ASC";
        
        return (await connection.QueryAsync<CommandQueueItem>(sql, new { AgentId = agentId })).ToList();
    }
    
    public async Task<bool> MarkCommandAsSent(string commandId)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            UPDATE CommandQueue 
            SET Status = 'Sent',
                SentAt = GETUTCDATE()
            WHERE CommandId = @CommandId";
        
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
            UPDATE CommandQueue 
            SET Status = @Status,
                CompletedAt = GETUTCDATE(),
                ErrorMessage = @ErrorMessage
            WHERE CommandId = @CommandId";
        
        var parameters = new
        {
            CommandId = commandId,
            Status = success ? "Executed" : "Failed",
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
            INSERT INTO CommandQueue 
                (CommandId, CommandType, AgentId, ApplicationId, 
                 InstanceId, Parameters, Status, CreatedAt)
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
            DELETE FROM CommandQueue 
            WHERE CreatedAt < DATEADD(DAY, -7, GETUTCDATE())
            AND Status IN ('Executed', 'Failed')";
        
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
                SELECT ApplicationId, InstanceId, AgentId 
                FROM CommandQueue 
                WHERE CommandId = @CommandId";
            
            var command = await connection.QueryFirstOrDefaultAsync<CommandQueueItem>(getCommandSql, 
                new { CommandId = commandId });
            
            if (command != null && command.CommandType == "SPAWN")
            {
                // Parse result to get process ID and ports
                var spawnResult = JsonSerializer.Deserialize<SpawnResult>(result);
                if (spawnResult != null && spawnResult.ProcessId > 0)
                {
                    // Update ApplicationInstances table
                    const string updateInstanceSql = @"
                        INSERT INTO ApplicationInstances 
                            (InstanceId, ApplicationId, AgentId, ProcessId, Status, 
                             AssignedPorts, StartedAt, CreatedAt)
                        VALUES 
                            (@InstanceId, @ApplicationId, @AgentId, @ProcessId, 'Running',
                             @AssignedPorts, @StartedAt, GETUTCDATE())";
                    
                    await connection.ExecuteAsync(updateInstanceSql, new
                    {
                        InstanceId = command.InstanceId,
                        ApplicationId = command.ApplicationId,
                        AgentId = command.AgentId,
                        ProcessId = spawnResult.ProcessId,
                        AssignedPorts = JsonSerializer.Serialize(spawnResult.Ports),
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

// Missing NetworkManager service
public interface INetworkManager
{
    Task<List<PortAssignment>> AllocatePorts(string agentId, int portCount);
    Task<bool> ReleasePorts(string agentId, List<int> ports);
}

public class NetworkManager : INetworkManager
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<NetworkManager> _logger;
    
    public NetworkManager(
        IDbConnectionFactory connectionFactory,
        ILogger<NetworkManager> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }
    
    public async Task<List<PortAssignment>> AllocatePorts(string agentId, int portCount)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        // Get agent's available ports
        const string getPortsSql = @"
            SELECT AvailablePorts FROM Agents WHERE Id = @AgentId";
        
        var portsJson = await connection.ExecuteScalarAsync<string>(getPortsSql, new { AgentId = agentId });
        var availablePorts = JsonSerializer.Deserialize<List<int>>(portsJson ?? "[]") ?? new List<int>();
        
        if (availablePorts.Count < portCount)
        {
            // Auto-generate ports starting from 30000
            var startPort = 30000;
            while (availablePorts.Count < portCount)
            {
                if (!availablePorts.Contains(startPort))
                {
                    availablePorts.Add(startPort);
                }
                startPort++;
            }
        }
        
        // Take needed ports
        var assignedPorts = availablePorts.Take(portCount).ToList();
        var remainingPorts = availablePorts.Skip(portCount).ToList();
        
        // Update agent's available ports
        const string updatePortsSql = @"
            UPDATE Agents 
            SET AvailablePorts = @AvailablePorts 
            WHERE Id = @AgentId";
        
        await connection.ExecuteAsync(updatePortsSql, new
        {
            AgentId = agentId,
            AvailablePorts = JsonSerializer.Serialize(remainingPorts)
        });
        
        // Return port assignments
        return assignedPorts.Select((port, index) => new PortAssignment
        {
            InternalPort = port,
            ExternalPort = port,
            Protocol = "TCP",
            Name = $"port-{index}"
        }).ToList();
    }
    
    public async Task<bool> ReleasePorts(string agentId, List<int> ports)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        // Get current available ports
        const string getPortsSql = @"
            SELECT AvailablePorts FROM Agents WHERE Id = @AgentId";
        
        var currentPortsJson = await connection.ExecuteScalarAsync<string>(getPortsSql, new { AgentId = agentId });
        var currentPorts = JsonSerializer.Deserialize<List<int>>(currentPortsJson ?? "[]") ?? new List<int>();
        
        // Add released ports back
        currentPorts.AddRange(ports);
        currentPorts = currentPorts.Distinct().OrderBy(p => p).ToList();
        
        // Update agent
        const string updatePortsSql = @"
            UPDATE Agents 
            SET AvailablePorts = @AvailablePorts 
            WHERE Id = @AgentId";
        
        var result = await connection.ExecuteAsync(updatePortsSql, new
        {
            AgentId = agentId,
            AvailablePorts = JsonSerializer.Serialize(currentPorts)
        });
        
        if (result > 0)
        {
            _logger.LogDebug("Released {Count} ports back to agent {AgentId}", ports.Count, agentId);
            return true;
        }
        
        return false;
    }
}

// DTOs used by CommandService
public class SpawnResult
{
    public int ProcessId { get; set; }
    public string InstanceId { get; set; } = string.Empty;
    public List<PortAssignment> Ports { get; set; } = new();
    public DateTime StartTime { get; set; }
}

public class PortAssignment
{
    public int InternalPort { get; set; }
    public int ExternalPort { get; set; }
    public string Protocol { get; set; } = "TCP";
    public string Name { get; set; } = string.Empty;
}