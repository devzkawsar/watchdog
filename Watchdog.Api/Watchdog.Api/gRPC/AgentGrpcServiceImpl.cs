using System.Collections.Concurrent;
using System.Data;
using Dapper;
using Grpc.Core;
using System.Text.Json;
using Watchdog.Api.Data;
using Watchdog.Api.Interface;
using Watchdog.Api.Protos;
using Watchdog.Api.Services;

namespace Watchdog.Api.gRPC;

public class AgentGrpcServiceImpl : AgentService.AgentServiceBase
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IAgentManager _agentManager;
    private readonly IApplicationManager _applicationManager;
    private readonly IApplicationRepository _applicationRepository;
    private readonly ICommandService _commandService;
    private readonly IAgentGrpcService _agentGrpcService;
    private readonly ILogger<AgentGrpcServiceImpl> _logger;

    public AgentGrpcServiceImpl(
        IDbConnectionFactory connectionFactory,
        IAgentManager agentManager,
        IApplicationManager applicationManager,
        IApplicationRepository applicationRepository,
        ICommandService commandService,
        IAgentGrpcService agentGrpcService,
        ILogger<AgentGrpcServiceImpl> logger)
    {
        _connectionFactory = connectionFactory;
        _agentManager = agentManager;
        _applicationManager = applicationManager;
        _applicationRepository = applicationRepository;
        _commandService = commandService;
        _agentGrpcService = agentGrpcService;
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
            
            var agent = await _agentManager.RegisterAgent(agentRegistration);
            
            // Get assigned applications
            var assignedApplications = await _agentManager.GetAgentApplications(agent.Id);
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
                    assignment.Ports.Add(new Watchdog.Api.Protos.PortRequirement
                    {
                        Name = portReq.Name,
                        InternalPort = portReq.InternalPort,
                        Protocol = portReq.Protocol,
                        Required = portReq.Required
                    });
                }
            }
            // Get active instances for this agent
            var activeInstances = await _applicationRepository.GetActiveInstancesForAgent(agent.Id);
            var activeInstanceStatuses = activeInstances.Select(i => new Watchdog.Api.Protos.ApplicationStatus
            {
                InstanceId = i.InstanceId,
                ApplicationId = i.ApplicationId,
                Status = i.Status,
                CpuPercent = i.CpuPercent ?? 0,
                MemoryMb = i.MemoryMB ?? 0,
                HealthStatus = "Unknown", // API doesn't store this explicitly properly yet, inferred from status
                StartTime = i.StartedAt.HasValue ? ((DateTimeOffset)i.StartedAt.Value).ToUnixTimeSeconds() : 0,
                ProcessId = i.ProcessId?.ToString() ?? "0"
            }).ToList();
            
            // Add assigned ports
            foreach (var instanceStatus in activeInstanceStatuses)
            {
                var originalInstance = activeInstances.First(i => i.InstanceId == instanceStatus.InstanceId);
                if (!string.IsNullOrEmpty(originalInstance.AssignedPorts))
                {
                    try 
                    {
                        var ports = JsonSerializer.Deserialize<List<PortMapping>>(originalInstance.AssignedPorts);
                        if (ports != null)
                        {
                             instanceStatus.Ports.AddRange(ports.Select(p => new Watchdog.Api.Protos.PortMapping
                             {
                                 Name = p.Name ?? "",
                                 InternalPort = p.InternalPort,
                                 ExternalPort = p.ExternalPort,
                                 Protocol = p.Protocol ?? "TCP"
                             }));
                        }
                    }
                    catch
                    {
                        // Ignore parsing errors
                    }
                }
            }
            
            return new AgentRegistrationResponse
            {
                Success = true,
                Message = "Registration successful",
                AssignedId = agent.Id,
                Applications = { applicationAssignments },
                ActiveInstances = { activeInstanceStatuses }
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
            await _agentManager.UpdateAgentHeartbeat(request.AgentId);
            
            // Process application statuses
            foreach (var appStatus in request.ApplicationStatuses)
            {
                int? processId = null;
                if (int.TryParse(appStatus.ProcessId, out int pid) && pid > 0)
                {
                    processId = pid;
                }

                await _applicationManager.UpdateInstanceStatus(
                    appStatus.InstanceId,
                    appStatus.Status,
                    appStatus.CpuPercent,
                    appStatus.MemoryMb,
                    processId);
                
                // If application just spawned, record ports
                if (appStatus.Status == "Running" && appStatus.Ports.Any())
                {
                    await RecordApplicationPortsAsync(
                        appStatus.InstanceId,
                        appStatus.Ports.ToList());
                }
            }
            
            return new StatusReportResponse
            {
                Success = true,
                Message = "Status report processed"
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
            var connectionId = context.Peer; // Use Peer as connection ID
            _agentGrpcService.RegisterAgentConnection(agentId, responseStream, connectionId);

            // Streaming-only command delivery:
            // Flush any queued (Pending) commands once on connect.
            await SendPendingCommandsToAgentAsync(agentId, responseStream);
            
            // Process incoming messages
            await foreach (var message in requestStream.ReadAllAsync())
            {
                await ProcessAgentMessageAsync(agentId, message);
                await _agentGrpcService.ProcessAgentMessageAsync(agentId, message);
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
                _agentGrpcService.UnregisterAgentConnection(agentId);
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
            
            // Send command to agent via singleton service if connected
            var sent = await _agentGrpcService.SendCommandToAgentAsync(request.AgentId, request);
            
            if (sent)
            {
                // We don't mark as sent here because AgentGrpcService doesn't do it.
                // Actually CommandService.QueueSpawnCommand calls TrySendCommandImmediately which marks as sent.
                // But this is ExecuteCommand (likely from REST API).
                
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
                await _commandService.QueueCommand(new CommandQueueItem
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
    
    // This is now handled by AgentGrpcService
    
    private async Task ProcessAgentMessageAsync(string agentId, AgentMessage message)
    {
        try
        {
            switch (message.MessageCase)
            {
                case AgentMessage.MessageOneofCase.Heartbeat:
                    await _agentManager.UpdateAgentHeartbeat(agentId);
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
                    await _applicationManager.UpdateInstanceStatus(
                        stopped.InstanceId, "Stopped");
                    break;
                    
                case AgentMessage.MessageOneofCase.Error:
                    var error = message.Error;
                    _logger.LogError(
                        "Error from agent {AgentId}: {ErrorType} - {ErrorMessage} (Instance: {InstanceId})",
                        agentId, error.ErrorType, error.ErrorMessage, error.InstanceId);
                        
                    // Auto-restart logic for critical errors
                    if (!string.IsNullOrEmpty(error.InstanceId) && IsCriticalError(error.ErrorType))
                    {
                        _logger.LogWarning("Auto-restarting instance {InstanceId} due to critical error: {ErrorType}", 
                            error.InstanceId, error.ErrorType);
                            
                        // Use the high-level QueueRestartCommand which triggers immediate delivery
                        await _commandService.QueueRestartCommand(agentId, error.ApplicationId, error.InstanceId);
                    }
                    else if (string.IsNullOrEmpty(error.InstanceId) && IsCriticalError(error.ErrorType))
                    {
                        _logger.LogWarning("Received critical error {ErrorType} but no InstanceId was provided. Cannot auto-restart.", error.ErrorType);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message from agent {AgentId}", agentId);
        }
    }
    
    private bool IsCriticalError(string errorType)
    {
        return errorType switch
        {
            "HealthCheckFailed" => true,
            "ProcessExited" => true,
            "ResourceExceeded" => true,
            "HungProcess" => true,
            _ => false
        };
    }
    
    private async Task SendPendingCommandsToAgentAsync(
        string agentId, IServerStreamWriter<ControlPlaneMessage> responseStream)
    {
        var pendingCommands = await _commandService.GetPendingCommands(agentId);
        
        foreach (var cmd in pendingCommands)
        {
            try
            {
                var controlMessage = await ConvertToControlPlaneMessageAsync(cmd);
                await responseStream.WriteAsync(controlMessage);
                
                await _commandService.MarkCommandAsSent(cmd.CommandId);
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
            AgentMessage.MessageOneofCase.Spawned => ExtractAgentIdFromInstanceId(message.Spawned.InstanceId),
            AgentMessage.MessageOneofCase.Stopped => ExtractAgentIdFromInstanceId(message.Stopped.InstanceId),
            AgentMessage.MessageOneofCase.Error => message.Error.AgentId,
            _ => string.Empty
        };
    }

    private static string ExtractAgentIdFromInstanceId(string instanceId)
    {
        // Instance ID format: appId-agentId-uuid
        var parts = instanceId.Split('-');
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }
    
    private async Task RecordInstanceSpawnedAsync(string agentId, ApplicationSpawned spawned)
    {
        using var connection = _connectionFactory.CreateConnection();
        
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
        using var connection = _connectionFactory.CreateConnection();
        
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
    
    private Task<ControlPlaneMessage> ConvertToControlPlaneMessageAsync(CommandQueueItem cmd)
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
                message.Kill = new KillCommand
                {
                    InstanceId = cmd.InstanceId,
                    Force = true,
                    TimeoutSeconds = 30
                };
                break;
                
            case "RESTART":
                var restartParams = JsonSerializer.Deserialize<RestartCommandParams>(cmd.Parameters);
                message.Restart = new RestartCommand
                {
                    InstanceId = cmd.InstanceId,
                    TimeoutSeconds = restartParams?.TimeoutSeconds ?? 30
                };
                break;
        }
        
        return Task.FromResult(message);
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
    public int HealthCheckInterval { get; set; } = 30;
    public int InstanceIndex { get; set; }
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
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}