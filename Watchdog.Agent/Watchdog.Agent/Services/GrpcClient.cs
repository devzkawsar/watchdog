using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchdog.Agent.Configuration;
using Watchdog.Agent.Interface;
using Watchdog.Agent.Models;
using Watchdog.Agent.Protos;
using ApplicationStatus = Watchdog.Agent.Enums.ApplicationStatus;

namespace Watchdog.Agent.Services;

internal interface IGrpcClientInternal : IGrpcClient
{
}

public class GrpcClient : IGrpcClientInternal
{
    private readonly AgentService.AgentServiceClient _grpcClient;
    private readonly ILogger<GrpcClient> _logger;
    private readonly IOptions<ControlPlaneSettings> _controlPlaneSettings;
    private readonly IOptions<AgentSettings> _agentSettings;
    private readonly IApplicationManager _applicationManager;
    private readonly IProcessManager _processManager;
    
    private AsyncDuplexStreamingCall<AgentMessage, ControlPlaneMessage>? _stream;
    private CancellationTokenSource? _streamCts;
    private Task? _streamReceiveTask;
    private Task? _streamSendTask;
    private readonly object _connectionLock = new();
    private bool _isConnected = false;
    private int _reconnectAttempts = 0;
    private int _reconnectScheduled = 0;

    private void CleanupStream()
    {
        try
        {
            _streamCts?.Cancel();
        }
        catch
        {
            // Ignore
        }

        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // Ignore
        }

