using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Watchdog.Agent.Configuration;
using Watchdog.Agent.Interface;
using Watchdog.Agent.Models;
using Watchdog.Agent.Protos;
using ApplicationStatus = Watchdog.Agent.Enums.ApplicationStatus;

namespace Watchdog.Agent.Services;

internal interface ICommandExecutorInternal : ICommandExecutor
{
}

public class CommandExecutor : ICommandExecutorInternal
{
    private readonly ILogger<CommandExecutor> _logger;
    private readonly IProcessManager _processManager;
    private readonly IApplicationManager _applicationManager;
    private readonly IGrpcClient _grpcClient;
    private readonly IOptions<AgentSettings> _agentSettings;

    public CommandExecutor(
        ILogger<CommandExecutor> logger,
        IProcessManager processManager,
        IApplicationManager applicationManager,
        IGrpcClient grpcClient,
        IOptions<AgentSettings> agentSettings)
    {
        _logger = logger;
        _processManager = processManager;
        _applicationManager = applicationManager;
        _grpcClient = grpcClient;
        _agentSettings = agentSettings;
    }

    public async Task ExecuteCommand(CommandRequest command, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Received pending command {CommandId} ({CommandType}) for app {AppId} instance {InstanceId}",
            command.CommandId,
            command.CommandType,
            command.ApplicationId,
            command.InstanceId);

