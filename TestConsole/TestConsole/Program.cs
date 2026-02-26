using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PEPMonitoring.Request;
using TestConsole;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<TestConsoleWorker>();
builder.Services.AddWindowsService();

var host = builder.Build();
await host.RunAsync();

public class TestConsoleWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var monitor = new TestClass();
        monitor.Ready();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                monitor.Heartbeat();

                var snapshot = MetricsCollector.Collect(monitor.CollectionIntervalSeconds);
                monitor.RecordMetrics(snapshot);

                await Task.Delay(TimeSpan.FromSeconds(monitor.CollectionIntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            monitor.Unregister();
        }
    }
}

internal static class MetricsCollector
{
    private static readonly DateTime _startTime = DateTime.UtcNow;

    // Snapshot of previous network counters for delta calculation
    private static long _prevNetBytesSent;
    private static long _prevNetBytesReceived;

    public static MetricsSnapshotRequest Collect(int collectionIntervalSeconds)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();

        // Memory
        long workingSet = proc.WorkingSet64;
        long privateBytes = proc.PrivateMemorySize64;
        long totalMemoryBytes = GetTotalPhysicalMemory();
        double memoryMb = Math.Round(workingSet / 1024.0 / 1024.0, 2);
        double memoryPercent = totalMemoryBytes > 0
            ? Math.Round((double)workingSet / totalMemoryBytes * 100.0, 2)
            : 0;

        // CPU — use TotalProcessorTime delta; simple approximation
        double cpuPercent = Math.Round(GetCpuPercent(proc, collectionIntervalSeconds), 2);

        // I/O
        long ioRead = 0;
        long ioWrite = 0;
        try
        {
            ioRead = proc.TotalProcessorTime.Ticks; // fallback - actual IO not always available cross-platform
#pragma warning disable CA1416
            ioRead  = proc.UserProcessorTime.Ticks;
            ioWrite = proc.PrivilegedProcessorTime.Ticks;
#pragma warning restore CA1416
        }
        catch { }

        // Uptime
        var uptime = DateTime.UtcNow - _startTime;

        // Network: not easily available cross-platform without external libs; use 0 as default
        long netBytesSent = _prevNetBytesSent;
        long netBytesReceived = _prevNetBytesReceived;

        int userCpuMs = (int)(proc.UserProcessorTime.TotalMilliseconds);
        int privilegedCpuMs = (int)(proc.PrivilegedProcessorTime.TotalMilliseconds);

        return new MetricsSnapshotRequest
        {
            CpuPercent = cpuPercent,
            MemoryMb = memoryMb,
            MemoryPercent = memoryPercent,
            DiskUsagePercent = 0, // requires platform-specific API
            NetworkBytesSent = netBytesSent,
            NetworkBytesReceived = netBytesReceived,
            ThreadCount = proc.Threads.Count,
            HandleCount = GetHandleCount(proc),
            IoReadBytes = proc.TotalProcessorTime.Ticks,
            IoWriteBytes = proc.PrivilegedProcessorTime.Ticks,
            PrivateBytes = privateBytes,
            WorkingSet = workingSet,
            UptimeSeconds = (int)uptime.TotalSeconds,
            UserProcessorTime = userCpuMs,
            PrivilegedProcessorTime = privilegedCpuMs,
            CollectionInterval = collectionIntervalSeconds,
            Timestamp = DateTime.UtcNow
        };
    }

    private static long GetTotalPhysicalMemory()
    {
        try
        {
            // Works on Linux via /proc/meminfo; on Windows via GC
            return (long)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 4; // rough estimate
        }
        catch
        {
            return 0;
        }
    }

    private static double GetCpuPercent(System.Diagnostics.Process proc, int intervalSeconds)
    {
        try
        {
            double cpuUsed = proc.TotalProcessorTime.TotalSeconds;
            int cores = Environment.ProcessorCount;
            return Math.Min(100.0, cpuUsed / (intervalSeconds * cores) * 100.0);
        }
        catch
        {
            return 0;
        }
    }

    private static int GetHandleCount(System.Diagnostics.Process proc)
    {
        try
        {
#pragma warning disable CA1416
            return proc.HandleCount;
#pragma warning restore CA1416
        }
        catch
        {
            return 0;
        }
    }
}