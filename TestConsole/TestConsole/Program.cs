// See https://aka.ms/new-console-template for more information

using PEPMonitoring;
using PEPMonitoring.Config;

var grpcAddress = Environment.GetEnvironmentVariable("PEP_MONITORING_GRPC_ADDRESS") ?? "http://localhost:5144";
var serviceName = Environment.GetEnvironmentVariable("PEP_MONITORING_SERVICE_NAME") ?? "TestConsole";
var serviceId = Environment.GetEnvironmentVariable("WATCHDOG_APP_ID") ?? Environment.GetEnvironmentVariable("PEP_MONITORING_SERVICE_ID") ??  $"{Environment.MachineName}-001";
var instanceId = Environment.GetEnvironmentVariable("WATCHDOG_INSTANCE_ID") ?? Environment.GetEnvironmentVariable("PEP_MONITORING_INSTANCE_ID") ?? $"{Environment.MachineName}-{Guid.NewGuid():N}";
var intervalSecondsRaw = Environment.GetEnvironmentVariable("PEP_MONITORING_HEARTBEAT_SECONDS") ?? "10";

_ = int.TryParse(intervalSecondsRaw, out var intervalSeconds);
if (intervalSeconds <= 0)
{
    intervalSeconds = 10;
}

var config = new MonitoringConfig
{
    GrpcAddress = grpcAddress,
    Name = serviceName,
    MUUID = serviceId,
    InstanceId = instanceId,
    ExpectedHeatbeatInterval = intervalSeconds,
    ExecutablePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty,
    Arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)),
    ApplicationType = 0 // Console application
};

using var monitor = new DefaultPEPMonitor(config);

Console.WriteLine($"Starting monitoring: name='{config.Name}', appId='{config.MUUID}', instanceId='{config.InstanceId}', grpc='{config.GrpcAddress}', useGrpc={config.UseGrpc}");
var registered = await monitor.RegisterAsync();
Console.WriteLine($"Registration result: {registered}");
if (!registered)
{
    Console.WriteLine("Registration failed. Check console error output and Watchdog.Api logs for details.");
}

var processId = Environment.ProcessId;
var assignedPort = 8080; // Single port for this application instance

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