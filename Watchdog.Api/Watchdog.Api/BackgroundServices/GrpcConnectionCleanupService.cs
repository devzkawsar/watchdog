using Watchdog.Api.Interface;
using Watchdog.Api.Services;

namespace Watchdog.Api.BackgroundServices;

public class GrpcConnectionCleanupService : BackgroundService
{
    private readonly IAgentGrpcService _agentGrpcService;
    private readonly ILogger<GrpcConnectionCleanupService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);
    
    public GrpcConnectionCleanupService(
        IAgentGrpcService agentGrpcService,
        ILogger<GrpcConnectionCleanupService> logger)
    {
        _agentGrpcService = agentGrpcService;
        _logger = logger;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("gRPC connection cleanup background service started");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Cleaning up stale gRPC connections...");
                await _agentGrpcService.CleanupStaleConnectionsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in gRPC connection cleanup");
            }
            
            await Task.Delay(_interval, stoppingToken);
        }
        
        _logger.LogInformation("gRPC connection cleanup background service stopped");
    }
}