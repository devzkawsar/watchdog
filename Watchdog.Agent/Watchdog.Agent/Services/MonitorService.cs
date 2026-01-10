using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchdog.Agent.Configuration;
using Watchdog.Agent.Models;
using Watchdog.Agent.Protos;

namespace Watchdog.Agent.Services;

internal interface IMonitorServiceInternal : IMonitorService
{
}

public class MonitorService : IMonitorServiceInternal
{
    private readonly IProcessManager _processManager;
    private readonly IApplicationManager _applicationManager;
    private readonly IGrpcClient _grpcClient;
    private readonly IHealthChecker _healthChecker;
    private readonly ILogger<MonitorService> _logger;
    private readonly IOptions<MonitoringSettings> _monitoringSettings;
    
    private Timer? _metricsTimer;
    private Timer? _healthCheckTimer;
    private Timer? _failureDetectionTimer;
    private readonly object _lock = new();
    private bool _isRunning = false;
    
    public MonitorService(
        IProcessManager processManager,
        IApplicationManager applicationManager,
        IGrpcClient grpcClient,
        IHealthChecker healthChecker,
        ILogger<MonitorService> logger,
        IOptions<MonitoringSettings> monitoringSettings)
    {
        _processManager = processManager;
        _applicationManager = applicationManager;
        _grpcClient = grpcClient;
        _healthChecker = healthChecker;
        _logger = logger;
        _monitoringSettings = monitoringSettings;
    }
    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_isRunning)
                return Task.CompletedTask;
            
            _logger.LogInformation("Starting MonitorService");
            
            // Start metrics collection timer (every 10 seconds)
            _metricsTimer = new Timer(
                async _ => await CollectAndReportMetricsAsync(),
                null,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(_monitoringSettings.Value.MetricsCollectionIntervalMs));
            
            // Start health check timer (every 30 seconds)
            _healthCheckTimer = new Timer(
                async _ => await PerformHealthChecksAsync(),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(_monitoringSettings.Value.HealthCheckIntervalMs));
            
            // Start failure detection timer (every 60 seconds)
            _failureDetectionTimer = new Timer(
                async _ => await DetectAndHandleFailuresAsync(),
                null,
                TimeSpan.FromSeconds(60),
                TimeSpan.FromMilliseconds(_monitoringSettings.Value.FailureDetectionIntervalMs));
            
            _isRunning = true;
            _logger.LogInformation("MonitorService started successfully");
        }

        return Task.CompletedTask;
    }
    
    public Task StopAsync()
    {
        lock (_lock)
        {
            if (!_isRunning)
                return Task.CompletedTask;
            
            _logger.LogInformation("Stopping MonitorService");
            
            _metricsTimer?.Dispose();
            _healthCheckTimer?.Dispose();
            _failureDetectionTimer?.Dispose();
            
            _metricsTimer = null;
            _healthCheckTimer = null;
            _failureDetectionTimer = null;
            
            _isRunning = false;
            _logger.LogInformation("MonitorService stopped");
        }

        return Task.CompletedTask;
    }
    
    public async Task<List<ProcessMetrics>> CollectMetricsAsync()
    {
        var allMetrics = new List<ProcessMetrics>();
        
        try
        {
            var instances = await _applicationManager.GetAllInstancesAsync();
            
            foreach (var instance in instances)
            {
                if (instance.Status == ApplicationStatus.Running && instance.ProcessId.HasValue)
                {
                    var metrics = await _processManager.GetProcessMetricsAsync(instance.InstanceId);
                    if (metrics != null)
                    {
                        allMetrics.Add(metrics);
                        
                        // Update instance with latest metrics
                        instance.Metrics = metrics;
                        instance.LastHealthCheck = DateTime.UtcNow;
                    }
                }
            }
            
            _logger.LogDebug("Collected metrics for {Count} instances", allMetrics.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error collecting metrics");
        }
        
        return allMetrics;
    }
    
    public async Task<bool> CheckHealthAsync(string instanceId)
    {
        try
        {
            var instance = await _applicationManager.GetApplicationInstanceAsync(instanceId);
            if (instance == null)
                return false;
            
            // Check if process is running
            var isRunning = await _processManager.IsProcessRunningAsync(instanceId);
            if (!isRunning)
                return false;
            
            // If health check URL is provided, perform HTTP health check
            if (!string.IsNullOrEmpty(instance.Command?.HealthCheckUrl))
            {
                return await _healthChecker.PerformHttpHealthCheckAsync(
                    instance.Command.HealthCheckUrl,
                    instance.Command.HealthCheckInterval);
            }
            
            // Default health check: process is running and responsive
            var processInfo = await _processManager.GetProcessInfoAsync(instanceId);
            return processInfo != null && processInfo.Status == "Running";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking health for instance {InstanceId}", instanceId);
            return false;
        }
    }
    
    public async Task<List<HealthCheckResult>> CheckAllHealthAsync()
    {
        var results = new List<HealthCheckResult>();
        
        try
        {
            var instances = await _applicationManager.GetAllInstancesAsync();
            var runningInstances = instances.Where(i => i.Status == ApplicationStatus.Running);
            
            foreach (var instance in runningInstances)
            {
                var isHealthy = await CheckHealthAsync(instance.InstanceId);
                
                results.Add(new HealthCheckResult
                {
                    InstanceId = instance.InstanceId,
                    ApplicationId = instance.ApplicationId,
                    IsHealthy = isHealthy,
                    CheckedAt = DateTime.UtcNow
                });
                
                if (!isHealthy)
                {
                    _logger.LogWarning(
                        "Health check failed for instance {InstanceId}",
                        instance.InstanceId);
                    
                    // Update instance status
                    await _applicationManager.UpdateInstanceStatusAsync(
                        instance.InstanceId,
                        ApplicationStatus.Unhealthy);
                }
                else
                {
                    // Update last health check time
                    instance.LastHealthCheck = DateTime.UtcNow;
                }
            }
            
            _logger.LogDebug("Performed health checks for {Count} instances", results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing health checks");
        }
        
        return results;
    }
    
    public async Task DetectFailuresAsync()
    {
        try
        {
            var failures = await DetectAllFailuresAsync();
            
            foreach (var failure in failures.Where(f => f.IsFailure))
            {
                _logger.LogWarning(
                    "Detected failure for instance {InstanceId}: {Reason}",
                    failure.InstanceId, failure.Reason);
                
                await HandleFailureAsync(failure);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in failure detection");
        }
    }
    
    public async Task<List<FailureDetection>> DetectAllFailuresAsync()
    {
        var detections = new List<FailureDetection>();
        
        try
        {
            var instances = await _applicationManager.GetAllInstancesAsync();
            
            foreach (var instance in instances)
            {
                var detection = await DetectInstanceFailureAsync(instance);
                detections.Add(detection);
            }
            
            _logger.LogDebug("Performed failure detection for {Count} instances", detections.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting failures");
        }
        
        return detections;
    }
    
    private async Task CollectAndReportMetricsAsync()
    {
        try
        {
            if (!_isRunning)
                return;
            
            // Collect metrics
            var metrics = await CollectMetricsAsync();
            
            // Build metrics report
            var systemMetrics = await GetSystemMetricsAsync();
            var metricsReport = new MetricsReport
            {
                AgentId = Environment.MachineName,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                SystemMetrics = systemMetrics
            };
            
            foreach (var metric in metrics)
            {
                var appMetric = new Watchdog.Agent.Protos.ApplicationMetrics
                {
                    InstanceId = "unknown", // Would need instance ID mapping
                    CpuPercent = metric.CpuPercent,
                    MemoryMb = metric.MemoryMB,
                    ThreadCount = metric.ThreadCount,
                    HandleCount = metric.HandleCount,
                    IoReadBytes = metric.IoReadBytes,
                    IoWriteBytes = metric.IoWriteBytes
                };
                metricsReport.ApplicationMetrics.Add(appMetric);
            }
            
            // Send metrics via gRPC if connected
            await _grpcClient.SendMetricsAsync(metricsReport);
            
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in metrics collection and reporting");
        }
    }
    
    private async Task PerformHealthChecksAsync()
    {
        try
        {
            if (!_isRunning)
                return;
            
            _logger.LogDebug("Performing periodic health checks");
            
            await CheckAllHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing health checks");
        }
    }
    
    private async Task DetectAndHandleFailuresAsync()
    {
        try
        {
            if (!_isRunning)
                return;
            
            _logger.LogDebug("Running failure detection");
            
            await DetectFailuresAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in failure detection");
        }
    }
    
    private async Task<FailureDetection> DetectInstanceFailureAsync(ManagedApplication instance)
    {
        var detection = new FailureDetection
        {
            InstanceId = instance.InstanceId,
            ApplicationId = instance.ApplicationId,
            IsFailure = false
        };
        
        try
        {
            // Check if instance is supposed to be running
            if (instance.Status == ApplicationStatus.Running)
            {
                // Check if process is still running
                var isRunning = await _processManager.IsProcessRunningAsync(instance.InstanceId);
                if (!isRunning)
                {
                    detection.IsFailure = true;
                    detection.Reason = "Process is not running";
                    detection.FailureType = FailureType.ProcessExited;
                    return detection;
                }
                
                // Check health
                var isHealthy = await CheckHealthAsync(instance.InstanceId);
                if (!isHealthy)
                {
                    detection.IsFailure = true;
                    detection.Reason = "Health check failed";
                    detection.FailureType = FailureType.HealthCheckFailed;
                    return detection;
                }
                
                // Check resource usage
                var metrics = await _processManager.GetProcessMetricsAsync(instance.InstanceId);
                if (metrics != null)
                {
                    if (metrics.CpuPercent > _monitoringSettings.Value.MaxCpuThreshold)
                    {
                        detection.IsFailure = true;
                        detection.Reason = $"CPU usage too high: {metrics.CpuPercent}%";
                        detection.FailureType = FailureType.ResourceExceeded;
                        detection.Metrics = metrics;
                        return detection;
                    }
                    
                    if (metrics.MemoryMB > _monitoringSettings.Value.MaxMemoryThreshold)
                    {
                        // Need total memory to calculate percentage
                        detection.IsFailure = true;
                        detection.Reason = $"Memory usage too high: {metrics.MemoryMB}MB";
                        detection.FailureType = FailureType.ResourceExceeded;
                        detection.Metrics = metrics;
                        return detection;
                    }
                }
                
                // Check for hung process (no activity)
                if (instance.LastHealthCheck.HasValue)
                {
                    var timeSinceLastCheck = DateTime.UtcNow - instance.LastHealthCheck.Value;
                    if (timeSinceLastCheck > TimeSpan.FromMinutes(5))
                    {
                        detection.IsFailure = true;
                        detection.Reason = "No activity for 5 minutes";
                        detection.FailureType = FailureType.HungProcess;
                        return detection;
                    }
                }
            }
            else if (instance.Status == ApplicationStatus.Error || instance.Status == ApplicationStatus.Stopped)
            {
                // Check if we should restart this instance
                var shouldRestart = await _applicationManager.ShouldRestartInstanceAsync(instance);
                if (shouldRestart)
                {
                    detection.IsFailure = true;
                    detection.Reason = "Instance stopped and requires restart";
                    detection.FailureType = FailureType.Stopped;
                    detection.ShouldRestart = true;
                    return detection;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting failure for instance {InstanceId}", instance.InstanceId);
            detection.IsFailure = true;
            detection.Reason = $"Error during detection: {ex.Message}";
            detection.FailureType = FailureType.DetectionError;
        }
        
        return detection;
    }
    
    private async Task HandleFailureAsync(FailureDetection failure)
    {
        try
        {
            _logger.LogWarning(
                "Handling failure for instance {InstanceId}: {Reason}",
                failure.InstanceId, failure.Reason);
            
            switch (failure.FailureType)
            {
                case FailureType.ProcessExited:
                case FailureType.HealthCheckFailed:
                case FailureType.ResourceExceeded:
                case FailureType.HungProcess:
                    if (failure.ShouldRestart)
                    {
                        await HandleRestartAsync(failure);
                    }
                    break;
                    
                case FailureType.Stopped:
                    await HandleRestartAsync(failure);
                    break;
                    
                case FailureType.DetectionError:
                    // Just log, don't take action
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling failure for instance {InstanceId}", failure.InstanceId);
        }
    }
    
    private async Task HandleRestartAsync(FailureDetection failure)
    {
        try
        {
            // Increment restart count
            await _applicationManager.IncrementRestartCountAsync(failure.InstanceId);
            
            // Check if we should restart
            var instance = await _applicationManager.GetApplicationInstanceAsync(failure.InstanceId);
            if (instance == null)
                return;
            
            var shouldRestart = await _applicationManager.ShouldRestartInstanceAsync(instance);
            if (!shouldRestart)
            {
                _logger.LogWarning(
                    "Instance {InstanceId} has reached maximum restart attempts",
                    failure.InstanceId);
                return;
            }
            
            // Restart the process
            _logger.LogInformation(
                "Restarting instance {InstanceId} (attempt {Attempt})",
                failure.InstanceId, instance.RestartCount);
            
            var success = await _processManager.RestartProcessAsync(failure.InstanceId);
            
            if (success)
            {
                _logger.LogInformation(
                    "Successfully restarted instance {InstanceId}",
                    failure.InstanceId);
            }
            else
            {
                _logger.LogError(
                    "Failed to restart instance {InstanceId}",
                    failure.InstanceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting instance {InstanceId}", failure.InstanceId);
        }
    }
    
    private async Task<SystemMetrics> GetSystemMetricsAsync()
    {
        try
        {
            // Get system-wide metrics
            var memoryInfo = GetMemoryInfo();
            var diskInfo = GetDiskInfo();
            
            return new SystemMetrics
            {
                CpuPercent = await GetSystemCpuUsageAsync(),
                MemoryPercent = memoryInfo.UsedPercentage,
                DiskPercent = diskInfo.UsedPercentage,
                TotalProcesses = Process.GetProcesses().Length,
                UptimeSeconds = (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime).TotalSeconds,
                TotalMemoryMb = memoryInfo.TotalMB,
                AvailableMemoryMb = memoryInfo.AvailableMB,
                TotalDiskGb = diskInfo.TotalGB,
                AvailableDiskGb = diskInfo.AvailableGB,
                NetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces().Length
            };
        }
        catch
        {
            return new SystemMetrics();
        }
    }
    
    private Task<double> GetSystemCpuUsageAsync()
    {
        // This would use PerformanceCounter or WMI on Windows
        // Simplified implementation
        return Task.FromResult(0.0);
    }
    
    private (long TotalMB, long AvailableMB, double UsedPercentage) GetMemoryInfo()
    {
        try
        {
            var memoryStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memoryStatus))
            {
                var totalMB = (long)(memoryStatus.ullTotalPhys / (1024 * 1024));
                var availableMB = (long)(memoryStatus.ullAvailPhys / (1024 * 1024));
                var usedPercentage = 100.0 - memoryStatus.dwMemoryLoad;
                
                return (totalMB, availableMB, usedPercentage);
            }
        }
        catch
        {
            // Fallback for non-Windows or if API fails
        }
        
        return (0, 0, 0);
    }
    
    private (long TotalGB, long AvailableGB, double UsedPercentage) GetDiskInfo()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            var totalGB = drive.TotalSize / (1024 * 1024 * 1024);
            var availableGB = drive.AvailableFreeSpace / (1024 * 1024 * 1024);
            var usedPercentage = 100.0 - ((double)drive.AvailableFreeSpace / drive.TotalSize * 100);
            
            return (totalGB, availableGB, usedPercentage);
        }
        catch
        {
            return (0, 0, 0);
        }
    }
    
    // Platform interop for memory info (Windows only)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        
        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf(this);
        }
    }
    
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}

public class HealthCheckResult
{
    public string InstanceId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public bool IsHealthy { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CheckedAt { get; set; }
    public TimeSpan ResponseTime { get; set; }
}

public class FailureDetection
{
    public string InstanceId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public bool IsFailure { get; set; }
    public string? Reason { get; set; }
    public FailureType FailureType { get; set; }
    public bool ShouldRestart { get; set; }
    public ProcessMetrics? Metrics { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}

public enum FailureType
{
    ProcessExited,
    HealthCheckFailed,
    ResourceExceeded,
    HungProcess,
    Stopped,
    DetectionError
}