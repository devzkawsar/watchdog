using Microsoft.Extensions.Logging;
using Watchdog.Agent.Interface;
using Watchdog.Agent.Protos;

namespace Watchdog.Agent.Services;

internal interface ICommandExecutorInternal : ICommandExecutor
{
}

public class CommandExecutor : ICommandExecutorInternal
{
    private readonly ILogger<CommandExecutor> _logger;

    public CommandExecutor(ILogger<CommandExecutor> logger)
    {
        _logger = logger;
    }

    public Task ExecuteCommand(CommandRequest command, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Received pending command {CommandId} ({CommandType}) for app {AppId} instance {InstanceId}",
            command.CommandId,
            command.CommandType,
            command.ApplicationId,
            command.InstanceId);

        return Task.CompletedTask;
    }
}
