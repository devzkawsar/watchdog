using System.Collections.Concurrent;
using Grpc.Core;
using System.Text.Json;
using  Watchdog.Api.Protos;
using Watchdog.Api.Services;

namespace Watchdog.Api.gRPC;

public class AgentGrpcServiceImpl : AgentService.AgentServiceBase
{
    private readonly IAgentManager _agentManager;
    private readonly IApplicationManager _applicationManager;
    private readonly ICommandService _commandService;
    private readonly ILogger<AgentGrpcServiceImpl> _logger;
    
    // Track connected agents
    private static readonly ConcurrentDictionary<string, 
        (IServerStreamWriter<ControlPlaneMessa> Stream, DateTime LastSeen)> 
        _connectedAgents = new();
    
    public AgentGrpcServiceImpl(
        IAgentManager agentManager,
        IApplicationManager applicationManager,
        ICommandService commandService,
        ILogger<AgentGrpcServiceImpl> logger)
    {
        _agentManager = agentManager;
        _applicationManager = applicationManager;
        _commandService = commandService;
        _logger = logger;
    }
    
    // Agent registration
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
    
    // Status reporting
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
            var pendingCommands = await _commandService.GetPendingCommandsAsync(request.AgentId);
            var grpcCommands = pendingCommands.Select(cmd => new CommandRequest
            {
                CommandId = cmd.CommandId,
                CommandType = cmd.CommandType,
                ApplicationId = cmd.ApplicationId,
                InstanceId = cmd.InstanceId,
                Parameters = cmd.Parameters,
                Timestamp = cmd.CreatedAt.Ticks
            }).ToList();
            
