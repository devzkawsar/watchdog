using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchdog.Agent.Configuration;
using Watchdog.Agent.Interface;
using Watchdog.Agent.Models;
using Watchdog.Agent.Protos;

namespace Watchdog.Agent.Services;

internal interface IProcessManagerInternal : IProcessManager
{
}

public class ProcessManager : IProcessManagerInternal
{
    private readonly ILogger<ProcessManager> _logger;
    private readonly INetworkManager _networkManager;
    private readonly IApplicationManager _applicationManager;
    private readonly IOptions<ProcessSettings> _processSettings;
    private readonly IOptions<AgentSettings> _agentSettings;
    
    private readonly ConcurrentDictionary<string, ManagedProcess> _managedProcesses = new();
    private readonly ConcurrentDictionary<int, string> _processIdToInstanceId = new();
    
    public ProcessManager(
        ILogger<ProcessManager> logger,
        INetworkManager networkManager,
        IApplicationManager applicationManager,
        IOptions<ProcessSettings> processSettings,
        IOptions<AgentSettings> agentSettings)
    {
        _logger = logger;
        _networkManager = networkManager;
        _applicationManager = applicationManager;
        _processSettings = processSettings;
        _agentSettings = agentSettings;
    }
    
    public async Task<ProcessSpawnResult> SpawnProcess(SpawnCommand command, List<PortMapping> ports)
    {
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            _logger.LogInformation(
                "Spawning process for application {AppId} instance {InstanceId}",
                command.ApplicationId, command.InstanceId);
            
            // Create process directory
            var instanceDir = Path.Combine(_agentSettings.Value.WorkingDirectory, command.InstanceId);
            Directory.CreateDirectory(instanceDir);
            
            // Create log files
            var stdoutPath = Path.Combine(instanceDir, "stdout.log");
            var stderrPath = Path.Combine(instanceDir, "stderr.log");
            
            // Build environment variables
            var envVars = BuildEnvironmentVariables(command, ports, instanceDir);
            
            // Build command line arguments
            var arguments = BuildCommandLineArguments(command, ports);
            
            // Create process start info
            var startInfo = new ProcessStartInfo
            {
                FileName = command.ExecutablePath,
                Arguments = arguments,
                WorkingDirectory = GetWorkingDirectory(command),
                UseShellExecute = _processSettings.Value.UseShellExecute,
                RedirectStandardOutput = _processSettings.Value.RedirectOutput,
                RedirectStandardError = _processSettings.Value.RedirectOutput,
                CreateNoWindow = _processSettings.Value.CreateNoWindow,
                ErrorDialog = false
            };
            
            // Add environment variables
            foreach (var envVar in envVars)
            {
                startInfo.EnvironmentVariables[envVar.Key] = envVar.Value;
            }
            
            // Add Watchdog-specific variables
            startInfo.EnvironmentVariables["WATCHDOG_INSTANCE_ID"] = command.InstanceId;
            startInfo.EnvironmentVariables["WATCHDOG_AGENT_ID"] = _agentSettings.Value.AgentId;
            startInfo.EnvironmentVariables["WATCHDOG_APP_ID"] = command.ApplicationId;
            startInfo.EnvironmentVariables["WATCHDOG_INSTANCE_INDEX"] = command.InstanceIndex.ToString();
            
            var process = new Process { StartInfo = startInfo };
            
            // Setup output redirection if enabled
            if (_processSettings.Value.RedirectOutput)
            {
                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();
                
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.AppendLine(e.Data);
                        if (outputBuilder.Length > _processSettings.Value.MaxOutputBufferSize)
                        {
                            outputBuilder.Remove(0, outputBuilder.Length - _processSettings.Value.MaxOutputBufferSize / 2);
                        }
                        
                        // Write to log file
                        if (_processSettings.Value.CaptureOutput)
                        {
                            try
                            {
                                File.AppendAllText(stdoutPath, e.Data + Environment.NewLine);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to write to stdout log file");
                            }
                        }
                    }
                };
                
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                        if (errorBuilder.Length > _processSettings.Value.MaxOutputBufferSize)
                        {
                            errorBuilder.Remove(0, errorBuilder.Length - _processSettings.Value.MaxOutputBufferSize / 2);
                        }
                        
