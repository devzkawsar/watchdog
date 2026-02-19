using Watchdog.Api.Protos;

namespace Watchdog.Agent.Interface;

public interface IGrpcClient
{
    Task<bool> Connect(CancellationToken cancellationToken = default);
    Task Disconnect();
    Task<bool> IsConnected();
    Task<AgentRegistrationResponse?> Register(CancellationToken cancellationToken = default);
    Task<bool> ReportStatus(StatusReportRequest request, CancellationToken cancellationToken = default);
    Task<bool> SendHeartbeat(CancellationToken cancellationToken = default);
    Task<bool> SendApplicationSpawned(ApplicationSpawned spawned, CancellationToken cancellationToken = default);
    Task<bool> SendApplicationStopped(ApplicationStopped stopped, CancellationToken cancellationToken = default);
    Task<bool> SendError(ErrorReport error, CancellationToken cancellationToken = default);
    Task StartCommandStreaming(CancellationToken cancellationToken = default);
}
