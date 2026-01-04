using Watchdog.Api.Services;

namespace Watchdog.Api.BackgroundServices;

public class ScalingBackgroundService : BackgroundService
{
    private readonly IScalingEngine _scalingEngine;
    private readonly ILogger<ScalingBackgroundService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(60); // Check every minute
    
    public ScalingBackgroundService(
        IScalingEngine scalingEngine,
        ILogger<ScalingBackgroundService> logger)
    {
        _scalingEngine = scalingEngine;
        _logger = logger;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scaling background service started");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Checking application scaling...");
                await _scalingEngine.CheckAndScaleApplicationsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scaling background service");
            }
            
            await Task.Delay(_interval, stoppingToken);
        }
        
        _logger.LogInformation("Scaling background service stopped");
    }
}