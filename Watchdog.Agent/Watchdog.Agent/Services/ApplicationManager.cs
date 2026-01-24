using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Watchdog.Agent.Configuration;
using Watchdog.Agent.Interface;
using Watchdog.Agent.Models;
using Watchdog.Agent.Protos;
using ApplicationStatus = Watchdog.Agent.Enums.ApplicationStatus;

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
        
        private readonly ConcurrentDictionary<string, ApplicationConfig> _applications = new();
        private readonly ConcurrentDictionary<string, ManagedApplication> _instances = new();
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
        
        public async Task Initialize()
        {
            try
            {
                _logger.LogInformation("Initializing ApplicationManager...");
                
                // Create directories if they don't exist
                Directory.CreateDirectory(_agentSettings.Value.WorkingDirectory);
                Directory.CreateDirectory(_agentSettings.Value.LogDirectory);
                Directory.CreateDirectory(_agentSettings.Value.DataDirectory);
                
                // Load saved state if exists
                await LoadSavedState();
                
                _logger.LogInformation("ApplicationManager initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ApplicationManager");
                throw;
            }
        }
        
        public Task<List<ApplicationConfig>> GetAssignedApplications()
        {
            return Task.FromResult(_applications.Values.ToList());
        }
        
        public Task<ApplicationConfig?> GetApplicationConfig(string applicationId)
        {
            _applications.TryGetValue(applicationId, out var config);
            return Task.FromResult(config);
        }
        
        public Task<bool> ValidateApplicationConfig(ApplicationConfig config)
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
        
        public async Task ReportToOrchestrator()
        {
            try
            {
                var instanceStatuses = await GetInstanceStatuses();
                var systemMetrics = await GetSystemMetrics();
                
                var statusRequest = new StatusReportRequest
                {
                    AgentId = _agentSettings.Value.AgentId,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    SystemMetrics = systemMetrics
                };
                
                statusRequest.ApplicationStatuses.AddRange(instanceStatuses);
                
                var grpcClient = _serviceProvider.GetRequiredService<IGrpcClient>();
                await grpcClient.ReportStatus(statusRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to report to orchestrator");
            }
        }
        
        public async Task<ManagedApplication> CreateApplicationInstance(SpawnCommand command)
        {
            var instance = new ManagedApplication()
            {
                InstanceId = command.InstanceId,
                ApplicationId = command.ApplicationId,
                Command = command,
                Status = ApplicationStatus.Starting,
                CreatedAt = DateTime.UtcNow,
                RestartCount = 0
            };
            
            _instances[command.InstanceId] = instance;
            
            // Save state
            await SaveState();
            
            _logger.LogInformation(
                "Created application instance {InstanceId} for application {AppId}",
                instance.InstanceId, instance.ApplicationId);
            
            return instance;
        }
        
        public Task<ManagedApplication?> GetApplicationInstance(string instanceId)
        {
            _instances.TryGetValue(instanceId, out var instance);
            return Task.FromResult(instance);
        }
        
        public Task<List<ManagedApplication>> GetAllInstances()
        {
            return Task.FromResult(_instances.Values.ToList());
        }
        
        public async Task UpdateInstanceStatus(
            string instanceId, 
            ApplicationStatus status,
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
                    
                    if (status == ApplicationStatus.Running)
                    {
                        instance.StartedAt = DateTime.UtcNow;
                        instance.LastHealthCheck = DateTime.UtcNow;
                        instance.RestartCount = 0;
                        instance.LastRestartAttempt = null;
                        instance.StopReported = false;
                    }
                    else if (status == ApplicationStatus.Stopped || status == ApplicationStatus.Error)
                    {
                        instance.StoppedAt = DateTime.UtcNow;
                    }
                }
                
                // Save state
                await SaveState();
                
                _logger.LogDebug(
                    "Updated instance {InstanceId} status to {Status}",
                    instanceId, status);
            }
        }
        
        public async Task RemoveInstance(string instanceId)
        {
            if (_instances.TryRemove(instanceId, out var instance))
            {
                // Save state
                await SaveState();
                
                _logger.LogInformation(
                    "Removed instance {InstanceId} from tracking",
                    instanceId);
            }
        }
        
        public async Task<List<Watchdog.Agent.Protos.ApplicationStatus>> GetInstanceStatuses()
        {
            var statuses = new List<Watchdog.Agent.Protos.ApplicationStatus>();
            
            foreach (var instance in _instances.Values)
            {
                try
                {
                    var status = new Watchdog.Agent.Protos.ApplicationStatus
                    {
                        InstanceId = instance.InstanceId,
                        ApplicationId = instance.ApplicationId,
                        Status = instance.Status.ToString(),
                        ProcessId = instance.ProcessId?.ToString() ?? string.Empty,
                        StartTime = instance.StartedAt.HasValue
                            ? new DateTimeOffset(instance.StartedAt.Value).ToUnixTimeSeconds()
                            : 0,
                        HealthStatus = await GetInstanceHealthStatus(instance)
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
        
        public async Task UpdateApplicationsFromControlPlane(List<ApplicationAssignment> assignments)
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
                        HealthCheckInterval = assignment.HealthCheckInterval
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
            await SaveState();
        }
        
        public Task<bool> ShouldRestartInstance(ManagedApplication instance)
        {
            if (instance.Status != ApplicationStatus.Error && 
                instance.Status != ApplicationStatus.Stopped &&
                instance.Status != ApplicationStatus.Unhealthy)
                return Task.FromResult(false);

            _applications.TryGetValue(instance.ApplicationId, out var appConfig);
            var maxRestartAttempts = appConfig?.MaxRestartAttempts > 0
                ? appConfig!.MaxRestartAttempts
                : _monitoringSettings.Value.MaxRestartAttempts;

            var restartDelaySeconds = appConfig?.RestartDelaySeconds > 0
                ? appConfig!.RestartDelaySeconds
                : _monitoringSettings.Value.RestartDelaySeconds;

            if (instance.RestartCount >= maxRestartAttempts)
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
                if (timeSinceLastAttempt < TimeSpan.FromSeconds(restartDelaySeconds))
                    return Task.FromResult(false);
            }
            
            return Task.FromResult(true);
        }
        
        public async Task NotifyInstanceStopped(string instanceId, int exitCode, string reason)
        {
            if (!_instances.TryGetValue(instanceId, out var instance))
            {
                _logger.LogWarning("NotifyInstanceStopped called for unknown instance {InstanceId}", instanceId);
                return;
            }

            bool shouldSend;
            lock (_syncLock)
            {
                if (instance.StopReported)
                {
                    shouldSend = false;
                }
                else
                {
                    instance.StopReported = true;
                    shouldSend = true;
                }
            }

            if (!shouldSend)
                return;

            await SaveState();

            try
            {
                var grpcClient = _serviceProvider.GetRequiredService<IGrpcClient>();
                await grpcClient.SendApplicationStopped(new ApplicationStopped
                {
                    InstanceId = instance.InstanceId,
                    ApplicationId = instance.ApplicationId,
                    ExitCode = exitCode,
                    Reason = reason,
                    StopTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to notify control plane about stopped instance {InstanceId}", instanceId);
            }
        }
        
        public async Task IncrementRestartCount(string instanceId)
        {
            if (_instances.TryGetValue(instanceId, out var instance))
            {
                lock (_syncLock)
                {
                    instance.RestartCount++;
                    instance.LastRestartAttempt = DateTime.UtcNow;
                }
                
                // Save state
                await SaveState();
            }
        }
        
        private Task<string> GetInstanceHealthStatus(ManagedApplication instance)
        {
            if (instance.Status != ApplicationStatus.Running)
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
        
        private Task<SystemMetrics> GetSystemMetrics()
        {
            // This would be implemented by a separate metrics collector
            return Task.FromResult(new SystemMetrics
            {
                UptimeSeconds = (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime).TotalSeconds,
                TotalProcesses = Process.GetProcesses().Length
            });
        }
        
        private async Task LoadSavedState()
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
        
        private async Task SaveState()
        {
            try
            {
                var state = new AgentState
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
        public async Task SyncInstancesFromApi(List<Watchdog.Agent.Protos.ApplicationStatus> activeInstances)
        {
            _logger.LogInformation("Syncing {Count} instances from Control Plane...", activeInstances.Count);
            
            var apiInstanceIds = activeInstances.Select(i => i.InstanceId).ToHashSet();
            
            // 1. Remove local instances no longer known by API
            var removedCount = 0;
            foreach (var localId in _instances.Keys)
            {
                if (!apiInstanceIds.Contains(localId))
                {
                    if (_instances.TryRemove(localId, out _))
                    {
                        removedCount++;
                    }
                }
            }
            
            if (removedCount > 0)
            {
                _logger.LogInformation("Removed {Count} local instances not found in Control Plane state", removedCount);
            }

            // 2. Add or Update instances from API
            foreach (var apiInstance in activeInstances)
            {
                // We only care about instances the API thinks are Running
                if (apiInstance.Status != "Running") continue;
                
                // Check if we already know about this instance
                if (_instances.TryGetValue(apiInstance.InstanceId, out var localInstance))
                {
                    // Ensure local status reflects API's expectation 
                    // so we give ReattachProcesses a chance to find it.
                    if (localInstance.Status != Enums.ApplicationStatus.Running)
                    {
                        _logger.LogInformation("Updating local status for instance {InstanceId} from {OldStatus} to Running to match Control Plane", 
                            apiInstance.InstanceId, localInstance.Status);
                        localInstance.Status = Enums.ApplicationStatus.Running;
                    }

                    // Sync the PID from API if we don't have one (or it's different)
                    if (int.TryParse(apiInstance.ProcessId, out int pid) && pid > 0)
                    {
                        // Preference: If we already have a PID locally and it's running, keep it?
                        // For now, let the API's PID override if ours is missing or different.
                        if (!localInstance.ProcessId.HasValue || localInstance.ProcessId != pid)
                        {
                            localInstance.ProcessId = pid;
                        }
                    }
                }
                else
                {
                    // API knows about an instance we don't have in local state (maybe state file lost)
                    // We need to construct a ManagedApplication from the API info + App Config
                    
                    var appConfig = await GetApplicationConfig(apiInstance.ApplicationId);
                    if (appConfig == null)
                    {
                        _logger.LogWarning("Unknown application {AppId} for instance {InstanceId}. Cannot sync from API yet.", 
                            apiInstance.ApplicationId, apiInstance.InstanceId);
                        continue;
                    }

                    // Reconstruct ManagedApplication
                    var managedApp = new ManagedApplication
                    {
                        InstanceId = apiInstance.InstanceId,
                        ApplicationId = apiInstance.ApplicationId,
                        Status = Enums.ApplicationStatus.Running,
                        ProcessId = int.TryParse(apiInstance.ProcessId, out int pid) ? pid : null,
                        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(apiInstance.StartTime).UtcDateTime,
                        StartedAt = DateTimeOffset.FromUnixTimeSeconds(apiInstance.StartTime).UtcDateTime,
                        Command = new SpawnCommand 
                        {
                            ApplicationId = appConfig.Id,
                            InstanceId = apiInstance.InstanceId,
                            ExecutablePath = appConfig.ExecutablePath,
                            Arguments = appConfig.Arguments,
                            WorkingDirectory = appConfig.WorkingDirectory,
                            HealthCheckUrl = appConfig.HealthCheckUrl,
                            HealthCheckInterval = appConfig.HealthCheckInterval
                        }
                    };
                    
                    // Recover ports if available
                    if (apiInstance.Ports != null)
                    {
                        foreach (var p in apiInstance.Ports)
                        {
                            managedApp.Ports.Add(new Watchdog.Agent.Protos.PortMapping 
                            { 
                                Name = p.Name,
                                InternalPort = p.InternalPort,
                                ExternalPort = p.ExternalPort,
                                Protocol = p.Protocol
                            });
                        }
                    }
                    
                    _instances[managedApp.InstanceId] = managedApp;
                }
            }
            
            // Save authoritative state
            await SaveState();
            
            // Now that we've populated _instances with what API expects,
            // ReattachProcesses will verify if they actually exist.
            // Any "Running" instance that ReattachProcesses fails to find/recover 
            // should be reported as Stopped.
            
            // Note: We deliberately do NOT immediately report stopped here. 
            // We let the AgentWorker call ReattachProcesses next, which triggers discovery.
            // But we need a way to detect "Missing" after ReattachProcesses.
            // See AgentWorker update.
        }

    }
}