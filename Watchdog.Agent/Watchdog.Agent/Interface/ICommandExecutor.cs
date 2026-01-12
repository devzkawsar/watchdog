using Watchdog.Agent.Protos;

namespace Watchdog.Agent.Interface;

public interface ICommandExecutor
{
    Task ExecuteCommand(CommandRequest command, CancellationToken cancellationToken);
}
