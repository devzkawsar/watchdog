using Watchdog.Agent.Protos;

namespace Watchdog.Agent.Services;

public interface ICommandExecutor
{
    Task ExecuteCommandAsync(CommandRequest command, CancellationToken cancellationToken);
}
