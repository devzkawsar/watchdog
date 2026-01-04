using Watchdog.Api.gRPC;
using Watchdog.Api.Protos;
using Watchdog.Api.Services;

namespace Watchdog.Api.BackgroundServices;

public class CommandDispatcherBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAgentGrpcService _agentGrpcService;
    private readonly ILogger<CommandDispatcherBackgroundService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(10);
    
    public CommandDispatcherBackgroundService(
        IServiceScopeFactory scopeFactory,
        IAgentGrpcService agentGrpcService,
        ILogger<CommandDispatcherBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
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
                using var scope = _scopeFactory.CreateScope();
                var commandService = scope.ServiceProvider.GetRequiredService<ICommandService>();
                var agentManager = scope.ServiceProvider.GetRequiredService<IAgentManager>();

                // Clean up old commands
                await commandService.CleanupOldCommandsAsync();
                
                // Dispatch pending commands to all agents
                await DispatchPendingCommandsAsync(agentManager, commandService);
                
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
    
    private async Task DispatchPendingCommandsAsync(
        IAgentManager agentManager,
        ICommandService commandService)
    {
        try
        {
            var agents = await agentManager.GetOnlineAgentsAsync();

            foreach (var agent in agents)
            {
                await DispatchCommandsToAgentAsync(agent.Id, commandService);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching pending commands");
        }
    }
    
    private async Task DispatchCommandsToAgentAsync(string agentId, ICommandService commandService)
    {
        try
        {
            var pendingCommands = await commandService.GetPendingCommandsAsync(agentId);
            
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
                AgentId = agentId,
                ApplicationId = command.ApplicationId,
                InstanceId = command.InstanceId,
                Parameters = command.Parameters,
                Timestamp = command.CreatedAt.Ticks
            };
            
            // Try to send via gRPC
            var sent = await _agentGrpcService.SendCommandToAgentAsync(agentId, grpcCommand);
            
            if (sent)
            {
                using var scope = _scopeFactory.CreateScope();
                var commandService = scope.ServiceProvider.GetRequiredService<ICommandService>();
                await commandService.MarkCommandAsSentAsync(command.CommandId);
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