        try
        {
            var commandType = (command.CommandType ?? string.Empty).Trim().ToUpperInvariant();

            switch (commandType)
            {
                case "SPAWN":
                    await ExecuteSpawn(command, cancellationToken);
                    break;

                case "KILL":
                    await ExecuteKill(command, cancellationToken);
                    break;

                case "RESTART":
                    await ExecuteRestart(command, cancellationToken);
                    break;

                case "UPDATE":
                    await ExecuteUpdate(command, cancellationToken);
                    break;

                default:
                    _logger.LogWarning("Unknown command type {CommandType} for command {CommandId}", commandType, command.CommandId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed executing command {CommandId} ({CommandType})", command.CommandId, command.CommandType);

            await _grpcClient.SendError(new ErrorReport
            {
                AgentId = _agentSettings.Value.AgentId,
                ErrorType = "CommandExecutionFailed",
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace ?? string.Empty,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, cancellationToken);
        }
    }

    private async Task ExecuteSpawn(CommandRequest command, CancellationToken cancellationToken)
    {
        var parameters = DeserializeJson<SpawnCommandParams>(command.Parameters);
        if (parameters == null)
        {
            throw new InvalidOperationException("Invalid SPAWN parameters");
        }

        // Idempotency: if this instance already exists and is running, skip spawning a duplicate
        var existingInstance = await _applicationManager.GetApplicationInstance(command.InstanceId);
        if (existingInstance != null &&
            existingInstance.Status == ApplicationStatus.Running &&
            existingInstance.ProcessId.HasValue)
        {
            var isRunning = await _processManager.IsProcessRunning(command.InstanceId);
            if (isRunning)
            {
                _logger.LogInformation(
                    "SPAWN command for already running instance {InstanceId}; skipping duplicate spawn",
                    command.InstanceId);
                return;
            }
        }

        var spawnCommand = new SpawnCommand
        {
            ApplicationId = command.ApplicationId,
            InstanceId = command.InstanceId,
            ExecutablePath = parameters.ExecutablePath ?? string.Empty,
            Arguments = parameters.Arguments ?? string.Empty,
            WorkingDirectory = parameters.WorkingDirectory ?? string.Empty,
            HealthCheckUrl = parameters.HealthCheckUrl ?? string.Empty,
            HealthCheckInterval = parameters.HealthCheckInterval,
            InstanceIndex = parameters.InstanceIndex
        };

        if (parameters.EnvironmentVariables != null)
        {
            foreach (var kvp in parameters.EnvironmentVariables)
            {
                spawnCommand.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }

        var ports = new List<PortMapping>();
        if (parameters.Ports != null)
        {
            foreach (var p in parameters.Ports)
            {
                var portMapping = new PortMapping
                {
                    Name = p.Name,
                    InternalPort = p.InternalPort,
                    ExternalPort = p.ExternalPort,
                    Protocol = p.Protocol
                };

                ports.Add(portMapping);
                spawnCommand.Ports.Add(portMapping);
            }
        }

        await _applicationManager.CreateApplicationInstance(spawnCommand);

        var result = await _processManager.SpawnProcess(spawnCommand, ports);
        if (!result.Success)
        {
            await _applicationManager.UpdateInstanceStatus(command.InstanceId, ApplicationStatus.Error);

            await _grpcClient.SendError(new ErrorReport
            {
                AgentId = _agentSettings.Value.AgentId,
                ErrorType = "SpawnFailed",
                ErrorMessage = result.ErrorMessage ?? "Unknown error",
                StackTrace = string.Empty,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, cancellationToken);
            return;
        }

        await _applicationManager.UpdateInstanceStatus(
            command.InstanceId,
            ApplicationStatus.Running,
            result.ProcessId,
            result.Ports);

        await _grpcClient.SendApplicationSpawned(new ApplicationSpawned
        {
            InstanceId = command.InstanceId,
            ApplicationId = command.ApplicationId,
            ProcessId = result.ProcessId ?? 0,
            Ports = { result.Ports },
            StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }, cancellationToken);
    }

    private async Task ExecuteKill(CommandRequest command, CancellationToken cancellationToken)
    {
        var parameters = DeserializeJson<KillCommandParams>(command.Parameters) ?? new KillCommandParams();
        var force = parameters.Force;
        var timeoutSeconds = parameters.TimeoutSeconds;

        var instance = await _applicationManager.GetApplicationInstance(command.InstanceId);

        var success = await _processManager.KillProcess(command.InstanceId, force, timeoutSeconds);
        if (!success)
        {
            await _grpcClient.SendError(new ErrorReport
            {
                AgentId = _agentSettings.Value.AgentId,
                ErrorType = "KillFailed",
                ErrorMessage = "Failed to kill process",
                StackTrace = string.Empty,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, cancellationToken);
            return;
        }

        await _applicationManager.UpdateInstanceStatus(command.InstanceId, ApplicationStatus.Stopped);

        await _grpcClient.SendApplicationStopped(new ApplicationStopped
        {
            InstanceId = command.InstanceId,
            ApplicationId = instance?.ApplicationId ?? command.ApplicationId,
            ExitCode = 0,
            Reason = "Killed by command",
            StopTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }, cancellationToken);
    }

    private async Task ExecuteRestart(CommandRequest command, CancellationToken cancellationToken)
    {
        var parameters = DeserializeJson<RestartCommandParams>(command.Parameters) ?? new RestartCommandParams();
        var timeoutSeconds = parameters.TimeoutSeconds;

        var success = await _processManager.RestartProcess(command.InstanceId, timeoutSeconds);
        if (!success)
        {
            await _grpcClient.SendError(new ErrorReport
            {
                AgentId = _agentSettings.Value.AgentId,
                ErrorType = "RestartFailed",
                ErrorMessage = "Failed to restart process",
                StackTrace = string.Empty,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, cancellationToken);
            return;
        }

        var instance = await _applicationManager.GetApplicationInstance(command.InstanceId);
        if (instance?.ProcessId != null)
        {
            await _grpcClient.SendApplicationSpawned(new ApplicationSpawned
            {
                InstanceId = command.InstanceId,
                ApplicationId = instance.ApplicationId,
                ProcessId = instance.ProcessId.Value,
                Ports = { instance.Ports },
                StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, cancellationToken);
        }
    }

    private Task ExecuteUpdate(CommandRequest command, CancellationToken cancellationToken)
    {
        _logger.LogInformation("UPDATE command received for instance {InstanceId}", command.InstanceId);
        return Task.CompletedTask;
    }

    private static T? DeserializeJson<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private sealed class SpawnCommandParams
    {
        public string? ExecutablePath { get; set; }
        public string? Arguments { get; set; }
        public string? WorkingDirectory { get; set; }
        public Dictionary<string, string>? EnvironmentVariables { get; set; }
        public List<PortMapping>? Ports { get; set; }
        public string? HealthCheckUrl { get; set; }
        public int HealthCheckInterval { get; set; } = 30;
        public int InstanceIndex { get; set; }
    }

    private sealed class KillCommandParams
    {
        public bool Force { get; set; } = true;
        public int TimeoutSeconds { get; set; } = 30;
    }

    private sealed class RestartCommandParams
    {
        public int TimeoutSeconds { get; set; } = 30;
    }
}
