using System.Collections.Concurrent;
using Grpc.Core;
using System.Text.Json;
using Watchdog.Api.gRPC;
using Watchdog.Api.Interface;
using Watchdog.Api.Protos;

namespace Watchdog.Api.Services;

public class AgentGrpcService : IAgentGrpcService
{
    private readonly ILogger<AgentGrpcService> _logger;
    private readonly TimeSpan _staleThreshold = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (IServerStreamWriter<ControlPlaneMessage> Stream, DateTime LastSeen, string ConnectionId)> _connections = new();

    public AgentGrpcService(ILogger<AgentGrpcService> logger)
    {
        _logger = logger;
    }

    public void RegisterAgentConnection(string agentId, IServerStreamWriter<ControlPlaneMessage> responseStream, string connectionId)
    {
        _connections[agentId] = (responseStream, DateTime.UtcNow, connectionId);
    }

    public void UnregisterAgentConnection(string agentId)
    {
        _connections.TryRemove(agentId, out _);
    }

    public Task ProcessAgentMessageAsync(string agentId, AgentMessage message)
    {
        if (_connections.TryGetValue(agentId, out var existing))
        {
            _connections[agentId] = (existing.Stream, DateTime.UtcNow, existing.ConnectionId);
        }

        return Task.CompletedTask;
    }

    public async Task<bool> SendCommandToAgentAsync(string agentId, CommandRequest request)
    {
        if (!_connections.TryGetValue(agentId, out var connection))
            return false;

        var controlMessage = ConvertToControlPlaneMessage(request);

        try
        {
            await connection.Stream.WriteAsync(controlMessage);
            _connections[agentId] = (connection.Stream, DateTime.UtcNow, connection.ConnectionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send command to agent {AgentId} (Connection: {ConnectionId})", agentId, connection.ConnectionId);
            return false;
        }
    }

    public Task CleanupStaleConnectionsAsync()
    {
        var cutoff = DateTime.UtcNow - _staleThreshold;

        foreach (var kvp in _connections)
        {
            if (kvp.Value.LastSeen < cutoff)
            {
                _connections.TryRemove(kvp.Key, out _);
            }
        }

        return Task.CompletedTask;
    }

    private static ControlPlaneMessage ConvertToControlPlaneMessage(CommandRequest request)
    {
        var controlMessage = new ControlPlaneMessage();

        switch (request.CommandType.ToUpperInvariant())
        {
            case "SPAWN":
                var spawnParams = JsonSerializer.Deserialize<SpawnCommandParams>(request.Parameters);
                if (spawnParams != null)
                {
                    controlMessage.Spawn = new SpawnCommand
                    {
                        ApplicationId = request.ApplicationId,
                        InstanceId = request.InstanceId,
                        ExecutablePath = spawnParams.ExecutablePath,
                        Arguments = spawnParams.Arguments,
                        WorkingDirectory = spawnParams.WorkingDirectory,
                        EnvironmentVariables = { spawnParams.EnvironmentVariables },
                        Ports = { spawnParams.Ports.Select(p => new PortMapping
                        {
                            Name = p.Name,
                            InternalPort = p.InternalPort,
                            ExternalPort = p.ExternalPort,
                            Protocol = p.Protocol
                        }) },
                        HealthCheckInterval = spawnParams.HealthCheckInterval,
                        InstanceIndex = spawnParams.InstanceIndex
                    };
                }
                break;

            case "KILL":
                var killParams = JsonSerializer.Deserialize<KillCommandParams>(request.Parameters);
                controlMessage.Kill = new KillCommand
                {
                    InstanceId = request.InstanceId,
                    Force = killParams?.Force ?? true,
                    TimeoutSeconds = killParams?.TimeoutSeconds ?? 30
                };
                break;

            case "RESTART":
                var restartParams = JsonSerializer.Deserialize<RestartCommandParams>(request.Parameters);
                controlMessage.Restart = new RestartCommand
                {
                    InstanceId = request.InstanceId,
                    TimeoutSeconds = restartParams?.TimeoutSeconds ?? 30
                };
                break;
        }

        return controlMessage;
    }
}