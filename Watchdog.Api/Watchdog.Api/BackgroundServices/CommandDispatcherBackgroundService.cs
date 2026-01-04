using System.Data.SqlClient;
using Dapper;
using Watchdog.Api.Services;

namespace Watchdog.Api.BackgroundServices;

public class CommandDispatcherBackgroundService : BackgroundService
{
    private readonly ICommandService _commandService;
    private readonly IAgentGrpcService _agentGrpcService;
    private readonly ILogger<CommandDispatcherBackgroundService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(10);
    
    public CommandDispatcherBackgroundService(
        ICommandService commandService,
        IAgentGrpcService agentGrpcService,
        ILogger<CommandDispatcherBackgroundService> logger)
    {
        _commandService = commandService;
        _agentGrpcService = agentGrpcService;
        _logger = logger;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Command dispatcher background service started");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Clean up old commands
                await _commandService.CleanupOldCommandsAsync();
                
                // Dispatch pending commands to all agents
                await DispatchPendingCommandsAsync();
                
                // Wait for next cycle
                await Task.Delay(_interval, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in command dispatcher background service");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        
        _logger.LogInformation("Command dispatcher background service stopped");
    }
    
    private async Task DispatchPendingCommandsAsync()
    {
        try
        {
            // Get all online agents
            using var connection = new SqlConnection(_connectionString);
            const string getAgentsSql = @"
                SELECT Id FROM Agents 
                WHERE Status = 'Online' 
                AND LastHeartbeat > DATEADD(MINUTE, -5, GETUTCDATE())";
            
            var agentIds = await connection.QueryAsync<string>(getAgentsSql);
            
            foreach (var agentId in agentIds)
            {
                await DispatchCommandsToAgentAsync(agentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching pending commands");
        }
    }
    
    private async Task DispatchCommandsToAgentAsync(string agentId)
    {
        try
        {
            var pendingCommands = await _commandService.GetPendingCommandsAsync(agentId);
            
            if (!pendingCommands.Any())
                return;
            
            _logger.LogDebug("Found {Count} pending commands for agent {AgentId}", 
                pendingCommands.Count, agentId);
            
            foreach (var command in pendingCommands)
            {
                await SendCommandToAgentAsync(agentId, command);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching commands to agent {AgentId}", agentId);
        }
    }
    
    private async Task SendCommandToAgentAsync(string agentId, CommandQueueItem command)
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
            var sent = await _agentGrpcService.SendCommandToAgentAsync(agentId, grpcCommand);
            
            if (sent)
            {
                await _commandService.MarkCommandAsSentAsync(command.CommandId);
                _logger.LogDebug(
                    "Sent command {CommandId} to agent {AgentId}", 
                    command.CommandId, agentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Failed to send command {CommandId} to agent {AgentId}", 
                command.CommandId, agentId);
        }
    }
}