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
                BuiltInPort = app.BuiltInPort ?? 0,
                EnvironmentVariables = { app.EnvironmentVariables },
            }).ToList();
            

            var assignedAppIds = assignedApplications.Select(a => a.Id).ToList();
            if (assignedAppIds.Any())
            {
                var orphanInstances = await _applicationRepository.GetOrphanInstances(assignedAppIds);
                foreach (var orphan in orphanInstances)
                {
                    try
                    {
                        var newInstanceId = $"{request.Hostname}-{request.AgentName}-{Guid.NewGuid().ToString("N")}";
                        
                        _logger.LogInformation("Claiming orphan instance {OldId} as {NewId} for agent {agent}", 
                            orphan.InstanceId, newInstanceId, agent.Id);
                        
                        await _applicationRepository.ClaimOrphanInstance(orphan.InstanceId, newInstanceId, agent.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to claim orphan instance {InstanceId}", orphan.InstanceId);
                    }
                }
            }
           
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
                if (originalInstance.AssignedPort.HasValue && originalInstance.AssignedPort.Value > 0)
                {
                     instanceStatus.AssignedPort = originalInstance.AssignedPort.Value;
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
    
    public override async Task<StatusReportResponse> ReportStatus(
        StatusReportRequest request, ServerCallContext context)
    {
        try
        {
            using var connection = _connectionFactory.CreateConnection();

            // Update agent heartbeat
            await _agentManager.UpdateAgentHeartbeat(request.AgentId);
            
            // Process application statuses
            foreach (var appStatus in request.ApplicationStatuses)
            {
                // Ensure we have a valid instance ID
                if (string.IsNullOrEmpty(appStatus.InstanceId)) continue;
                
                // Update instance status in DB
                int.TryParse(appStatus.ProcessId, out int processId);

                // If this is an adopted instance, check for and remove any orphan record for the same process
                if (appStatus.InstanceId.Contains("-adopted-") && processId > 0)
                {
                    const string findOrphanSql = @"
                        SELECT instance_id 
                        FROM application_instance 
                        WHERE application_id = @ApplicationId 
                          AND process_id = @ProcessId 
                          AND agent_id IS NULL";
                    
                    var orphanId = await connection.QueryFirstOrDefaultAsync<string>(findOrphanSql, new 
                    { 
                        ApplicationId = appStatus.ApplicationId,
                        ProcessId = processId
                    });

                    if (!string.IsNullOrEmpty(orphanId))
                    {
                        await connection.ExecuteAsync("DELETE FROM application_instance WHERE instance_id = @OrphanId", new { OrphanId = orphanId });
                        _logger.LogInformation("Removed orphan instance record {OrphanId} in favor of adopted instance {AdoptedId}", orphanId, appStatus.InstanceId);
                    }
                }
                
                await _applicationRepository.UpdateInstanceStatus(
                    appStatus.InstanceId, 
                    NormalizeStatus(appStatus.Status),
                    appStatus.CpuPercent,
                    appStatus.MemoryMb,
                    processId > 0 ? processId : null,
                    request.AgentId);
                
                // If port is reported, update it as well
                if (appStatus.AssignedPort > 0)
                {
                    await RecordApplicationPortsAsync(appStatus.InstanceId, appStatus.AssignedPort);
                }
            }
            
            // Get pending commands
            var pendingCommands = await _commandService.GetPendingCommands(request.AgentId);
            var commandRequests = new List<CommandRequest>();
            
            foreach (var cmd in pendingCommands)
            {
                // Convert to gRPC command
                var grpcCommand = new CommandRequest
                {
                    CommandId = cmd.CommandId,
                    CommandType = cmd.CommandType,
                    ApplicationId = cmd.ApplicationId,
                    InstanceId = cmd.InstanceId,
                    Parameters = cmd.Parameters,
                    Timestamp = cmd.CreatedAt.Ticks,
                    AgentId = cmd.AgentId
                };
                
                commandRequests.Add(grpcCommand);
                
                // Mark as sent
                await _commandService.MarkCommandAsSent(cmd.CommandId);
            }
            
            return new StatusReportResponse
            {
                Success = true,
                Message = "Status processed successfully",
                PendingCommands = { commandRequests }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting status for agent {AgentId}", request.AgentId);
            
            return new StatusReportResponse
            {
                Success = false,
                Message = $"Error processing status: {ex.Message}"
            };
        }
    }

    public override async Task CommandStream(
        IAsyncStreamReader<AgentMessage> requestStream, 
        IServerStreamWriter<ControlPlaneMessage> responseStream, 
        ServerCallContext context)
    {
        string? agentId = null;
        string connectionId = Guid.NewGuid().ToString();
        
        try
        {
            _logger.LogInformation("New agent connection started (ConnectionId: {ConnectionId})", connectionId);
            
            // Process incoming messages
            await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
            {
                // Identify agent from first message or heartbeat
                if (agentId == null)
                {
                    if (message.MessageCase == AgentMessage.MessageOneofCase.Heartbeat)
                    {
                        agentId = message.Heartbeat.AgentId;
                        _agentGrpcService.RegisterAgentConnection(agentId, responseStream, connectionId);
                        _logger.LogInformation("Agent {AgentId} identified and registered (ConnectionId: {ConnectionId})", 
                            agentId, connectionId);
                    }
                    else if (message.MessageCase == AgentMessage.MessageOneofCase.Error)
                    {
                        agentId = message.Error.AgentId;
                        // Don't register connection yet if we only got an error, wait for heartbeat
                    }
                }
                
                // Update last seen
                if (agentId != null)
                {
                    await _agentGrpcService.ProcessAgentMessageAsync(agentId, message);
                }
                
                // Process message
                switch (message.MessageCase)
                {
                    case AgentMessage.MessageOneofCase.Heartbeat:
                        // Heartbeat already handled for keepalive
                        break;
                        
                    case AgentMessage.MessageOneofCase.Spawned:
                        _logger.LogInformation("Received application spawned report from {AgentId}: {AppId} instance {InstanceId} (PID: {Pid})", 
                            agentId, message.Spawned.ApplicationId, message.Spawned.InstanceId, message.Spawned.ProcessId);
                            
                        if (agentId != null)
                        {
                            await RecordInstanceSpawnedAsync(agentId, message.Spawned);
                            
                            // Check for pending commands to execute next
                            // Note: Commands are pushed via AgentGrpcService, so we don't need to poll here explicitly
                        }
                        break;
                        
                    case AgentMessage.MessageOneofCase.Stopped:
                        _logger.LogInformation("Received application stopped report from {AgentId}: {AppId} instance {InstanceId} (Exit: {ExitCode})", 
                            agentId, message.Stopped.ApplicationId, message.Stopped.InstanceId, message.Stopped.ExitCode);
                            
                        if (agentId != null)
                        {
                            await RecordInstanceStoppedAsync(agentId, message.Stopped);
                        }
                        break;
                        
                    case AgentMessage.MessageOneofCase.Error:
                        _logger.LogError("Received error from {AgentId}: {ErrorType} - {ErrorMessage}", 
                            message.Error.AgentId, message.Error.ErrorType, message.Error.ErrorMessage);
                            
                        if (agentId != null)
                        {
                            await RecordInstanceErrorAsync(agentId, message.Error);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Agent connection canceled (AgentId: {AgentId}, ConnectionId: {ConnectionId})", 
                agentId ?? "Unknown", connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in command stream (AgentId: {AgentId}, ConnectionId: {ConnectionId})", 
                agentId ?? "Unknown", connectionId);
        }
        finally
        {
            if (agentId != null)
            {
                _agentGrpcService.UnregisterAgentConnection(agentId);
                _logger.LogInformation("Agent {AgentId} disconnected (ConnectionId: {ConnectionId})", 
                    agentId, connectionId);
            }
        }
    }

    private async Task RecordInstanceSpawnedAsync(string agentId, ApplicationSpawned spawned)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            IF EXISTS (SELECT 1 FROM application_instance WHERE instance_id = @InstanceId)
            BEGIN
                UPDATE application_instance 
                SET process_id = @ProcessId, 
                    agent_id = @AgentId,
                    status = 'running', 
                    assigned_port = @AssignedPort, 
                    started_at = @StartedAt,
                    last_heartbeat = GETUTCDATE(),
                    updated_at = GETUTCDATE()
                WHERE instance_id = @InstanceId
            END
            ELSE
            BEGIN
                INSERT INTO application_instance 
                    (instance_id, application_id, agent_id, process_id, status, 
                     assigned_port, started_at, created_at, last_heartbeat)
                VALUES 
                    (@InstanceId, @ApplicationId, @AgentId, @ProcessId, 'running',
                     @AssignedPort, @StartedAt, GETUTCDATE(), GETUTCDATE())
            END";
        
        // Extract assigned port
        int assignedPort = spawned.AssignedPort;

        await connection.ExecuteAsync(sql, new
        {
            InstanceId = spawned.InstanceId,
            ApplicationId = spawned.ApplicationId,
            AgentId = agentId,
            ProcessId = spawned.ProcessId,
            AssignedPort = assignedPort,
            StartedAt = DateTime.UnixEpoch.AddSeconds(spawned.StartTime)
        });
    }
    
    private async Task RecordApplicationPortsAsync(string instanceId, int assignedPort)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            UPDATE application_instance 
            SET assigned_port = @AssignedPort,
                updated_at = GETUTCDATE()
            WHERE instance_id = @InstanceId";
            
        // Use the passed port
        await connection.ExecuteAsync(sql, new
        {
            InstanceId = instanceId,
            AssignedPort = assignedPort
        });
    }

    private async Task RecordInstanceStoppedAsync(string agentId, ApplicationStopped stopped)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        const string sql = @"
            UPDATE application_instance 
            SET status = 'stopped', 
                updated_at = GETUTCDATE()
            WHERE instance_id = @InstanceId";
            
        await connection.ExecuteAsync(sql, new
        {
            InstanceId = stopped.InstanceId
        });
    }

    private async Task RecordInstanceErrorAsync(string agentId, ErrorReport error)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        // If we have an instance ID, update the instance status
        if (!string.IsNullOrEmpty(error.InstanceId))
        {
            const string sql = @"
                UPDATE application_instance 
                SET status = 'error', 
                    updated_at = GETUTCDATE()
                WHERE instance_id = @InstanceId";
                
            await connection.ExecuteAsync(sql, new
            {
                InstanceId = error.InstanceId
            });
        }
        
    }
    
    private Task<ControlPlaneMessage> ConvertToControlPlaneMessageAsync(CommandQueueItem cmd)
    {
        var message = new ControlPlaneMessage();
        
        switch (cmd.CommandType.ToLower())
        {
            case "spawn":
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
                        Port = spawnParams.Port,
                        HealthCheckInterval = spawnParams.HealthCheckInterval
                    };
                }
                break;
                
            case "kill":
                message.Kill = new KillCommand
                {
                    InstanceId = cmd.InstanceId,
                    Force = true,
                    TimeoutSeconds = 30
                };
                break;
                
            case "restart":
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

    private string NormalizeStatus(string status)
    {
        if (string.IsNullOrEmpty(status)) return "running";

        var allowed = new[] { "pending", "starting", "running", "stopping", "stopped", "error" };
        foreach (var s in allowed)
        {
            if (string.Equals(s, status, StringComparison.OrdinalIgnoreCase))
                return s;
        }

        return "running"; // Default to running if unknown to satisfy constraint
    }
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
    public string Status { get; set; } = "pending"; // pending, sent, executing, failed
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}