using Watchdog.Agent.Protos;

namespace Watchdog.Agent.Services;

public interface IGrpcClient
{
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task<bool> IsConnectedAsync();
    Task<AgentRegistrationResponse?> RegisterAsync(CancellationToken cancellationToken = default);
    Task<bool> ReportStatusAsync(StatusReportRequest request, CancellationToken cancellationToken = default);
    Task<bool> SendMetricsAsync(MetricsReport report, CancellationToken cancellationToken = default);
    Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default);
    Task<bool> SendApplicationSpawnedAsync(ApplicationSpawned spawned, CancellationToken cancellationToken = default);
    Task<bool> SendApplicationStoppedAsync(ApplicationStopped stopped, CancellationToken cancellationToken = default);
    Task<bool> SendErrorAsync(ErrorReport error, CancellationToken cancellationToken = default);
    Task StartCommandStreamingAsync(CancellationToken cancellationToken = default);
}
