using Watchdog.Api.Interface;
using Watchdog.Api.Services;

namespace Watchdog.Api.BackgroundServices;

public class ScalingBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScalingBackgroundService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(60); // Check every minute
    
    public ScalingBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<ScalingBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scaling background service started");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Checking application scaling...");
                using var scope = _scopeFactory.CreateScope();
                var scalingEngine = scope.ServiceProvider.GetRequiredService<IScalingEngine>();
                await scalingEngine.CheckAndScaleApplications();
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