                        // Write to log file
                        if (_processSettings.Value.CaptureOutput)
                        {
                            try
                            {
                                File.AppendAllText(stderrPath, e.Data + Environment.NewLine);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to write to stderr log file");
                            }
                        }
                    }
                };
            }
            
            // Start process
            if (!process.Start())
            {
                throw new Exception("Process.Start() returned false");
            }
            
            // Begin async output reading
            if (_processSettings.Value.RedirectOutput)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            
            // Wait for process to initialize
            var startupTimeout = TimeSpan.FromSeconds(_processSettings.Value.ProcessStartTimeoutSeconds);
            var startupTask = Task.Run(async () =>
            {
                while (!process.HasExited && stopwatch.Elapsed < startupTimeout)
                {
                    await Task.Delay(100);
                }
            });
            
            await startupTask;
            
            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                var errorOutput = GetProcessErrorOutput(process);
                
                throw new Exception($"Process exited during startup with code {exitCode}. Error: {errorOutput}");
            }
            
            // Register managed process
            var managedProcess = new ManagedProcess
            {
                Process = process,
                InstanceId = command.InstanceId,
                ApplicationId = command.ApplicationId,
                Ports = ports,
                StartTime = DateTime.UtcNow,
                Command = command,
                OutputBuffer = new StringBuilder(),
                ErrorBuffer = new StringBuilder(),
                StdOutPath = stdoutPath,
                StdErrPath = stderrPath
            };
            
            _managedProcesses[command.InstanceId] = managedProcess;
            _processIdToInstanceId[process.Id] = command.InstanceId;
            
            // Start background monitoring
            _ = Task.Run(() => MonitorProcess(managedProcess));
            
            stopwatch.Stop();
            
            _logger.LogInformation(
                "Successfully spawned process {ProcessId} for instance {InstanceId} in {ElapsedMs}ms",
                process.Id, command.InstanceId, stopwatch.ElapsedMilliseconds);
            
            return ProcessSpawnResult.Succeeded(process.Id, ports);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            
            _logger.LogError(ex, 
                "Failed to spawn process for instance {InstanceId} after {ElapsedMs}ms", 
                command.InstanceId, stopwatch.ElapsedMilliseconds);
            
            // Release allocated ports on failure
            if (ports.Any())
            {
                var portNumbers = ports.Select(p => p.ExternalPort).ToList();
                await _networkManager.ReleasePorts(portNumbers);
            }
            
            return ProcessSpawnResult.Failed(ex.Message);
        }
    }
    
    public async Task<bool> KillProcess(string instanceId, bool force = false, int timeoutSeconds = 30)
    {
        try
        {
            if (!_managedProcesses.TryGetValue(instanceId, out var managedProcess))
            {
                _logger.LogWarning("Process not found for instance {InstanceId}, treating as already stopped", instanceId);
                await _applicationManager.UpdateInstanceStatus(instanceId, Enums.ApplicationStatus.Stopped);
                return true;
            }
            
            _logger.LogInformation(
                "Killing process {ProcessId} for instance {InstanceId} (Force: {Force})",
                managedProcess.Process.Id, instanceId, force);
            
            // Update application manager
            await _applicationManager.UpdateInstanceStatus(instanceId, Enums.ApplicationStatus.Stopping);
            
            var process = managedProcess.Process;
            
            if (!process.HasExited)
            {
                if (!force)
                {
                    // Try graceful shutdown
                    try
                    {
                        if (process.CloseMainWindow())
                        {
                            // Wait for graceful shutdown
                            var gracefulTimeout = TimeSpan.FromSeconds(timeoutSeconds);
                            var gracefulTask = Task.Run(() => 
                                process.WaitForExit((int)gracefulTimeout.TotalMilliseconds));
                            
                            if (await gracefulTask)
                            {
                                _logger.LogDebug("Process {ProcessId} exited gracefully", process.Id);
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Graceful shutdown timed out for instance {InstanceId}, forcing kill",
                                    instanceId);
                                force = true;
                            }
                        }
                        else
                        {
                            _logger.LogDebug("CloseMainWindow returned false, forcing kill");
                            force = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error during graceful shutdown, forcing kill");
                        force = true;
                    }
                }
                
                if (force || !process.HasExited)
                {
                    // Force kill
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        await Task.WhenAny(
                            process.WaitForExitAsync(),
                            Task.Delay(TimeSpan.FromSeconds(5)));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error killing process {ProcessId}", process.Id);
                    }
                }
            }
            
            // Clean up resources
            await CleanupProcessResources(managedProcess);
            
            _logger.LogInformation(
                "Successfully killed process for instance {InstanceId}", instanceId);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to kill process for instance {InstanceId}", instanceId);
            return false;
        }
    }
    
    public async Task<bool> RestartProcess(string instanceId, int timeoutSeconds = 30)
    {
        try
        {
            _logger.LogInformation("Restarting process for instance {InstanceId}", instanceId);
            
            // Get the managed process
            if (!_managedProcesses.TryGetValue(instanceId, out var managedProcess))
            {
                _logger.LogWarning("Process not found for instance {InstanceId}", instanceId);
                return false;
            }
            
            // Kill the process
            await KillProcess(instanceId, false, timeoutSeconds);
            
            // Wait a bit
            await Task.Delay(2000);
            
            // Get the original command and ports
            var command = managedProcess.Command;
            var ports = managedProcess.Ports;
            
            if (command == null)
            {
                _logger.LogError("Original command not found for instance {InstanceId}", instanceId);
                return false;
            }
            
            // Spawn new process
            var result = await SpawnProcess(command, ports);
            
            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart process for instance {InstanceId}", instanceId);
            return false;
        }
    }
    
    public async Task<ProcessInfo?> GetProcessInfo(string instanceId)
    {
        if (!_managedProcesses.TryGetValue(instanceId, out var managedProcess))
            return null;
        
        return await BuildProcessInfo(managedProcess);
    }
    
    public async Task<List<ProcessInfo>> GetAllProcesses()
    {
        var processInfos = new List<ProcessInfo>();
        
        foreach (var managedProcess in _managedProcesses.Values)
        {
            var info = await BuildProcessInfo(managedProcess);
            if (info != null)
                processInfos.Add(info);
        }
        
        return processInfos;
    }
    
    public Task<bool> IsProcessRunning(string instanceId)
    {
        if (!_managedProcesses.TryGetValue(instanceId, out var managedProcess))
            return Task.FromResult(false);
        
        try
        {
            return Task.FromResult(!managedProcess.Process.HasExited);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
    
    public async Task<ProcessMetrics?> GetProcessMetrics(string instanceId)
    {
        if (!_managedProcesses.TryGetValue(instanceId, out var managedProcess))
            return null;
        
        try
        {
            var process = managedProcess.Process;
            
            if (process.HasExited)
                return null;
            
            var metrics = new ProcessMetrics
            {
                CpuPercent = await GetCpuUsage(process),
                MemoryMB = process.WorkingSet64 / 1024.0 / 1024.0,
                ThreadCount = process.Threads.Count,
                HandleCount = process.HandleCount,
                IoReadBytes = await GetIoReadBytes(process),
                IoWriteBytes = await GetIoWriteBytes(process),
                CollectedAt = DateTime.UtcNow
            };
            
            return metrics;
        }
        catch
        {
            return null;
        }
    }
    
    public async Task ReattachProcesses()
    {
        try
        {
            var instances = await _applicationManager.GetAllInstances();
            foreach (var instance in instances)
            {
                if (instance.Status != Enums.ApplicationStatus.Running ||
                    !instance.ProcessId.HasValue)
                {
                    continue;
                }
                
                if (_managedProcesses.ContainsKey(instance.InstanceId))
                {
                    continue;
                }
                
                Process process;
                try
                {
                    process = Process.GetProcessById(instance.ProcessId.Value);
                    if (process.HasExited)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }
                
                var managedProcess = new ManagedProcess
                {
                    Process = process,
                    InstanceId = instance.InstanceId,
                    ApplicationId = instance.ApplicationId,
                    Command = instance.Command,
                    Ports = instance.Ports ?? new List<PortMapping>(),
                    StartTime = instance.StartedAt ?? DateTime.UtcNow,
                    StdOutPath = string.Empty,
                    StdErrPath = string.Empty
                };
                
                _managedProcesses[instance.InstanceId] = managedProcess;
                _processIdToInstanceId[process.Id] = instance.InstanceId;
                
                _logger.LogInformation(
                    "Reattached to existing process {ProcessId} for instance {InstanceId}",
                    process.Id, instance.InstanceId);
                
                _ = Task.Run(() => MonitorProcess(managedProcess));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reattaching to existing processes");
        }
    }
    
    private Dictionary<string, string> BuildEnvironmentVariables(
        SpawnCommand command, 
        List<PortMapping> ports, 
        string instanceDir)
    {
        var envVars = new Dictionary<string, string>();
        
        // Add command environment variables
        foreach (var envVar in command.EnvironmentVariables)
        {
            envVars[envVar.Key] = envVar.Value;
        }
        
        // Add port mappings
        for (int i = 0; i < ports.Count; i++)
        {
            var port = ports[i];
            envVars[$"PORT_{i}"] = port.ExternalPort.ToString();
            
            if (!string.IsNullOrEmpty(port.Name))
            {
                envVars[$"PORT_{port.Name.ToUpper()}"] = port.ExternalPort.ToString();
            }
        }
        
        // Add all ports as comma-separated list
        if (ports.Any())
        {
            envVars["PORTS"] = string.Join(",", ports.Select(p => p.ExternalPort));
            envVars["PORT_LIST"] = string.Join(";", ports.Select(p => $"{p.Name}:{p.ExternalPort}"));
        }
        
        // Add directory paths
        envVars["WATCHDOG_INSTANCE_DIR"] = instanceDir;
        envVars["WATCHDOG_LOG_DIR"] = instanceDir;
        
        // Add host information
        envVars["COMPUTERNAME"] = Environment.MachineName;
        envVars["USERNAME"] = Environment.UserName;
        envVars["USERDOMAIN"] = Environment.UserDomainName;
        
        return envVars;
    }
    
    private string BuildCommandLineArguments(SpawnCommand command, List<PortMapping> ports)
    {
        var arguments = command.Arguments;
        
        // Replace port placeholders
        if (ports.Any())
        {
            for (int i = 0; i < ports.Count; i++)
            {
                var port = ports[i];
                arguments = arguments.Replace($"${{PORT_{i}}}", port.ExternalPort.ToString());
                
                if (!string.IsNullOrEmpty(port.Name))
                {
                    arguments = arguments.Replace($"${{PORT_{port.Name}}}", port.ExternalPort.ToString());
                    arguments = arguments.Replace($"${{PORT_{port.Name.ToUpper()}}}", port.ExternalPort.ToString());
                }
            }
        }
        
        // Replace other placeholders
        arguments = arguments.Replace("${INSTANCE_ID}", command.InstanceId);
        arguments = arguments.Replace("${AGENT_ID}", _agentSettings.Value.AgentId);
        arguments = arguments.Replace("${APP_ID}", command.ApplicationId);
        arguments = arguments.Replace("${INSTANCE_INDEX}", command.InstanceIndex.ToString());
        
        return arguments;
    }
    
    private string GetWorkingDirectory(SpawnCommand command)
    {
        if (!string.IsNullOrEmpty(command.WorkingDirectory) && 
            Directory.Exists(command.WorkingDirectory))
        {
            return command.WorkingDirectory;
        }
        
        // Fallback to executable directory
        var exeDir = Path.GetDirectoryName(command.ExecutablePath);
        if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir))
        {
            return exeDir;
        }
        
        // Fallback to current directory
        return Directory.GetCurrentDirectory();
    }
    
    private async Task MonitorProcess(ManagedProcess managedProcess)
    {
        try
        {
            // Wait for process exit
            await managedProcess.Process.WaitForExitAsync();
            
            var exitCode = managedProcess.Process.ExitCode;
            
            _logger.LogInformation(
                "Process {ProcessId} for instance {InstanceId} exited with code {ExitCode}",
                managedProcess.Process.Id, managedProcess.InstanceId, exitCode);
            
            // Update application manager
            var status = exitCode == 0 
                ? Enums.ApplicationStatus.Stopped 
                : Enums.ApplicationStatus.Error;
            await _applicationManager.UpdateInstanceStatus(
                managedProcess.InstanceId,
                status);

            if (status == Enums.ApplicationStatus.Stopped)
            {
                await _applicationManager.NotifyInstanceStopped(
                    managedProcess.InstanceId,
                    exitCode,
                    "Process exited normally");
            }

            // Log exit
            await LogProcessExit(managedProcess, exitCode);
            
            // Clean up resources
            await CleanupProcessResources(managedProcess);
            
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring process {ProcessId}", managedProcess.Process.Id);
            
            // Clean up resources
            await CleanupProcessResources(managedProcess);
        }
    }
    
    private async Task CleanupProcessResources(ManagedProcess managedProcess)
    {
        try
        {
            // Remove from tracking dictionaries
            _managedProcesses.TryRemove(managedProcess.InstanceId, out _);
            _processIdToInstanceId.TryRemove(managedProcess.Process.Id, out _);
            
            // Release ports
            if (managedProcess.Ports.Any())
            {
                var portNumbers = managedProcess.Ports.Select(p => p.ExternalPort).ToList();
                await _networkManager.ReleasePorts(portNumbers);
            }
            
            // Dispose process
            try
            {
                managedProcess.Process.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing process {ProcessId}", managedProcess.Process.Id);
            }
            
            // Close output streams
            try
            {
                managedProcess.Process.CancelOutputRead();
                managedProcess.Process.CancelErrorRead();
            }
            catch
            {
                // Ignore
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up process resources for instance {InstanceId}", 
                managedProcess.InstanceId);
        }
    }
    
    private async Task<ProcessInfo> BuildProcessInfo(ManagedProcess managedProcess)
    {
        try
        {
            var process = managedProcess.Process;
            var metrics = await GetProcessMetrics(managedProcess.InstanceId);
            
            var info = new ProcessInfo
            {
                InstanceId = managedProcess.InstanceId,
                ApplicationId = managedProcess.ApplicationId,
                ProcessId = process.Id,
                Status = process.HasExited ? "Exited" : "Running",
                ExitCode = process.HasExited ? process.ExitCode : null,
                StartTime = managedProcess.StartTime,
                Ports = managedProcess.Ports,
                CommandLine = $"{managedProcess.Command?.ExecutablePath} {managedProcess.Command?.Arguments}",
                WorkingDirectory = process.StartInfo.WorkingDirectory
            };
            
            if (metrics != null)
            {
                info.CpuPercent = metrics.CpuPercent;
                info.MemoryMB = metrics.MemoryMB;
                info.ThreadCount = metrics.ThreadCount;
                info.HandleCount = metrics.HandleCount;
            }
            
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building process info for instance {InstanceId}", 
                managedProcess.InstanceId);
            
            return new ProcessInfo
            {
                InstanceId = managedProcess.InstanceId,
                ApplicationId = managedProcess.ApplicationId,
                Status = "Error",
                ErrorMessage = ex.Message
            };
        }
    }
    
    private async Task<double> GetCpuUsage(Process process)
    {
        try
        {
            var startTime = DateTime.UtcNow;
            var startCpuUsage = process.TotalProcessorTime;
            
            await Task.Delay(250);
            
            var endTime = DateTime.UtcNow;
            var endCpuUsage = process.TotalProcessorTime;
            
            var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
            var totalMsPassed = (endTime - startTime).TotalMilliseconds;
            
            // Calculate CPU percentage
            var cpuUsagePercent = (cpuUsedMs / (Environment.ProcessorCount * totalMsPassed)) * 100;
            
            return Math.Round(cpuUsagePercent, 2);
        }
        catch
        {
            return 0;
        }
    }
    
    private Task<long> GetIoReadBytes(Process process)
    {
        try
        {
            return Task.FromResult((long)process.Id);
            // Note: Getting actual IO stats requires more complex interop
            // For now, return process ID as placeholder
        }
        catch
        {
            return Task.FromResult(0L);
        }
    }
    
    private Task<long> GetIoWriteBytes(Process process)
    {
        try
        {
            return Task.FromResult((long)process.Id);
            // Note: Getting actual IO stats requires more complex interop
            // For now, return process ID as placeholder
        }
        catch
        {
            return Task.FromResult(0L);
        }
    }
    
    private string GetProcessErrorOutput(Process process)
    {
        try
        {
            if (_processSettings.Value.RedirectOutput)
            {
                return process.StandardError.ReadToEnd();
            }
            return string.Empty;
        }
        catch
        {
            return "Unable to read error output";
        }
    }
    
    private async Task LogProcessExit(ManagedProcess managedProcess, int exitCode)
    {
        try
        {
            var logEntry = new
            {
                InstanceId = managedProcess.InstanceId,
                ApplicationId = managedProcess.ApplicationId,
                ProcessId = managedProcess.Process.Id,
                ExitCode = exitCode,
                StartTime = managedProcess.StartTime,
                EndTime = DateTime.UtcNow,
                Duration = DateTime.UtcNow - managedProcess.StartTime,
                CommandLine = $"{managedProcess.Command?.ExecutablePath} {managedProcess.Command?.Arguments}"
            };
            
            var logPath = Path.Combine(_agentSettings.Value.LogDirectory, "process-exits.json");
            var json = JsonSerializer.Serialize(logEntry) + Environment.NewLine;
            
            await File.AppendAllTextAsync(logPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log process exit for instance {InstanceId}", 
                managedProcess.InstanceId);
        }
    }
}

