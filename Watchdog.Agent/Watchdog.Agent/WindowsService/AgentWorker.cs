using Watchdog.Agent.Configuration;
using Watchdog.Agent.Services;
using Microsoft.Extensions.Options;
using Watchdog.Agent.Interface;

namespace Watchdog.Agent.WindowsService;

public class AgentWorker : BackgroundService
{
    private readonly IApplicationManager _applicationManager;
    private readonly IProcessManager _processManager;
    private readonly IMonitorService _monitorService;
    private readonly IGrpcClient _grpcClient;
    private readonly ILogger<AgentWorker> _logger;
    private readonly IOptions<AgentSettings> _agentSettings;
    private readonly IOptions<ControlPlaneSettings> _controlPlaneSettings;
    
    private Timer? _statusReportTimer;
    private Timer? _reconnectTimer;
    private bool _isRunning = false;
    private int _registrationAttempts = 0;
    
    public AgentWorker(
        IApplicationManager applicationManager,
        IProcessManager processManager,
        IMonitorService monitorService,
        IGrpcClient grpcClient,
        ILogger<AgentWorker> logger,
        IOptions<AgentSettings> agentSettings,
        IOptions<ControlPlaneSettings> controlPlaneSettings)
    {
        _applicationManager = applicationManager;
        _processManager = processManager;
        _monitorService = monitorService;
        _grpcClient = grpcClient;
        _logger = logger;
        _agentSettings = agentSettings;
        _controlPlaneSettings = controlPlaneSettings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Watchdog Agent worker starting");
        
        try
        {
            // Initialize application manager
            await _applicationManager.Initialize();
            
            // Auto-register if configured (Syncs state from API)
            if (_agentSettings.Value.AutoRegister)
            {
                await RegisterWithControlPlane(stoppingToken);
            }

            // Reattach to any existing processes for instances loaded from state (local or synced)
            _logger.LogInformation("Attempting to reattach to existing running processes...");
            await _processManager.ReattachProcesses();
            _logger.LogInformation("Process reattachment phase completed");
            
            // Start monitoring service
            await _monitorService.Start(stoppingToken);
            
            // On startup, immediately detect failures so any assigned instances
            // without a running process are reported to the control plane as stopped.
            await _monitorService.DetectFailures();
            
            // Start status reporting timer (every 5 seconds)
            _statusReportTimer = new Timer(
                async _ => await ReportStatus(stoppingToken),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));
            
            // Start reconnect timer (every 30 seconds)
            _reconnectTimer = new Timer(
                async _ => await CheckConnectionAndReconnect(stoppingToken),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30));
            
            _isRunning = true;
            _logger.LogInformation("Watchdog Agent worker started successfully");
            
            // Main loop
            // while (!stoppingToken.IsCancellationRequested && _isRunning)
            // {
            //     try
            //     {
            //         await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            //     }
            //     catch (OperationCanceledException)
            //     {
            //         break;
            //     }
            //     catch (Exception ex)
            //     {
            //         _logger.LogError(ex, "Error in agent worker main loop");
            //         await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            //     }
            // }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in agent worker");
            throw;
        }
        finally
        {
            await Cleanup();
            _logger.LogInformation("Watchdog Agent worker stopped");
        }
    }
    
    private async Task RegisterWithControlPlane(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Attempting to register with control plane...");
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var connected = await _grpcClient.Connect(cancellationToken);
                
                if (connected)
                {
                    _logger.LogInformation("Successfully registered with control plane");
                    _registrationAttempts = 0;
                    return;
                }
                
                _registrationAttempts++;
                
                if (_registrationAttempts >= _agentSettings.Value.MaxRegistrationAttempts)
                {
                    _logger.LogError("Maximum registration attempts reached");
                    break;
                }
                
                _logger.LogWarning(
                    "Registration failed, retrying in {Interval} seconds (attempt {Attempt}/{Max})",
                    _agentSettings.Value.RegistrationRetryInterval,
                    _registrationAttempts,
                    _agentSettings.Value.MaxRegistrationAttempts);
                
                await Task.Delay(
                    TimeSpan.FromSeconds(_agentSettings.Value.RegistrationRetryInterval), 
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during registration attempt");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
    }
    
    private async Task ReportStatus(CancellationToken cancellationToken)
    {
        try
        {
            if (!_isRunning)
                return;
            
            // Check if connected to control plane
            var isConnected = await _grpcClient.IsConnected();
            if (!isConnected)
            {
                _logger.LogDebug("Not connected to control plane, skipping status report");
                return;
            }
            
            // Report to orchestrator
            await _applicationManager.ReportToOrchestrator();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reporting status");
        }
    }
    
    private async Task CheckConnectionAndReconnect(CancellationToken cancellationToken)
    {
        try
        {
            if (!_isRunning)
                return;
            
            var isConnected = await _grpcClient.IsConnected();
            if (!isConnected)
            {
                _logger.LogWarning("Lost connection to control plane, attempting to reconnect...");
                await _grpcClient.Connect(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking connection");
        }
    }
    
    private async Task Cleanup()
    {
        _isRunning = false;
        
        _statusReportTimer?.Dispose();
        _reconnectTimer?.Dispose();
        
        await _monitorService.Stop();
        await _grpcClient.Disconnect();
        
        _logger.LogInformation("Agent worker cleanup completed");
    }
    
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Watchdog Agent worker stopping...");
        await base.StopAsync(cancellationToken);
    }
}