// See https://aka.ms/new-console-template for more information

using PEPMonitoring;
using PEPMonitoring.Config;

var grpcAddress = Environment.GetEnvironmentVariable("PEP_MONITORING_GRPC_ADDRESS") ?? "http://localhost:5144";
var serviceId = Environment.GetEnvironmentVariable("WATCHDOG_APP_ID") ?? Environment.GetEnvironmentVariable("PEP_MONITORING_SERVICE_ID") ??  $"testapplication-001";
var intervalSecondsRaw = Environment.GetEnvironmentVariable("PEP_MONITORING_HEARTBEAT_SECONDS") ?? "10";

_ = int.TryParse(intervalSecondsRaw, out var intervalSeconds);
if (intervalSeconds <= 0)
{
    intervalSeconds = 10;
}

var config = new MonitoringConfig
{
    GrpcAddress = grpcAddress,
    MUUID = serviceId,
    ApplicationType = 0
};

using var monitor = new DefaultPEPMonitor(config);

var processId = Environment.ProcessId;
var portStr = Environment.GetEnvironmentVariable("PORT");
int.TryParse(portStr, out var assignedPort);

var ready = await monitor.ReadyAsync(processId: processId, assignedPort: assignedPort);
Console.WriteLine($"Ready result: {ready} (InstanceId: {config.InstanceId}, ProcessId: {processId})");
if (!ready)
{
    Console.WriteLine("Ready failed. Check console error output and Watchdog.Api logs for details.");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var heartbeatOk = await monitor.HeartbeatAsync();
        Console.WriteLine($"Heartbeat result: {heartbeatOk} at {DateTime.UtcNow:O}");
        if (!heartbeatOk)
        {
            Console.WriteLine("Heartbeat failed. Check console error output and Watchdog.Api logs for details.");
        }
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cts.Token);
    }
}
catch (OperationCanceledException)
{
}
finally
{
    var unregistered = await monitor.UnregisterAsync();
    Console.WriteLine($"Unregister result: {unregistered}");
}