public class ProcessSpawnResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ProcessId { get; set; }
    public List<PortMapping> Ports { get; set; } = new();
    public DateTime StartTime { get; set; }
    
    public static ProcessSpawnResult Succeeded(int processId, List<PortMapping> ports)
    {
        return new ProcessSpawnResult
        {
            Success = true,
            ProcessId = processId,
            Ports = ports,
            StartTime = DateTime.UtcNow
        };
    }
    
    public static ProcessSpawnResult Failed(string errorMessage)
    {
        return new ProcessSpawnResult
        {
            Success = false,
            ErrorMessage = errorMessage,
            StartTime = DateTime.UtcNow
        };
    }
}

public class ManagedProcess
{
    public Process Process { get; set; } = null!;
    public string InstanceId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public SpawnCommand? Command { get; set; }
    public List<PortMapping> Ports { get; set; } = new();
    public DateTime StartTime { get; set; }
    public StringBuilder OutputBuffer { get; set; } = new();
    public StringBuilder ErrorBuffer { get; set; } = new();
    public string StdOutPath { get; set; } = string.Empty;
    public string StdErrPath { get; set; } = string.Empty;
}

public class ProcessInfo
{
    public string InstanceId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryMB { get; set; }
    public int ThreadCount { get; set; }
    public long HandleCount { get; set; }
    public DateTime StartTime { get; set; }
    public List<PortMapping> Ports { get; set; } = new();
    public string CommandLine { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}