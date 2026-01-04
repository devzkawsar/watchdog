using System.Collections.Concurrent;
using Grpc.Core;
using System.Text.Json;
using Watchdog.Api.gRPC;
using Watchdog.Api.Protos;
using Watchdog.Api.Services;

namespace Watchdog.Api.Services;

public interface IAgentGrpcService
{
    void RegisterAgentConnection(string agentId, IServerStreamWriter<ControlPlaneMessage> responseStream, string connectionId);
    void UnregisterAgentConnection(string agentId);
    Task ProcessAgentMessageAsync(string agentId, AgentMessage message);
    Task<bool> SendCommandToAgentAsync(string agentId, CommandRequest request);
    Task CleanupStaleConnectionsAsync();
}

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
                        HealthCheckUrl = spawnParams.HealthCheckUrl,
                        HealthCheckInterval = spawnParams.HealthCheckInterval
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

#if false

namespace Watchdog.Api.gRPC;

public class AgentGrpcServiceImpl : AgentService.AgentServiceBase
{
    private readonly IAgentGrpcService _agentGrpcService;
    private readonly IAgentManager _agentManager;
    private readonly IApplicationManager _applicationManager;
    private readonly ILogger<AgentGrpcServiceImpl> _logger;
    
    public AgentGrpcServiceImpl(
        IAgentGrpcService agentGrpcService,
        IAgentManager agentManager,
        IApplicationManager applicationManager,
        ILogger<AgentGrpcServiceImpl> logger)
    {
        _agentGrpcService = agentGrpcService;
        _agentManager = agentManager;
        _applicationManager = applicationManager;
        _logger = logger;
    }
    
    public override async Task<AgentRegistrationResponse> RegisterAgent(
        AgentRegistrationRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Registering agent {AgentId} from {IpAddress}", 
                request.AgentId, request.IpAddress);
            
            // Register agent in database
            var agentRegistration = new AgentRegistration
            {
                AgentId = request.AgentId,
                AgentName = request.AgentName,
                IpAddress = request.IpAddress,
                Hostname = request.Hostname,
                TotalMemoryMB = request.TotalMemoryMb,
                CpuCores = request.CpuCores,
                OsVersion = request.OsVersion
            };
            
            var agent = await _agentManager.RegisterAgentAsync(agentRegistration);
            
            // Get assigned applications
            var assignedApplications = await _agentManager.GetAgentApplicationsAsync(agent.Id);
            var applicationAssignments = assignedApplications.Select(app => new ApplicationAssignment
            {
                ApplicationId = app.Id,
                ApplicationName = app.Name,
                ExecutablePath = app.ExecutablePath,
                Arguments = app.Arguments,
                WorkingDirectory = app.WorkingDirectory,
                DesiredInstances = app.DesiredInstances,
                EnvironmentVariables = { app.EnvironmentVariables },
                HealthCheckUrl = app.HealthCheckUrl,
                HealthCheckInterval = app.HealthCheckInterval
            }).ToList();
            
            // Add port requirements
            foreach (var assignment in applicationAssignments)
            {
                var app = assignedApplications.First(a => a.Id == assignment.ApplicationId);
                foreach (var portReq in app.PortRequirements)
                {
                    assignment.Ports.Add(new PortRequirement
                    {
                        Name = portReq.Name,
                        InternalPort = portReq.InternalPort,
                        Protocol = portReq.Protocol,
                        Required = portReq.Required
                    });
                }
            }
            
