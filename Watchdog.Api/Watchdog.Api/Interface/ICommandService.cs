using Watchdog.Api.Data;
using Watchdog.Api.gRPC;

namespace Watchdog.Api.Interface;

public interface ICommandService
{
    Task<CommandQueueItem> QueueSpawnCommand(
        string agentId, 
        Application application, 
        string instanceId, 
        int instanceIndex);
    
    Task<CommandQueueItem> QueueKillCommand(
        string agentId, 
        string applicationId, 
        string instanceId);
    
    Task<CommandQueueItem> QueueRestartCommand(
        string agentId, 
        string applicationId, 
        string instanceId);
    
    Task<List<CommandQueueItem>> GetPendingCommands(string agentId);
    Task<bool> MarkCommandAsSent(string commandId);
    Task<bool> MarkCommandAsExecuted(string commandId, bool success, string? result = null, string? error = null);
    Task<bool> QueueCommand(CommandQueueItem command);
    Task CleanupOldCommands();
}