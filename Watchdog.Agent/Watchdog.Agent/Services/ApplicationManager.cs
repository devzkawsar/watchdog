using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchdog.Agent.Configuration;
using Watchdog.Agent.Protos;

namespace Watchdog.Agent.Services
{
    internal interface IApplicationManagerInternal : IApplicationManager
    {
    }

    public class ApplicationManager : IApplicationManagerInternal
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ApplicationManager> _logger;
        private readonly IOptions<AgentSettings> _agentSettings;
        private readonly IOptions<MonitoringSettings> _monitoringSettings;
        
        private readonly ConcurrentDictionary<string, Watchdog.Agent.Models.ApplicationConfig> _applications = new();
        private readonly ConcurrentDictionary<string, Watchdog.Agent.Models.ManagedApplication> _instances = new();
        private readonly object _syncLock = new();
        
        public ApplicationManager(
            IServiceProvider serviceProvider,
            ILogger<ApplicationManager> logger,
            IOptions<AgentSettings> agentSettings,
            IOptions<MonitoringSettings> monitoringSettings)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _agentSettings = agentSettings;
            _monitoringSettings = monitoringSettings;
        }
        
        public async Task InitializeAsync()
        {
            try
            {
                _logger.LogInformation("Initializing ApplicationManager...");
                
                // Create directories if they don't exist
                Directory.CreateDirectory(_agentSettings.Value.WorkingDirectory);
                Directory.CreateDirectory(_agentSettings.Value.LogDirectory);
                Directory.CreateDirectory(_agentSettings.Value.DataDirectory);
                
                // Load saved state if exists
                await LoadSavedStateAsync();
                
                _logger.LogInformation("ApplicationManager initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ApplicationManager");
                throw;
            }
        }
        
        public Task<List<Watchdog.Agent.Models.ApplicationConfig>> GetAssignedApplicationsAsync()
        {
            return Task.FromResult(_applications.Values.ToList());
        }
        
        public Task<Watchdog.Agent.Models.ApplicationConfig?> GetApplicationConfigAsync(string applicationId)
        {
            _applications.TryGetValue(applicationId, out var config);
            return Task.FromResult(config);
        }
        
        public Task<bool> ValidateApplicationConfigAsync(Watchdog.Agent.Models.ApplicationConfig config)
        {
            try
            {
                // Check if executable exists
                if (!File.Exists(config.ExecutablePath))
                {
                    _logger.LogError("Executable not found: {Path}", config.ExecutablePath);
                    return Task.FromResult(false);
                }
                
                // Check if working directory exists
                if (!string.IsNullOrEmpty(config.WorkingDirectory) && 
                    !Directory.Exists(config.WorkingDirectory))
                {
                    _logger.LogError("Working directory not found: {Path}", config.WorkingDirectory);
                    return Task.FromResult(false);
                }
                
                // Validate port requirements
                if (config.PortRequirements != null && config.PortRequirements.Any())
                {
                    foreach (var portReq in config.PortRequirements)
                    {
                        if (portReq.InternalPort < 1 || portReq.InternalPort > 65535)
                        {
                            _logger.LogError("Invalid port number: {Port}", portReq.InternalPort);
                            return Task.FromResult(false);
                        }
                    }
                }
                
                // Validate environment variables
                if (config.EnvironmentVariables != null)
                {
                    foreach (var envVar in config.EnvironmentVariables)
                    {
                        if (string.IsNullOrEmpty(envVar.Key))
                        {
                            _logger.LogError("Environment variable key cannot be empty");
                            return Task.FromResult(false);
                        }
                    }
                }
                
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating application config for {AppId}", config.Id);
                return Task.FromResult(false);
            }
        }
        
        public async Task ReportToOrchestratorAsync()
        {
            try
            {
                var instanceStatuses = await GetInstanceStatusesAsync();
                var systemMetrics = await GetSystemMetricsAsync();
                
                var statusRequest = new StatusReportRequest
                {
                    AgentId = _agentSettings.Value.AgentId,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    SystemMetrics = systemMetrics
                };
                
                statusRequest.Instances.AddRange(instanceStatuses);
                
                var grpcClient = _serviceProvider.GetRequiredService<IGrpcClient>();
                await grpcClient.ReportStatusAsync(statusRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to report to orchestrator");
            }
        }
        
        public async Task<Watchdog.Agent.Models.ManagedApplication> CreateApplicationInstanceAsync(SpawnCommand command)
        {
            var instance = new Watchdog.Agent.Models.ManagedApplication
            {
                InstanceId = command.InstanceId,
                ApplicationId = command.ApplicationId,
                Command = command,
                Status = Watchdog.Agent.Models.ApplicationStatus.Starting,
                CreatedAt = DateTime.UtcNow,
                RestartCount = 0
            };
            
            _instances[command.InstanceId] = instance;
            
            // Save state
            await SaveStateAsync();
            
            _logger.LogInformation(
                "Created application instance {InstanceId} for application {AppId}",
                instance.InstanceId, instance.ApplicationId);
            
            return instance;
        }
        
        public Task<Watchdog.Agent.Models.ManagedApplication?> GetApplicationInstanceAsync(string instanceId)
        {
            _instances.TryGetValue(instanceId, out var instance);
            return Task.FromResult(instance);
        }
        
        public Task<List<Watchdog.Agent.Models.ManagedApplication>> GetAllInstancesAsync()
        {
            return Task.FromResult(_instances.Values.ToList());
        }
        
        public async Task UpdateInstanceStatusAsync(
            string instanceId, 
            Watchdog.Agent.Models.ApplicationStatus status,
            int? processId = null,
            List<PortMapping>? ports = null)
        {
            if (_instances.TryGetValue(instanceId, out var instance))
            {
                lock (_syncLock)
                {
                    instance.Status = status;
                    
                    if (processId.HasValue)
                        instance.ProcessId = processId.Value;
                    
                    if (ports != null)
                        instance.Ports = ports;
                    
                    if (status == Watchdog.Agent.Models.ApplicationStatus.Running)
                    {
                        instance.StartedAt = DateTime.UtcNow;
                        instance.LastHealthCheck = DateTime.UtcNow;
                    }
                    else if (status == Watchdog.Agent.Models.ApplicationStatus.Stopped || status == Watchdog.Agent.Models.ApplicationStatus.Error)
                    {
                        instance.StoppedAt = DateTime.UtcNow;
                    }
                    
                    if (status == Watchdog.Agent.Models.ApplicationStatus.Error || status == Watchdog.Agent.Models.ApplicationStatus.Stopped)
                    {
                        instance.RestartCount = 0;
                    }
                }
                
                // Save state
                await SaveStateAsync();
                
                _logger.LogDebug(
                    "Updated instance {InstanceId} status to {Status}",
                    instanceId, status);
            }
        }
        
        public async Task RemoveInstanceAsync(string instanceId)
        {
            if (_instances.TryRemove(instanceId, out var instance))
            {
                // Save state
                await SaveStateAsync();
                
                _logger.LogInformation(
                    "Removed instance {InstanceId} from tracking",
                    instanceId);
            }
        }
        
        public async Task<List<ApplicationInstanceStatus>> GetInstanceStatusesAsync()
        {
            var statuses = new List<ApplicationInstanceStatus>();
            
            foreach (var instance in _instances.Values)
            {
                try
                {
                    var status = new ApplicationInstanceStatus
                    {
                        InstanceId = instance.InstanceId,
                        ApplicationId = instance.ApplicationId,
                        Status = instance.Status.ToString(),
                        ProcessId = instance.ProcessId?.ToString() ?? string.Empty,
                        StartTime = instance.StartedAt.HasValue
                            ? new DateTimeOffset(instance.StartedAt.Value).ToUnixTimeSeconds()
                            : 0,
                        UptimeSeconds = instance.StartedAt.HasValue ? 
                            (long)(DateTimeOffset.UtcNow - new DateTimeOffset(instance.StartedAt.Value)).TotalSeconds : 0,
                        HealthStatus = await GetInstanceHealthStatusAsync(instance)
                    };
                    
                    // Add ports if available
                    if (instance.Ports != null && instance.Ports.Any())
                    {
                        status.Ports.AddRange(instance.Ports);
                    }
                    
                    // Add metrics if available
                    if (instance.Metrics != null)
                    {
                        status.CpuPercent = instance.Metrics.CpuPercent;
                        status.MemoryMb = instance.Metrics.MemoryMB;
                    }
                    
                    statuses.Add(status);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting status for instance {InstanceId}", instance.InstanceId);
                }
            }
            
            return statuses;
        }
        
        public async Task UpdateApplicationsFromControlPlaneAsync(List<ApplicationAssignment> assignments)
        {
            lock (_syncLock)
            {
                foreach (var assignment in assignments)
                {
                    var config = new Watchdog.Agent.Models.ApplicationConfig
                    {
                        Id = assignment.ApplicationId,
                        Name = assignment.ApplicationName,
                        ExecutablePath = assignment.ExecutablePath,
                        Arguments = assignment.Arguments,
                        WorkingDirectory = assignment.WorkingDirectory,
                        DesiredInstances = assignment.DesiredInstances,
                        PortRequirements = assignment.Ports.Select(p => new PortRequirement
                        {
                            Name = p.Name,
                            InternalPort = p.InternalPort,
                            Protocol = p.Protocol,
                            Required = p.Required
                        }).ToList(),
                        EnvironmentVariables = assignment.EnvironmentVariables.ToDictionary(
                            kvp => kvp.Key, kvp => kvp.Value),
                        HealthCheckUrl = assignment.HealthCheckUrl,
                        HealthCheckInterval = assignment.HealthCheckInterval,
                        MaxRestartAttempts = assignment.MaxRestartAttempts,
                        RestartDelaySeconds = assignment.RestartDelaySeconds
                    };
                    
                    _applications[config.Id] = config;
                    
                    _logger.LogInformation(
                        "Updated application config for {AppId} ({Name})",
                        config.Id, config.Name);
                }
                
                // Remove applications that are no longer assigned
                var assignedIds = assignments.Select(a => a.ApplicationId).ToHashSet();
                var removedApps = _applications.Keys.Where(id => !assignedIds.Contains(id)).ToList();
                
                foreach (var appId in removedApps)
                {
                    _applications.TryRemove(appId, out _);
                    _logger.LogInformation("Removed unassigned application {AppId}", appId);
                }
            }
            
            // Save state
            await SaveStateAsync();
        }
        
        public Task<bool> ShouldRestartInstanceAsync(Watchdog.Agent.Models.ManagedApplication instance)
        {
            if (instance.Status != Watchdog.Agent.Models.ApplicationStatus.Error && 
                instance.Status != Watchdog.Agent.Models.ApplicationStatus.Stopped)
                return Task.FromResult(false);
            
            if (instance.RestartCount >= _monitoringSettings.Value.MaxRestartAttempts)
            {
                _logger.LogWarning(
                    "Instance {InstanceId} has reached maximum restart attempts ({Attempts})",
                    instance.InstanceId, instance.RestartCount);
                return Task.FromResult(false);
            }
            
            // Check cooldown period
            if (instance.LastRestartAttempt.HasValue)
            {
                var timeSinceLastAttempt = DateTime.UtcNow - instance.LastRestartAttempt.Value;
                if (timeSinceLastAttempt < TimeSpan.FromSeconds(_monitoringSettings.Value.RestartDelaySeconds))
                    return Task.FromResult(false);
            }
            
            return Task.FromResult(true);
        }
        
        public async Task IncrementRestartCountAsync(string instanceId)
        {
            if (_instances.TryGetValue(instanceId, out var instance))
            {
                lock (_syncLock)
                {
                    instance.RestartCount++;
                    instance.LastRestartAttempt = DateTime.UtcNow;
                }
                
                // Save state
                await SaveStateAsync();
            }
        }
        
        private Task<string> GetInstanceHealthStatusAsync(Watchdog.Agent.Models.ManagedApplication instance)
        {
            if (instance.Status != Watchdog.Agent.Models.ApplicationStatus.Running)
                return Task.FromResult("Stopped");
            
            // Check if process is still running
            if (!instance.ProcessId.HasValue)
                return Task.FromResult("Unknown");
            
            try
            {
                var process = Process.GetProcessById(instance.ProcessId.Value);
                if (process.HasExited)
                    return Task.FromResult("Unhealthy");
                
                // Check last health check time
                if (instance.LastHealthCheck.HasValue)
                {
                    var timeSinceLastCheck = DateTime.UtcNow - instance.LastHealthCheck.Value;
                    if (timeSinceLastCheck > TimeSpan.FromSeconds(60))
                        return Task.FromResult("Unknown");
                }
                
                return Task.FromResult("Healthy");
            }
            catch (ArgumentException)
            {
                // Process not found
                return Task.FromResult("Unhealthy");
            }
            catch (Exception)
            {
                return Task.FromResult("Unknown");
            }
        }
        
        private Task<SystemMetrics> GetSystemMetricsAsync()
        {
            // This would be implemented by a separate metrics collector
            return Task.FromResult(new SystemMetrics
            {
                UptimeSeconds = (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime).TotalSeconds,
                TotalProcesses = Process.GetProcesses().Length
            });
        }
        
        private async Task LoadSavedStateAsync()
        {
            try
            {
                var statePath = Path.Combine(_agentSettings.Value.DataDirectory, "agent-state.json");
                if (!File.Exists(statePath))
                    return;
                
                var json = await File.ReadAllTextAsync(statePath);
                var state = JsonSerializer.Deserialize<Watchdog.Agent.Models.AgentState>(json);
                
                if (state != null)
                {
                    // Load applications
                    foreach (var app in state.Applications)
                    {
                        _applications[app.Id] = app;
                    }
                    
                    // Load instances
                    foreach (var instance in state.Instances)
                    {
                        _instances[instance.InstanceId] = instance;
                    }
                    
                    _logger.LogInformation(
                        "Loaded saved state: {AppCount} applications, {InstanceCount} instances",
                        state.Applications.Count, state.Instances.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load saved state");
            }
        }
        
        private async Task SaveStateAsync()
        {
            try
            {
                var state = new Watchdog.Agent.Models.AgentState
                {
                    Applications = _applications.Values.ToList(),
                    Instances = _instances.Values.ToList(),
                    SavedAt = DateTime.UtcNow
                };
                
                var statePath = Path.Combine(_agentSettings.Value.DataDirectory, "agent-state.json");
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                
                await File.WriteAllTextAsync(statePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save state");
            }
        }
    }
}

namespace Watchdog.Agent.Models
{
    public class ApplicationConfig
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = string.Empty;
        public int DesiredInstances { get; set; } = 1;
        public List<PortRequirement> PortRequirements { get; set; } = new();
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
        public string HealthCheckUrl { get; set; } = string.Empty;
        public int HealthCheckInterval { get; set; } = 30;
        public int MaxRestartAttempts { get; set; } = 3;
        public int RestartDelaySeconds { get; set; } = 10;
    }

    public class ManagedApplication
    {
        public string InstanceId { get; set; } = string.Empty;
        public string ApplicationId { get; set; } = string.Empty;
        public SpawnCommand? Command { get; set; }
        public ApplicationStatus Status { get; set; } = ApplicationStatus.Pending;
        public int? ProcessId { get; set; }
        public List<PortMapping> Ports { get; set; } = new();
        public ProcessMetrics? Metrics { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? StoppedAt { get; set; }
        public DateTime? LastHealthCheck { get; set; }
        public int RestartCount { get; set; }
        public DateTime? LastRestartAttempt { get; set; }
        public string? LastError { get; set; }
    }

    public enum ApplicationStatus
    {
        Pending,
        Starting,
        Running,
        Unhealthy,
        Stopping,
        Stopped,
        Error
    }

    public class AgentState
    {
        public List<ApplicationConfig> Applications { get; set; } = new();
        public List<ManagedApplication> Instances { get; set; } = new();
        public DateTime SavedAt { get; set; }
    }

    public class ProcessMetrics
    {
        public double CpuPercent { get; set; }
        public double MemoryMB { get; set; }
        public int ThreadCount { get; set; }
        public long HandleCount { get; set; }
        public long IoReadBytes { get; set; }
        public long IoWriteBytes { get; set; }
        public DateTime CollectedAt { get; set; }
    }
}