            return new AgentRegistrationResponse
            {
                Success = true,
                Message = "Registration successful",
                AssignedId = agent.Id,
                Applications = { applicationAssignments }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering agent {AgentId}", request.AgentId);
            
            return new AgentRegistrationResponse
            {
                Success = false,
                Message = $"Registration failed: {ex.Message}"
            };
        }
    }
    
    public override async Task<StatusReportResponse> ReportStatus(
        StatusReportRequest request, ServerCallContext context)
    {
        try
        {
            // Update agent heartbeat
            await _agentManager.UpdateAgentHeartbeatAsync(request.AgentId);
            
            // Process application statuses
            foreach (var appStatus in request.ApplicationStatuses)
            {
                await _applicationManager.UpdateInstanceStatusAsync(
                    appStatus.InstanceId,
                    appStatus.Status,
                    appStatus.CpuPercent,
                    appStatus.MemoryMb);
                
                // If application just spawned, record ports
                if (appStatus.Status == "Running" && appStatus.Ports.Any())
                {
                    await RecordApplicationPortsAsync(
                        appStatus.InstanceId,
                        appStatus.Ports.ToList());
                }
            }
            
            // Get pending commands for this agent
            var pendingCommands = await GetPendingCommandsForAgentAsync(request.AgentId);
            
            return new StatusReportResponse
            {
                Success = true,
                Message = "Status report processed",
                PendingCommands = { pendingCommands }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing status report from agent {AgentId}", 
                request.AgentId);
            
            return new StatusReportResponse
            {
                Success = false,
                Message = $"Error: {ex.Message}"
            };
        }
    }
    
    public override async Task CommandStream(
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ControlPlaneMessage> responseStream,
        ServerCallContext context)
    {
        var agentId = string.Empty;
        var connectionId = Guid.NewGuid().ToString();
        
        try
        {
            // Wait for first message to identify agent
            if (!await requestStream.MoveNext())
                return;
            
            var firstMessage = requestStream.Current;
            agentId = ExtractAgentIdFromMessage(firstMessage);
            
            if (string.IsNullOrEmpty(agentId))
            {
                _logger.LogWarning("Received message without agent ID");
                return;
            }
            
            // Register agent stream
            _agentGrpcService.RegisterAgentConnection(agentId, responseStream, connectionId);
            
            // Process first message
            await _agentGrpcService.ProcessAgentMessageAsync(agentId, firstMessage);
            
            // Process incoming messages
            await foreach (var message in requestStream.ReadAllAsync())
            {
                await _agentGrpcService.ProcessAgentMessageAsync(agentId, message);
                
                // Send any pending commands
                await SendPendingCommandsToAgentAsync(agentId, responseStream);
            }
        }
        catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Agent {AgentId} disconnected from streaming (Connection: {ConnectionId})", 
                agentId, connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in streaming connection for agent {AgentId} (Connection: {ConnectionId})", 
                agentId, connectionId);
        }
        finally
        {
            if (!string.IsNullOrEmpty(agentId))
            {
                _agentGrpcService.UnregisterAgentConnection(agentId);
            }
        }
    }
    
    public override async Task<CommandResponse> ExecuteCommand(
        CommandRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Executing command {CommandType} for {InstanceId} via gRPC",
                request.CommandType, request.InstanceId);
            
            // Send command to agent via gRPC
            var sent = await _agentGrpcService.SendCommandToAgentAsync(request.AgentId, request);
            
            if (sent)
            {
                // Update command status in database
                await MarkCommandAsSentAsync(request.CommandId);
                
                return new CommandResponse
                {
                    CommandId = request.CommandId,
                    Success = true,
                    Message = "Command sent to agent",
                    Timestamp = DateTime.UtcNow.Ticks
                };
            }
            else
            {
                // Agent not connected, queue command
                await QueueCommandForLaterAsync(request);
                
                return new CommandResponse
                {
                    CommandId = request.CommandId,
                    Success = true,
                    Message = "Command queued (agent offline)",
                    Timestamp = DateTime.UtcNow.Ticks
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing command {CommandId}", request.CommandId);
            
            return new CommandResponse
            {
                CommandId = request.CommandId,
                Success = false,
                Message = $"Error: {ex.Message}",
                Timestamp = DateTime.UtcNow.Ticks
            };
        }
    }
    
    private string ExtractAgentIdFromMessage(AgentMessage message)
    {
        return message.MessageCase switch
        {
            AgentMessage.MessageOneofCase.Heartbeat => message.Heartbeat.AgentId,
            AgentMessage.MessageOneofCase.Spawned => ExtractAgentIdFromInstanceId(message.Spawned.InstanceId),
            AgentMessage.MessageOneofCase.Stopped => ExtractAgentIdFromInstanceId(message.Stopped.InstanceId),
            AgentMessage.MessageOneofCase.Error => message.Error.AgentId,
            _ => string.Empty
        };
    }
    
    private string ExtractAgentIdFromInstanceId(string instanceId)
    {
        // Instance ID format: appId-agentId-uuid
        var parts = instanceId.Split('-');
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }
    
    private async Task<List<CommandRequest>> GetPendingCommandsForAgentAsync(string agentId)
    {
        using var connection = new SqlConnection(_connectionString);
        
        const string sql = @"
            SELECT 
                CommandId, CommandType, AgentId, ApplicationId, 
                InstanceId, Parameters, Status
            FROM CommandQueue
            WHERE AgentId = @AgentId 
            AND Status = 'Pending'
            ORDER BY CreatedAt ASC";
        
        var commands = await connection.QueryAsync<CommandQueueItem>(sql, new { AgentId = agentId });
        
        return commands.Select(cmd => new CommandRequest
        {
            CommandId = cmd.CommandId,
            CommandType = cmd.CommandType,
            ApplicationId = cmd.ApplicationId,
            InstanceId = cmd.InstanceId,
            Parameters = cmd.Parameters,
            Timestamp = cmd.CreatedAt.Ticks
        }).ToList();
    }
    
    private async Task SendPendingCommandsToAgentAsync(
        string agentId, IServerStreamWriter<ControlPlaneMessage> responseStream)
    {
        var pendingCommands = await GetPendingCommandsForAgentAsync(agentId);
        
        foreach (var cmd in pendingCommands)
        {
            try
            {
                var controlMessage = ConvertToControlPlaneMessage(cmd);
                await responseStream.WriteAsync(controlMessage);
                
                await MarkCommandAsSentAsync(cmd.CommandId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send command to agent {AgentId}", agentId);
            }
        }
    }
    
    private async Task RecordApplicationPortsAsync(string instanceId, List<PortMapping> ports)
    {
        using var connection = new SqlConnection(_connectionString);
        
        const string sql = @"
            UPDATE ApplicationInstances 
            SET AssignedPorts = @AssignedPorts,
                LastHealthCheck = GETUTCDATE()
            WHERE InstanceId = @InstanceId";
        
        await connection.ExecuteAsync(sql, new
        {
            InstanceId = instanceId,
            AssignedPorts = JsonSerializer.Serialize(ports)
        });
    }
    
    private async Task MarkCommandAsSentAsync(string commandId)
    {
        using var connection = new SqlConnection(_connectionString);
        
        const string sql = @"
            UPDATE CommandQueue 
            SET Status = 'Sent',
                SentAt = GETUTCDATE()
            WHERE CommandId = @CommandId";
        
        await connection.ExecuteAsync(sql, new { CommandId = commandId });
    }
    
    private async Task QueueCommandForLaterAsync(CommandRequest request)
    {
        using var connection = new SqlConnection(_connectionString);
        
        const string sql = @"
            INSERT INTO CommandQueue 
                (CommandId, CommandType, AgentId, ApplicationId, 
                 InstanceId, Parameters, Status, CreatedAt)
            VALUES 
                (@CommandId, @CommandType, @AgentId, @ApplicationId, 
                 @InstanceId, @Parameters, 'Pending', GETUTCDATE())";
        
        await connection.ExecuteAsync(sql, new
        {
            CommandId = request.CommandId,
            CommandType = request.CommandType,
            AgentId = request.AgentId,
            ApplicationId = request.ApplicationId,
            InstanceId = request.InstanceId,
            Parameters = request.Parameters
        });
    }
    
    private ControlPlaneMessage ConvertToControlPlaneMessage(CommandRequest request)
    {
        // Reuse the conversion logic from AgentGrpcService
        var agentGrpcService = context.GetHttpContext().RequestServices.GetService<IAgentGrpcService>();
        
        // Since we can't easily access the conversion method, we'll implement it here
        var controlMessage = new ControlPlaneMessage();
        
        try
        {
            switch (request.CommandType.ToUpper())
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
                            })},
                            HealthCheckUrl = spawnParams.HealthCheckUrl,
                            HealthCheckInterval = spawnParams.HealthCheckInterval
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting command to ControlPlaneMessage");
        }
        
        return controlMessage;
    }
}
#endif