            return new StatusReportResponse
            {
                Success = true,
                Message = "Status report processed",
                PendingCommands = { grpcCommands }
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
    
    // Bi-directional streaming
    public override async Task CommandStream(
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ControlPlaneMessage> responseStream,
        ServerCallContext context)
    {
        var agentId = string.Empty;
        
        try
        {
            // Wait for first message to identify agent
            if (!await requestStream.MoveNext())
                return;
            
            // Process first message
            var firstMessage = requestStream.Current;
            agentId = ExtractAgentIdFromMessage(firstMessage);
            
            if (string.IsNullOrEmpty(agentId))
            {
                _logger.LogWarning("Received message without agent ID");
                return;
            }
            
            _logger.LogInformation("Agent {AgentId} connected via streaming", agentId);
            
            // Register agent stream
            _connectedAgents[agentId] = (responseStream, DateTime.UtcNow);
            
            // Process incoming messages
            await foreach (var message in requestStream.ReadAllAsync())
            {
                await ProcessAgentMessageAsync(agentId, message);
                
                // Update last seen
                if (_connectedAgents.ContainsKey(agentId))
                {
                    _connectedAgents[agentId] = (responseStream, DateTime.UtcNow);
                }
                
                // Send any pending commands
                await SendPendingCommandsToAgentAsync(agentId, responseStream);
            }
        }
        catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Agent {AgentId} disconnected from streaming", agentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in streaming connection for agent {AgentId}", agentId);
        }
        finally
        {
            // Clean up
            if (!string.IsNullOrEmpty(agentId))
                _connectedAgents.TryRemove(agentId, out _);
        }
    }
    
    // Execute specific command
    public override async Task<CommandResponse> ExecuteCommand(
        CommandRequest request, ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Executing command {CommandType} for {InstanceId}",
                request.CommandType, request.InstanceId);
            
            // Send command to agent via stream if connected
            if (_connectedAgents.TryGetValue(request.AgentId, out var agentStream))
            {
                var controlMessage = new ControlPlaneMessage();
                
                switch (request.CommandType)
                {
                    case "SPAWN":
                        var spawnParams = JsonSerializer.Deserialize<SpawnCommandParams>(
                            request.Parameters);
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
                                HealthCheckUrl = spawnParams.HealthCheckUrl
                            };
                        }
                        break;
                        
                    case "KILL":
                        var killParams = JsonSerializer.Deserialize<KillCommandParams>(
                            request.Parameters);
                        if (killParams != null)
                        {
                            controlMessage.Kill = new KillCommand
                            {
                                InstanceId = request.InstanceId,
                                Force = killParams.Force,
                                TimeoutSeconds = killParams.TimeoutSeconds
                            };
                        }
                        break;
                        
                    case "RESTART":
                        var restartParams = JsonSerializer.Deserialize<RestartCommandParams>(
                            request.Parameters);
                        if (restartParams != null)
                        {
                            controlMessage.Restart = new RestartCommand
                            {
                                InstanceId = request.InstanceId,
                                TimeoutSeconds = restartParams.TimeoutSeconds
                            };
                        }
                        break;
                }
                
                await agentStream.Stream.WriteAsync(controlMessage);
                
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
                await _commandService.QueueCommandAsync(new CommandQueueItem
                {
                    CommandId = request.CommandId,
                    CommandType = request.CommandType,
                    AgentId = request.AgentId,
                    ApplicationId = request.ApplicationId,
                    InstanceId = request.InstanceId,
                    Parameters = request.Parameters,
                    Status = "Pending"
                });
                
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
    
    public async Task SendCommandToAgentAsync(string agentId, ControlPlaneMessage message)
    {
        if (_connectedAgents.TryGetValue(agentId, out var agentStream))
        {
            try
            {
                await agentStream.Stream.WriteAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send command to agent {AgentId}", agentId);
            }
        }
    }
    
    private async Task ProcessAgentMessageAsync(string agentId, AgentMessage message)
    {
        try
        {
            switch (message.MessageCase)
            {
                case AgentMessage.MessageOneofCase.Heartbeat:
                    await _agentManager.UpdateAgentHeartbeatAsync(agentId);
                    break;
                    
                case AgentMessage.MessageOneofCase.Spawned:
                    var spawned = message.Spawned;
                    _logger.LogInformation(
                        "Application {ApplicationId} instance {InstanceId} spawned on agent {AgentId} (PID: {ProcessId})",
                        spawned.ApplicationId, spawned.InstanceId, agentId, spawned.ProcessId);
                    
                    // Record instance in database
                    await RecordInstanceSpawnedAsync(agentId, spawned);
                    break;
                    
                case AgentMessage.MessageOneofCase.Stopped:
                    var stopped = message.Stopped;
                    _logger.LogInformation(
                        "Application {ApplicationId} instance {InstanceId} stopped on agent {AgentId} (Exit code: {ExitCode})",
                        stopped.ApplicationId, stopped.InstanceId, agentId, stopped.ExitCode);
                    
                    // Update instance status
                    await _applicationManager.UpdateInstanceStatusAsync(
                        stopped.InstanceId, "Stopped");
                    break;
                    
                case AgentMessage.MessageOneofCase.Error:
                    var error = message.Error;
                    _logger.LogError(
                        "Error from agent {AgentId}: {ErrorType} - {ErrorMessage}",
                        agentId, error.ErrorType, error.ErrorMessage);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message from agent {AgentId}", agentId);
        }
    }
    
    private async Task SendPendingCommandsToAgentAsync(
        string agentId, IServerStreamWriter<ControlPlaneMessage> responseStream)
    {
        var pendingCommands = await _commandService.GetPendingCommandsAsync(agentId);
        
        foreach (var cmd in pendingCommands)
        {
            try
            {
                var controlMessage = await ConvertToControlPlaneMessageAsync(cmd);
                await responseStream.WriteAsync(controlMessage);
                
                await _commandService.MarkCommandAsSentAsync(cmd.CommandId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send command to agent {AgentId}", agentId);
            }
        }
    }
    
    private string ExtractAgentIdFromMessage(AgentMessage message)
    {
        return message.MessageCase switch
        {
            AgentMessage.MessageOneofCase.Heartbeat => message.Heartbeat.AgentId,
            AgentMessage.MessageOneofCase.Spawned => message.Spawned.ApplicationId.Split('-')[1], // Parse from instance ID
            AgentMessage.MessageOneofCase.Stopped => message.Stopped.ApplicationId.Split('-')[1],
            AgentMessage.MessageOneofCase.Error => message.Error.AgentId,
            _ => string.Empty
        };
    }
    
    private async Task RecordInstanceSpawnedAsync(string agentId, ApplicationSpawned spawned)
    {
        using var connection = new SqlConnection(_connectionString);
        
        const string sql = @"
            INSERT INTO ApplicationInstances 
                (InstanceId, ApplicationId, AgentId, ProcessId, Status, 
                 AssignedPorts, StartedAt, CreatedAt)
            VALUES 
                (@InstanceId, @ApplicationId, @AgentId, @ProcessId, 'Running',
                 @AssignedPorts, @StartedAt, GETUTCDATE())";
        
        await connection.ExecuteAsync(sql, new
        {
            InstanceId = spawned.InstanceId,
            ApplicationId = spawned.ApplicationId,
            AgentId = agentId,
            ProcessId = spawned.ProcessId,
            AssignedPorts = JsonSerializer.Serialize(spawned.Ports),
            StartedAt = DateTime.UnixEpoch.AddSeconds(spawned.StartTime)
        });
    }
    
    private async Task RecordApplicationPortsAsync(string instanceId, List<PortMapping> ports)
    {
        using var connection = new SqlConnection(_connectionString);
        
        const string sql = @"
            UPDATE ApplicationInstances 
            SET AssignedPorts = @AssignedPorts
            WHERE InstanceId = @InstanceId";
        
        await connection.ExecuteAsync(sql, new
        {
            InstanceId = instanceId,
            AssignedPorts = JsonSerializer.Serialize(ports)
        });
    }
    
    private async Task<ControlPlaneMessage> ConvertToControlPlaneMessageAsync(CommandQueueItem cmd)
    {
        var message = new ControlPlaneMessage();
        
        switch (cmd.CommandType)
        {
            case "SPAWN":
                var spawnParams = JsonSerializer.Deserialize<SpawnCommandParams>(cmd.Parameters);
                if (spawnParams != null)
                {
                    message.Spawn = new SpawnCommand
                    {
                        ApplicationId = cmd.ApplicationId,
                        InstanceId = cmd.InstanceId,
                        ExecutablePath = spawnParams.ExecutablePath,
                        Arguments = spawnParams.Arguments,
                        WorkingDirectory = spawnParams.WorkingDirectory,
                        EnvironmentVariables = { spawnParams.EnvironmentVariables }
                    };
                }
                break;
                
            case "KILL":
                message.Kill = new KillCommand
                {
                    InstanceId = cmd.InstanceId,
                    Force = true,
                    TimeoutSeconds = 30
                };
                break;
        }
        
        return message;
    }
}

public class SpawnCommandParams
{
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public List<PortMapping> Ports { get; set; } = new();
    public string HealthCheckUrl { get; set; } = string.Empty;
}

public class KillCommandParams
{
    public bool Force { get; set; } = false;
    public int TimeoutSeconds { get; set; } = 30;
}

public class RestartCommandParams
{
    public int TimeoutSeconds { get; set; } = 30;
}

public class CommandQueueItem
{
    public string CommandId { get; set; } = Guid.NewGuid().ToString();
    public string CommandType { get; set; } = string.Empty; // SPAWN, KILL, RESTART
    public string AgentId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string Parameters { get; set; } = string.Empty; // JSON
    public string Status { get; set; } = "Pending"; // Pending, Sent, Executed, Failed
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public string? ErrorMessage { get; set; }
}