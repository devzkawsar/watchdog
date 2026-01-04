namespace Watchdog.Api.BackgroundServices;

public class GrpcConnectionCleanupBackgroundService : BackgroundService
{
    private readonly IAgentGrpcService _agentGrpcService;
    private readonly ILogger<GrpcConnectionCleanupBackgroundService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);
    
    public GrpcConnectionCleanupBackgroundService(
        IAgentGrpcService agentGrpcService,
        ILogger<GrpcConnectionCleanupBackgroundService> logger)
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