        _stream = null;
        _streamReceiveTask = null;
        _streamSendTask = null;
        _streamCts = null;
    }

    private void TriggerReconnect(string reason, Exception? exception = null)
    {
        if (System.Threading.Interlocked.Exchange(ref _reconnectScheduled, 1) == 1)
        {
            return;
        }

        if (exception != null)
        {
            _logger.LogWarning(exception, "gRPC stream ended ({Reason}); reconnecting", reason);
        }
        else
        {
            _logger.LogWarning("gRPC stream ended ({Reason}); reconnecting", reason);
        }

        lock (_connectionLock)
        {
            _isConnected = false;
        }

        CleanupStream();

        _ = Task.Run(async () =>
        {
            try
            {
                await AttemptReconnect(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnect attempt failed");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _reconnectScheduled, 0);
            }
        });
    }

    public GrpcClient(
        AgentService.AgentServiceClient grpcClient,
        ILogger<GrpcClient> logger,
        IOptions<ControlPlaneSettings> controlPlaneSettings,
        IOptions<AgentSettings> agentSettings,
        IApplicationManager applicationManager,
        IProcessManager processManager)
    {
        _grpcClient = grpcClient;
        _logger = logger;
        _controlPlaneSettings = controlPlaneSettings;
        _agentSettings = agentSettings;
        _applicationManager = applicationManager;
        _processManager = processManager;
    }

    public async Task<bool> Connect(CancellationToken cancellationToken = default)
    {
        lock (_connectionLock)
        {
            if (_isConnected)
                return true;
        }

        try
        {
            _logger.LogInformation("Connecting to control plane at {Endpoint}", 
                _controlPlaneSettings.Value.GrpcEndpoint);

            lock (_connectionLock)
            {
                _isConnected = true;
                _reconnectAttempts = 0;
            }

            _logger.LogInformation("Successfully connected to control plane");

            // Register with control plane
            var registration = await Register(cancellationToken);
            if (registration == null || !registration.Success)
            {
                _logger.LogError("Failed to register with control plane");
                await Disconnect();
                return false;
            }

            // Update applications from control plane
            if (registration.Applications.Any())
            {
                await _applicationManager.UpdateApplicationsFromControlPlane(
                    registration.Applications.ToList());
            }

            // Start command streaming
            await StartCommandStreaming(cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to control plane");

            lock (_connectionLock)
            {
                _isConnected = false;
            }

            return false;
        }
    }

    public async Task Disconnect()
    {
        try
        {
            lock (_connectionLock)
            {
                _isConnected = false;
            }

            // Wait for tasks to complete
            if (_streamReceiveTask != null)
            {
                await _streamReceiveTask.ContinueWith(t => { }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }

            if (_streamSendTask != null)
            {
                await _streamSendTask.ContinueWith(t => { }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }

            CleanupStream();

            _logger.LogInformation("Disconnected from control plane");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disconnect");
        }
    }

    public Task<bool> IsConnected()
    {
        lock (_connectionLock)
        {
            return Task.FromResult(_isConnected);
        }
    }

    public async Task<AgentRegistrationResponse?> Register(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new AgentRegistrationRequest
            {
                AgentId = _agentSettings.Value.AgentId,
                AgentName = _agentSettings.Value.AgentName,
                IpAddress = GetLocalIpAddress(),
                Hostname = Environment.MachineName,
                TotalMemoryMb = GetTotalMemoryMB(),
                CpuCores = Environment.ProcessorCount,
                OsVersion = Environment.OSVersion.ToString()
            };

            var response = await _grpcClient.RegisterAgentAsync(request, 
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Registration {Status}: {Message}", 
                response.Success ? "successful" : "failed", 
                response.Message);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register with control plane");
            return null;
        }
    }

    public async Task<bool> ReportStatus(StatusReportRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _grpcClient.ReportStatusAsync(request, 
                cancellationToken: cancellationToken);

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report status");
            return false;
        }
    }

    public async Task<bool> SendHeartbeat(CancellationToken cancellationToken = default)
    {
        try
        {
            var heartbeat = new Heartbeat
            {
                AgentId = _agentSettings.Value.AgentId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            var message = new AgentMessage
            {
                Heartbeat = heartbeat
            };

            if (_stream != null)
            {
                await _stream.RequestStream.WriteAsync(message, cancellationToken);
                return true;
            }
            else
            {
                _logger.LogDebug("gRPC stream not available for heartbeat");
                return false;
            }
        }
        catch (RpcException rpcEx)
        {
            // When the server ends the stream, gRPC can throw during write.
            // In some cases this surfaces as StatusCode=OK (stream completed).
            TriggerReconnect($"heartbeat write failed ({rpcEx.StatusCode})", rpcEx);
            return false;
        }
        catch (InvalidOperationException invEx)
        {
            // Typically thrown if writing after CompleteAsync / stream already ended
            TriggerReconnect("heartbeat write invalid operation", invEx);
            return false;
        }
        catch (Exception ex)
        {
            TriggerReconnect("heartbeat write exception", ex);
            return false;
        }
    }

    public async Task<bool> SendApplicationSpawned(ApplicationSpawned spawned, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = new AgentMessage
            {
                Spawned = spawned
            };

            if (_stream != null)
            {
                await _stream.RequestStream.WriteAsync(message, cancellationToken);

                _logger.LogInformation(
                    "Reported application spawned: {InstanceId} (PID: {ProcessId})",
                    spawned.InstanceId, spawned.ProcessId);

                return true;
            }
            else
            {
                _logger.LogWarning("gRPC stream not available for reporting application spawned");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report application spawned");
            return false;
        }
    }

    public async Task<bool> SendApplicationStopped(ApplicationStopped stopped, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = new AgentMessage
            {
                Stopped = stopped
            };

            if (_stream != null)
            {
                await _stream.RequestStream.WriteAsync(message, cancellationToken);

                _logger.LogInformation(
                    "Reported application stopped: {InstanceId} (Exit code: {ExitCode})",
                    stopped.InstanceId, stopped.ExitCode);

                return true;
            }
            else
            {
                _logger.LogWarning("gRPC stream not available for reporting application stopped");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report application stopped");
            return false;
        }
    }

    public async Task<bool> SendError(ErrorReport error, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = new AgentMessage
            {
                Error = error
            };

            if (_stream != null)
            {
                await _stream.RequestStream.WriteAsync(message, cancellationToken);

                _logger.LogError(
                    "Reported error to control plane: {ErrorType} - {ErrorMessage}",
                    error.ErrorType, error.ErrorMessage);

                return true;
            }
            else
            {
                _logger.LogWarning("gRPC stream not available for reporting error");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to report error");
            return false;
        }
    }

    public Task StartCommandStreaming(CancellationToken cancellationToken = default)
    {
        try
        {
            _streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Start bi-directional stream
            _stream = _grpcClient.CommandStream(cancellationToken: _streamCts.Token);

            // Start receiving commands
            _streamReceiveTask = Task.Run(async () => 
                await ReceiveCommands(_streamCts.Token), _streamCts.Token);

            // Start sending heartbeats
            _streamSendTask = Task.Run(async () => 
                await SendHeartbeats(_streamCts.Token), _streamCts.Token);

            _logger.LogInformation("Started gRPC command streaming");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start command streaming");
            throw;
        }

        return Task.CompletedTask;
    }

    private async Task ReceiveCommands(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var controlMessage in _stream!.ResponseStream.ReadAllAsync(cancellationToken))
            {
                await ProcessControlMessage(controlMessage, cancellationToken);
            }

            // Stream completed normally
            TriggerReconnect("response stream completed");
        }
        catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogInformation("Command stream cancelled");
        }
        catch (RpcException rpcEx)
        {
            TriggerReconnect($"response stream rpc exception ({rpcEx.StatusCode})", rpcEx);
        }
        catch (Exception ex)
        {
            TriggerReconnect("response stream exception", ex);
        }
    }

    private async Task SendHeartbeats(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var sent = await SendHeartbeat(cancellationToken);
                if (!sent)
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown
                return;
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown
                return;
            }
            catch (Exception ex)
            {
                TriggerReconnect("heartbeat loop exception", ex);
                return;
            }
        }
    }

    private Task ProcessControlMessage(ControlPlaneMessage message, CancellationToken cancellationToken)
    {
        try
        {
            switch (message.MessageCase)
            {
                case ControlPlaneMessage.MessageOneofCase.Spawn:
                    return ProcessSpawnCommand(message.Spawn, cancellationToken);
                    
                case ControlPlaneMessage.MessageOneofCase.Kill:
                    return ProcessKillCommand(message.Kill, cancellationToken);
                    
                case ControlPlaneMessage.MessageOneofCase.Restart:
                    return ProcessRestartCommand(message.Restart, cancellationToken);
                    
                default:
                    _logger.LogWarning("Received unknown control message type: {Type}", message.MessageCase);
                    return Task.CompletedTask;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing control message");
            
            // Send error report
            return SendError(new ErrorReport
            {
                AgentId = _agentSettings.Value.AgentId,
                ErrorType = "CommandProcessingError",
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace ?? string.Empty,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, cancellationToken);
        }
    }
    
    private async Task ProcessSpawnCommand(SpawnCommand command, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Processing spawn command for application {AppId} instance {InstanceId}",
            command.ApplicationId, command.InstanceId);
        
        // Create application instance
        var instance = await _applicationManager.CreateApplicationInstance(command);
        
        // Ports are provided as PortMapping in the control-plane contract
        var ports = command.Ports.ToList();
        
        // Spawn process
        var result = await _processManager.SpawnProcess(command, ports);
        
        if (result.Success)
        {
            // Update instance status
            await _applicationManager.UpdateInstanceStatus(
                command.InstanceId,
                Enums.ApplicationStatus.Running,
                result.ProcessId,
                result.Ports);
            
            // Report success to control plane
            await SendApplicationSpawned(new ApplicationSpawned
            {
                InstanceId = command.InstanceId,
                ApplicationId = command.ApplicationId,
                ProcessId = result.ProcessId ?? 0,
                Ports = { result.Ports },
                StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, cancellationToken);
            
            _logger.LogInformation(
                "Successfully spawned application {AppId} instance {InstanceId}",
                command.ApplicationId, command.InstanceId);
        }
        else
        {
            // Update instance status
            await _applicationManager.UpdateInstanceStatus(
                command.InstanceId,
                ApplicationStatus.Error);
            
            // Report error to control plane
            await SendError(new ErrorReport
            {
                AgentId = _agentSettings.Value.AgentId,
                ErrorType = "SpawnFailed",
                ErrorMessage = result.ErrorMessage ?? "Unknown error",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, cancellationToken);
            
            _logger.LogError(
                "Failed to spawn application {AppId} instance {InstanceId}: {Error}",
                command.ApplicationId, command.InstanceId, result.ErrorMessage);
        }
    }
    
    private async Task ProcessKillCommand(KillCommand command, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Processing kill command for instance {InstanceId}", 
            command.InstanceId);
        
        var success = await _processManager.KillProcess(
            command.InstanceId,
            command.Force,
            command.TimeoutSeconds);
        
        if (success)
        {
            // Report stopped to control plane
            await SendApplicationStopped(new ApplicationStopped
            {
                InstanceId = command.InstanceId,
                ApplicationId = "unknown", // Would need to get from instance
                ExitCode = 0,
                Reason = "Killed by command",
                StopTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, cancellationToken);
        }
        else
        {
            await SendError(new ErrorReport
            {
                AgentId = _agentSettings.Value.AgentId,
                ErrorType = "KillFailed",
                ErrorMessage = "Failed to kill process",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, cancellationToken);
        }
    }
    
    private async Task ProcessRestartCommand(RestartCommand command, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Processing restart command for instance {InstanceId}", 
            command.InstanceId);
        
        var success = await _processManager.RestartProcess(
            command.InstanceId,
            command.TimeoutSeconds);
        
        if (!success)
        {
            await SendError(new ErrorReport
            {
                AgentId = _agentSettings.Value.AgentId,
                ErrorType = "RestartFailed",
                ErrorMessage = "Failed to restart process",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, cancellationToken);
        }
    }
    
    private Task ProcessUpdateCommand(UpdateCommand command, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Processing update command for instance {InstanceId}", 
            command.InstanceId);
        
        // Implementation would update environment variables
        // and possibly restart the application

        return Task.CompletedTask;
    }
    
    private async Task AttemptReconnect(CancellationToken cancellationToken)
    {
        lock (_connectionLock)
        {
            if (_reconnectAttempts >= _controlPlaneSettings.Value.MaxReconnectAttempts)
            {
                _logger.LogError("Maximum reconnect attempts reached");
                return;
            }
            
            _reconnectAttempts++;
        }
        
        _logger.LogInformation(
            "Attempting to reconnect (attempt {Attempt}/{Max})", 
            _reconnectAttempts, _controlPlaneSettings.Value.MaxReconnectAttempts);
        
        await Disconnect();
        
        await Task.Delay(
            TimeSpan.FromSeconds(_controlPlaneSettings.Value.ReconnectIntervalSeconds), 
            cancellationToken);
        
        await Connect(cancellationToken);
    }
    
    private string GetLocalIpAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            return host.AddressList
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?
                .ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
    
    private int GetTotalMemoryMB()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var memoryStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memoryStatus))
                {
                    return (int)(memoryStatus.ullTotalPhys / (1024 * 1024));
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        
        return 8192; // Default 8GB
    }
    
    // Platform interop for memory info
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        
        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf(this);
        }
    }
    
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}