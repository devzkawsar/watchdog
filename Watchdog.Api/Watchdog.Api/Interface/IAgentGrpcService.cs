using Grpc.Core;
using Watchdog.Api.Protos;

namespace Watchdog.Api.Interface;

public interface IAgentGrpcService
{
    void RegisterAgentConnection(string agentId, IServerStreamWriter<ControlPlaneMessage> responseStream, string connectionId);
    void UnregisterAgentConnection(string agentId);
    Task ProcessAgentMessageAsync(string agentId, AgentMessage message);
    Task<bool> SendCommandToAgentAsync(string agentId, CommandRequest request);
    Task CleanupStaleConnectionsAsync();
}