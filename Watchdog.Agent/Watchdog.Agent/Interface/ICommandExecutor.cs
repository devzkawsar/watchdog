using Watchdog.Api.Protos;

namespace Watchdog.Agent.Interface;

public interface ICommandExecutor
{
    Task ExecuteCommand(CommandRequest command, CancellationToken cancellationToken);
}
